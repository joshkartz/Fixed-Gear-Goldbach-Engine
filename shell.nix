{ system ? builtins.currentSystem
, pkgs ? import (builtins.fetchTarball {
	url = "https://github.com/NixOS/nixpkgs/archive/43b6e7fada3593e196fd76cea8888acc0bdd955e.tar.gz"; # first commit with (working) .NET 10 RC1
	sha256 = "1w086fx55gj60pp2zf4kmnyqgkxgdrf5fj6winbazz24mdm41zkf";
}) { inherit system; }
}:
pkgs.mkShell {
	packages = [ pkgs.dotnet-sdk_10 ];
}
