using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

// Build: dotnet build -c Release
// Examples:
//   dotnet run -c Release -- --limit 60000000000 --gear 300 --segmentEvens 500000000 --maxConcurrentSegments 2 --threadsInside 12 --resume --mode sieve --verifySeams
//   dotnet run -c Release -- --mode mr --startN 1000000000000000000 --windowEvens 1000000000 --gear 300 --threadsInside 24

namespace ResonanceEngine
{
    // ===========================
    // Affinity / NUMA helpers
    // ===========================
    static class Affinity
    {
#if WINDOWS
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] static extern IntPtr SetThreadAffinityMask(IntPtr hThread, IntPtr dwThreadAffinityMask);
#endif
        public static void SetProcessAffinity(ulong mask)
        {
            try
            {
                var p = Process.GetCurrentProcess();
                p.ProcessorAffinity = (IntPtr)unchecked((long)mask);
                Console.WriteLine($"[affinity] process mask=0x{mask:X}");
            }
            catch (Exception e) { Console.WriteLine($"[affinity] warn: {e.Message}"); }
        }
        public static IDisposable PinThisThread(ulong mask)
        {
#if WINDOWS
            IntPtr h = GetCurrentThread();
            IntPtr old = SetThreadAffinityMask(h, (IntPtr)unchecked((long)mask));
            return new RestoreAffinity(h, old);
#else
            return new Noop();
#endif
        }
        private sealed class Noop : IDisposable { public void Dispose() { } }
#if WINDOWS
        private sealed class RestoreAffinity : IDisposable
        {
            private readonly IntPtr h; private readonly IntPtr old;
            public RestoreAffinity(IntPtr h, IntPtr old) { this.h = h; this.old = old; }
            public void Dispose() { if (old != IntPtr.Zero) SetThreadAffinityMask(h, old); }
        }
#endif
    }

    // ===========================
    // Segmented even bitset (race-proof merge)
    // ===========================
    public class EvenBitset
    {
        public class Segment { public long EvensHere; public ulong[] Data; }
        public int SegmentWordsAt(int segIndex) => segments[segIndex].Data.Length;

        private readonly List<Segment> segments;
        private readonly long segmentEvens;
        public long SegmentEvens => segmentEvens;
        public int SegmentCount => segments.Count;
        public EvenBitset(long totalSlots, long requestedSegmentEvens)
        {
            long maxSafeEvens = (long)int.MaxValue * 64L;   // fits int word index
            long humanCap = 2_000_000_000L;                 // sanity cap
            segmentEvens = Math.Min(Math.Max(1, requestedSegmentEvens),
                                    Math.Min(maxSafeEvens, humanCap));

            long segCount = (totalSlots + segmentEvens - 1) / segmentEvens;
            if (segCount > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(totalSlots));

            segments = new List<Segment>((int)segCount);
            for (long i = 0; i < segCount; i++)
            {
                long evensHere = Math.Min(segmentEvens, totalSlots - i * segmentEvens);
                long words = (evensHere + 63) / 64;
                if (words > int.MaxValue)
                    throw new InvalidOperationException("Segment too large for CLR array.");

                segments.Add(new Segment
                {
                    EvensHere = evensHere,
                    Data = new ulong[(int)words]
                });
            }
        }

        public void Set(long nEven)
        {
            long idx = (nEven >> 1)- 1;          // old: n=2 -> idx=0. choose one style throughout
            if (idx < 0) return;
            long segIdx = idx / segmentEvens;
            if ((uint)segIdx >= (uint)segments.Count) return;

            var seg = segments[(int)segIdx];
            long localIdx = idx - segIdx * segmentEvens;
            if ((ulong)localIdx >= (ulong)seg.EvensHere) return;

            int word = (int)(localIdx >> 6);
            int bit = (int)(localIdx & 63);
            seg.Data[word] |= (1UL << bit);
        }

        public bool Get(long nEven)
        {
            long idx = (nEven >> 1) - 1;          // n=2 -> idx=0 choose one style throughout
            if (idx < 0) return false;
            long segIdx = idx / segmentEvens;
            if ((uint)segIdx >= (uint)segments.Count) return false;

            var seg = segments[(int)segIdx];
            long localIdx = idx - segIdx * segmentEvens;
            if ((ulong)localIdx >= (ulong)seg.EvensHere) return false;

            int word = (int)(localIdx >> 6);
            int bit = (int)(localIdx & 63);
            return (seg.Data[word] & (1UL << bit)) != 0;
        }

        // New: expose segment word count and a merge API
        public int SegmentWords => segments[0].Data.Length;
        public void MergeSegment(int segIndex, ulong[] local)
        {
            var dst = segments[segIndex].Data;
            for (int i = 0; i < dst.Length; i++) dst[i] |= local[i];
        }

        public void LogStats()
        {
            Console.WriteLine($"[Bitset] Segments={segments.Count}, segmentEvens={segmentEvens:N0}");
            for (int i = 0; i < segments.Count; i++)
                Console.WriteLine($"  Segment {i}: evensHere={segments[i].EvensHere:N0}, words={segments[i].Data.Length:N0}");
        }
    }

    // ===========================
    // Sieve utilities (base primes & segmented)
    // ===========================
    static class Sieve
    {
        public static List<int> SimpleSieve(int limit)
        {
            bool[] isPrime = new bool[limit + 1];
            for (int i = 0; i <= limit; ++i) isPrime[i] = true;
            if (limit >= 0) isPrime[0] = false;
            if (limit >= 1) isPrime[1] = false;

            int r = (int)Math.Sqrt(limit);
            for (int p = 2; p <= r; ++p)
            {
                if (!isPrime[p]) continue;
                long start = (long)p * p;
                for (long x = start; x <= limit; x += p) isPrime[(int)x] = false;
            }
            var primes = new List<int>();
            for (int i = 2; i <= limit; ++i) if (isPrime[i]) primes.Add(i);
            return primes;
        }

        public static IEnumerable<long> SegmentedPrimes(long low, long high, List<int> basePrimes, int segmentSize = 32_000_000)
        {
            for (long segLo = low; segLo <= high; segLo += segmentSize)
            {
                long segHi = Math.Min(segLo + segmentSize - 1, high);
                int len = (int)(segHi - segLo + 1);
                var mark = new byte[len];
                for (int i = 0; i < len; i++) mark[i] = 1;

                foreach (int p in basePrimes)
                {
                    long start = Math.Max((long)p * p, ((segLo + p - 1) / p) * p);
                    for (long x = start; x <= segHi; x += p) mark[(int)(x - segLo)] = 0;
                }
                for (int i = 0; i < len; i++) if (mark[i] != 0) yield return segLo + i;
            }
        }

        public static List<int> FirstKPrimes(int k)
        {
            var gear = new List<int>(k);
            foreach (var p in SegmentedPrimes(2, 5000, SimpleSieve(1000), 4096))
            {
                gear.Add((int)p);
                if (gear.Count >= k) break;
            }
            return gear;
        }
    }

    // ===========================
    // Deterministic MR for 64-bit
    // ===========================
    static class MR64
    {
        // Small prime prefilter (wheel) – must be public for caller
        public static readonly int[] SmallPrimes = { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53 };
        static readonly ulong[] Bases = { 2, 3, 5, 7, 11, 13, 17 };

        static ulong MulMod(ulong a, ulong b, ulong m) => (ulong)((BigInteger)a * b % m);
        static ulong PowMod(ulong a, ulong e, ulong m) => (ulong)BigInteger.ModPow(a, e, m);

        public static bool IsPrime(ulong n)
        {
            if (n < 2) return false;
            foreach (var p in SmallPrimes)
            {
                if ((ulong)p == n) return true;
                if (n % (ulong)p == 0) return false;
            }
            ulong d = n - 1, s = 0;
            while ((d & 1UL) == 0) { d >>= 1; s++; }
            foreach (var a in Bases)
            {
                if (a % n == 0) continue;
                ulong x = PowMod(a, d, n);
                if (x == 1 || x == n - 1) continue;
                bool cont = false;
                for (ulong r = 1; r < s; r++)
                {
                    x = MulMod(x, x, n);
                    if (x == n - 1) { cont = true; break; }
                }
                if (!cont) return false;
            }
            return true;
        }
    }

    // ===========================
    // Program
    // ===========================
    partial class Program
    {
        // Defaults
        static string MODE = "sieve";        // sieve | mr
        static long LIMIT = 1_000_000_000_000;  // for sieve mode (max even)
        static long START_N = 0;             // for slice/window
        static long WINDOW_EVENS = 0;        // if >0, process only this window
        static int GEAR_K = 310;
        static int THREADS = Environment.ProcessorCount;
        static long SEGMENT_EVENS = 500_000_000;
        static int MAX_CONCURRENT_SEGMENTS = 12;
        static int THREADS_INSIDE = Math.Max(1, Environment.ProcessorCount / 2);
        static int MISS_SAMPLE = 0;
        static bool RESUME = false;
        static bool VERIFY_SEAMS = true;
        static ulong AFFINITY_MASK = 0;      // 0 = don’t set

        const int PAD = 8; // p-interval padding to kill fenceposts

        record SegmentReport(int Index, long NStart, long NEnd, long Covered, long TotalEvens, double Pct, double Seconds);

        [JsonSerializable(typeof(SegmentReport))]
        [JsonSourceGenerationOptions(WriteIndented = true)]
        partial class SourceGenerationContext : JsonSerializerContext;

        static void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; ++i)
            {
                string a = args[i];
                string Next() => (i + 1 < args.Length) ? args[++i] : throw new ArgumentException($"Missing value after {a}");
                switch (a)
                {
                    case "--mode": MODE = Next(); break;
                    case "--limit": LIMIT = long.Parse(Next()); break;
                    case "--startN": START_N = long.Parse(Next()); break;
                    case "--windowEvens": WINDOW_EVENS = long.Parse(Next()); break;
                    case "--gear": GEAR_K = int.Parse(Next()); break;
                    case "--threads": THREADS = int.Parse(Next()); break;
                    case "--segmentEvens": SEGMENT_EVENS = long.Parse(Next()); break;
                    case "--maxConcurrentSegments": MAX_CONCURRENT_SEGMENTS = int.Parse(Next()); break;
                    case "--threadsInside": THREADS_INSIDE = int.Parse(Next()); break;
                    case "--misses": MISS_SAMPLE = int.Parse(Next()); break;
                    case "--resume": RESUME = true; break;
                    case "--verifySeams": VERIFY_SEAMS = true; break;
                    case "--affinityMask":
                        string v = Next();
                        AFFINITY_MASK = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt64(v, 16) : ulong.Parse(v);
                        break;
                }
            }
        }

        static bool HasWitnessMR(long n, int[] gearOdd)
        {
            foreach (var q in gearOdd)
            {
                ulong p = (ulong)(n - q);
                if (p <= 1) continue;
                // quick wheel
                bool bad = false;
                foreach (var sp in MR64.SmallPrimes)
                {
                    if ((ulong)sp == p) return true;
                    if (p % (ulong)sp == 0) { bad = true; break; }
                }
                if (bad) continue;
                if (MR64.IsPrime(p)) return true;
            }
            return false;
        }

        static SegmentReport ProcessSegment_Sieve(
    int segIndex, EvenBitset coverage, long segmentEvens, long limit, IReadOnlyList<int> gearAll,
    int baseSieveSize = 32_000_000, int threadsInside = 12, int missSample = 0, ulong threadMask = 0)
        {
            using var _ = (threadMask != 0) ? Affinity.PinThisThread(threadMask) : null;
            var sw = Stopwatch.StartNew();

            // Rebased slot math
            long totalSlots = limit / 2; // evens in [2..limit]
            long idxStart = segIndex * segmentEvens;
            long idxEnd = Math.Min(idxStart + segmentEvens, totalSlots) - 1;
            if (idxStart > idxEnd) return new SegmentReport(segIndex, 0, 0, 0, 0, 100.0, 0);

            long nStart = (idxStart + 1) << 1;  // NOT idxStart << 1
            long nEnd = idxEnd << 1;
            if (segIndex == 0 && nStart < 2) nStart = 2;  // still clamp for safety


            // Gear (odd only)
            var gear = gearAll.Where(q => (q & 1) == 1).ToList();
            if (gear.Count == 0) throw new InvalidOperationException("Empty gear.");
            int qMinOdd = gear[0];
            int qMax = gear[^1];

            // Slot-overlap: expand slot window a bit on both sides
            // Use overlap proportional to qMax so it scales with gear size.
            long overlapSlots = Math.Max(1024, 2L * qMax);  // tune: >= 2*qMax is bulletproof
            long idxStartX = Math.Max(0, idxStart - overlapSlots);
            long idxEndX = Math.Min(totalSlots - 1, idxEnd + overlapSlots);

            // Build p-window from the EXPANDED evens window, not just the true one
            long nStartX = (idxStartX + 1) << 1;
            long nEndX = (idxEndX + 1) << 1;

            long pLo = Math.Max(2, nStartX - qMax);
            long pHi = Math.Max(2, nEndX - qMinOdd);

            // Base primes and segmented sieve for [pLo..pHi]
            int root = (pHi >= 4) ? (int)Math.Sqrt(pHi) + 1 : 3;
            var basePrimes = Sieve.SimpleSieve(root);
            var segPrimes = Sieve.SegmentedPrimes(pLo, pHi, basePrimes, baseSieveSize).ToList();

            // Thread-local buffers sized to the EXACT word count for this segment
            int words = coverage.SegmentWordsAt(segIndex);  // not global!
            var locals = new ulong[threadsInside][];
            for (int t = 0; t < threadsInside; t++)
                locals[t] = new ulong[words];

            // fill locals in parallel
            Parallel.For(0, threadsInside,
                new ParallelOptions { MaxDegreeOfParallelism = threadsInside },
                t =>
                {
                    using var __ = (threadMask != 0) ? Affinity.PinThisThread(threadMask) : null;

                    var local = locals[t];

        // static partition of segPrimes indices
        int start = (int)((long)t * segPrimes.Count / threadsInside);
                    int end = (int)((long)(t + 1) * segPrimes.Count / threadsInside);

                    for (int i = start; i < end; i++)
                    {
                        long p = segPrimes[i];
                        foreach (int q in gear)
                        {
                            long n = p + q;
                            if ((n & 1L) != 0) continue;  // even only

                            long idx = n >> 1;            // global slot index
                            long localIdx = idx - idxStart; // offset within this segment

                            if ((ulong)localIdx < (ulong)(coverage.SegmentWordsAt(segIndex) * 64))
                            {
                                int w = (int)(localIdx >> 6);
                                int b = (int)(localIdx & 63);
                                local[w] |= 1UL << b;
                            }
                        }
                    }
                });

            // === barrier has passed; now we can merge ===
            for (int t = 0; t < threadsInside; t++)
            {
                var src = locals[t];
                // sanity: ensure lengths match this segment’s words
                if (src.Length != words)
                    throw new InvalidOperationException($"local[{t}] words {src.Length} != seg words {words}");
                coverage.MergeSegment(segIndex, src);
            }

            if (VERIFY_SEAMS)
            {
                var gearOddArr = gear.ToArray();

                long seamLoEnd = Math.Min(nStart + 200, nEnd);
                for (long n = nStart; n <= seamLoEnd; n += 2)
                {
                    if (n <= 4) continue;  // skip trivials
                    if (!coverage.Get(n) && HasWitnessMR(n, gearOddArr))
                        Console.WriteLine($"[seam-low] should-be-covered n={n}");
                }

                long seamHiStart = Math.Max(nStart, nEnd - 198);
                for (long n = seamHiStart; n <= nEnd; n += 2)
                {
                    if (n <= 4) continue;
                    if (!coverage.Get(n) && HasWitnessMR(n, gearOddArr))
                        Console.WriteLine($"[seam-high] should-be-covered n={n}");
                }
            }



            // coverage stats
            long effectiveNStart = (segIndex == 0 ? Math.Max(nStart, 6) : nStart);
            if (effectiveNStart > nEnd)
            {
                // empty segment after excluding trivials
                var repEmpty = new SegmentReport(segIndex, nStart, nEnd, 0, 0, 100.0, sw.Elapsed.TotalSeconds);
                File.WriteAllText($"seg_{segIndex:D5}.json",
                    JsonSerializer.Serialize(repEmpty, SourceGenerationContext.Default.SegmentReport));
                return repEmpty;
            }

            long totalEvens = ((nEnd - effectiveNStart) >> 1) + 1;

            long covered = 0;
            List<long>? missesOut = missSample > 0 ? new List<long>(missSample) : null;

            for (long n = effectiveNStart; n <= nEnd; n += 2) // <-- start from effectiveNStart
            {
                if (coverage.Get(n)) covered++;
                else if (missesOut != null && missesOut.Count < missSample) missesOut.Add(n);
            }

            double sec = sw.Elapsed.TotalSeconds;
            var rep = new SegmentReport(segIndex, nStart, nEnd, covered, totalEvens, 100.0 * covered / totalEvens, sec);
            File.WriteAllText($"seg_{segIndex:D5}.json", JsonSerializer.Serialize(rep, SourceGenerationContext.Default.SegmentReport));
            if (missesOut is not null && missesOut.Count > 0)
                File.WriteAllLines($"seg_{segIndex:D5}_misses.txt", missesOut.ConvertAll(x => x.ToString()));
            return rep;
        }

        static SegmentReport ProcessWindow_MR(
            long nStart, long windowEvens, IReadOnlyList<int> gearAll,
            int threadsInside, int missSample, ulong threadMask)
        {
            using var _ = (threadMask != 0) ? Affinity.PinThisThread(threadMask) : null;
            var sw = Stopwatch.StartNew();

            if (windowEvens <= 0) throw new ArgumentOutOfRangeException(nameof(windowEvens));

            long nEnd = nStart + 2 * (windowEvens - 1);
            long covered = 0;
            List<long>? missesOut = missSample > 0 ? new List<long>(missSample) : null;

            var gear = gearAll.Where(q => (q & 1) == 1).ToArray();  // odd gear only

            Parallel.For(0L, windowEvens, new ParallelOptions { MaxDegreeOfParallelism = threadsInside }, i =>
            {
                long n = nStart + (i << 1);
                bool hit = false;

                foreach (int q in gear)
                {
                    ulong p = (ulong)(n - q);
                    if (p <= 1) continue;

                    // wheel prefilter
                    bool composite = false;
                    foreach (var sp in MR64.SmallPrimes)
                    {
                        if ((ulong)sp == p) { hit = true; break; }
                        if (p % (ulong)sp == 0) { composite = true; break; }
                    }
                    if (hit) break;
                    if (composite) continue;

                    if (MR64.IsPrime(p)) { hit = true; break; }
                }

                if (hit) Interlocked.Increment(ref covered);
                else if (missesOut != null)
                {
                    lock (missesOut) if (missesOut.Count < missSample) missesOut.Add(n);
                }
            });

            double sec = sw.Elapsed.TotalSeconds;
            var rep = new SegmentReport(0, nStart, nEnd, covered, windowEvens, 100.0 * covered / windowEvens, sec);
            File.WriteAllText($"window_{nStart}_{windowEvens}.json", JsonSerializer.Serialize(rep, SourceGenerationContext.Default.SegmentReport));
            if (missesOut is not null && missesOut.Count > 0)
                File.WriteAllLines($"window_{nStart}_{windowEvens}_misses.txt", missesOut.ConvertAll(x => x.ToString()));
            return rep;
        }


        static void Main(string[] args)
        {
            ParseArgs(args);
            if (AFFINITY_MASK != 0) Affinity.SetProcessAffinity(AFFINITY_MASK);
            ThreadPool.SetMinThreads(Math.Max(THREADS, 16), Math.Max(THREADS, 16));
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

            var gearAll = Sieve.FirstKPrimes(GEAR_K);
            Console.WriteLine($"[cfg] mode={MODE} gear={GEAR_K} threads={THREADS} threadsInside={THREADS_INSIDE} maxSeg={MAX_CONCURRENT_SEGMENTS}");
            Console.WriteLine($"[cfg] segmentEvens={SEGMENT_EVENS:N0} resume={RESUME} verifySeams={VERIFY_SEAMS} affinity={(AFFINITY_MASK != 0 ? $"0x{AFFINITY_MASK:X}" : "none")}");
            Console.WriteLine($"[gear] K(all)={gearAll.Count}, max={gearAll[^1]}");

            if (MODE.Equals("mr", StringComparison.OrdinalIgnoreCase))
            {
                if (START_N <= 0 || WINDOW_EVENS <= 0)
                    throw new ArgumentException("--mode mr requires --startN and --windowEvens");
                var rep = ProcessWindow_MR(START_N, WINDOW_EVENS, gearAll, THREADS, MISS_SAMPLE, AFFINITY_MASK);
                Console.WriteLine($"[window] {rep.Covered:N0}/{rep.TotalEvens:N0} = {rep.Pct:F6}% in {rep.Seconds:F2}s");
                return;
            }

            // mode: sieve
            if (LIMIT <= 0) throw new ArgumentException("--limit must be > 0");
            // old: long totalSlots = (LIMIT >> 1) + 1;
            long totalSlots = (LIMIT >> 1) + 1;   // number of evens in [2..LIMIT]
            var coverage = new EvenBitset(totalSlots, SEGMENT_EVENS);
            coverage.LogStats();

            int segCount = (int)((totalSlots + coverage.SegmentEvens - 1) / coverage.SegmentEvens);
            var reports = new SegmentReport[segCount];
            var poSeg = new ParallelOptions { MaxDegreeOfParallelism = MAX_CONCURRENT_SEGMENTS };
            Parallel.For(0, coverage.SegmentCount, poSeg, s =>
            {
                string ck = $"seg_{s:D5}.json";
                if (RESUME && File.Exists(ck))
                {
                    Console.WriteLine($"[seg {s:D5}] resume: checkpoint exists, skipping.");
                    reports[s] = JsonSerializer.Deserialize<SegmentReport>(File.ReadAllText(ck), SourceGenerationContext.Default.SegmentReport);
                    return;
                }
                ulong segMask = AFFINITY_MASK; // optionally pin
                var rep = ProcessSegment_Sieve(
                    s, coverage, coverage.SegmentEvens, LIMIT, gearAll,
                    32_000_000, Math.Max(1, THREADS_INSIDE), MISS_SAMPLE, segMask);

                reports[s] = rep;
                Console.WriteLine($"[seg {s:D5}] {rep.Covered:N0}/{rep.TotalEvens:N0} ({rep.Pct:F6}%) in {rep.Seconds:F1}s");
            });

            long coveredAll = 0, totalAll = 0;
            foreach (var r in reports) { if (r != null) { coveredAll += r.Covered; totalAll += r.TotalEvens; } }
            Console.WriteLine($"[TOTAL] {coveredAll:N0}/{totalAll:N0} = {100.0 * coveredAll / totalAll:F6}%");
        }
    }
}

