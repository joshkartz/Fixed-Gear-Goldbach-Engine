# Fixed-Gear-Goldbach-Engine
A Constant-Residue Witness Engine in C# using NUMA  

Fixed-Gear Goldbach Verification Engine:
High-throughput verification of the even Goldbach conjecture using a constant-residue witness set ("gear"). Validated at trillion scale with 99.99999%+ coverage.

## Overview
This engine reduces per-even work to O(1) by fixing a small set of witness primes Q (e.g., the first 300 primes) and asking only: "Is n-q prime for some q in Q?"

## Key features:

Segmented architecture with bounded RAM (configurable segment size)  
Two execution modes: segmented sieve (10^9-10^12 scale) and deterministic 64-bit Miller-Rabin (quintillion slices)  
Per-segment checkpointing with resume capability  
Parallel execution with NUMA-aware thread affinity  
Auditable: JSON segment reports + optional miss logging  

Made with .NET 5, changes may or may not need to be done to run on current/later versions.  

# Quick Start
bash# Build
dotnet build -c Release

## Example arguments to verify [4, 1 billion] with K=300 gear

dotnet run -c Release -- --limit 1000000000 --gear 300 --segmentEvens 250000000 --threadsInside 12

## Example arguments to verify Quintillion-scale slice with Miller-Rabin mode

dotnet run -c Release -- --mode mr --startN 1000000000000000000 --windowEvens 1000000000 --gear 300
Results

## Parameters

* --mode          sieve | mr (default: sieve)
* --limit         Max even for sieve mode
* --startN        Starting even for MR slice
* --windowEvens   Number of evens in MR window
* --gear          K (size of witness set, default: 310)
* --segmentEvens  Evens per segment (default: 500M)
* --threadsInside Threads per segment (tune for your CPU)
* --resume        Skip completed segments
* --verifySeams   Enable boundary verification


## 
[4, 10^10]: 100% coverage, K=300, segmented sieve
[4, 10^12]: 99.999999%+ coverage, K=300
Quintillion slices: 100% observed coverage in tested windows
See the paper for full methodology and results.
##
Segmented bitset handles arbitrarily large ranges within CLR array limits
Thread-local buffers with barrier-synchronized merge (race-free)
Deterministic 64-bit Miller-Rabin with small-prime wheel prefilter
Checkpoints: seg_XXXXX.json (stats) + optional seg_XXXXX_misses.txt

<small>Special thanks to Elyndra and A.</small>

## Citation
If you use this engine in your research, please cite:  

Joshua Kratochvil - And this paper:  
License: Apache 2.0 - See LICENSE file  


## Contributing: Extensions welcome! Areas of interest:

* GPU acceleration  
* Distributed/cluster deployment
* Gear optimization strategies
* Edge/segment case improvements
* Alternative primality tests


