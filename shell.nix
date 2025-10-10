{ system ? builtins.currentSystem
, pkgs ? import (builtins.fetchTarball {
	url = "https://github.com/NixOS/nixpkgs/archive/a8f575995434695a10b574d35ca51b0f26ae9049.tar.gz"; # commit immediately before .NET 5 was removed
	sha256 = "0glawbqvvvbiz1gn7i1skhaqw96ilgdds6gzq7mj29kfk4vkzng1";
}) { inherit system; }
}:
pkgs.mkShell {
	packages = [ pkgs.dotnet-sdk_5 ];
}
