#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
tools_root=/workspace/.tools
export DOTNET_ROOT="$tools_root/dotnet"
export DOTNET_CLI_HOME="$tools_root/dotnet-home"
export NUGET_PACKAGES="$tools_root/nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export PATH="$DOTNET_ROOT:$PATH"
sdk_version=10.0.403
sdk_sha512=b7b3d8461cf98b07d2552ce9199702fa3ad010ac2671a5f8cd5136f7534e50f6855ba7bbb02b11494563f1d04156276a31cda1103b900e5eecd0d9473dc7b9c5
if ! command -v dotnet >/dev/null || [[ "$(dotnet --version)" != "$sdk_version" ]]; then
    [[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]] || { echo 'Este script prepara Linux x64; en Windows instala el SDK indicado en global.json.' >&2; exit 1; }
    archive="$(mktemp /tmp/mining-dotnet.XXXXXXXX.tar.gz)"
    trap 'rm -f "$archive"' EXIT
    curl --fail --show-error --location "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdk_version/dotnet-sdk-$sdk_version-linux-x64.tar.gz" -o "$archive"
    printf '%s  %s\n' "$sdk_sha512" "$archive" | sha512sum --check --status
    mkdir -p "$DOTNET_ROOT"
    tar -xzf "$archive" -C "$DOTNET_ROOT"
fi
cd "$repo_root"
dotnet restore Mining.slnx --disable-parallel
dotnet build Mining.slnx -c Release --no-restore -m:1 -p:UseSharedCompilation=false
dotnet run --project tests/Mining.Tests -c Release --no-build
