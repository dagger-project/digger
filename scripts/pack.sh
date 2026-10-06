#!/usr/bin/env bash
# Builds the digger tool packages for publishing into artifacts/package/release:
#
#   Digger.Debugger.<version>.nupkg        pointer package; `dotnet tool install -g Digger.Debugger`
#   Digger.Debugger.<rid>.<version>.nupkg  NativeAOT build, one per RID given (default: this machine's)
#   Digger.Debugger.any.<version>.nupkg    framework-dependent fallback for every other platform
#
#   scripts/pack.sh                       # this machine's RID
#   scripts/pack.sh linux-x64 linux-arm64 # NativeAOT needs a matching OS (arch can cross-compile)
#
# The pointer lists every RID in ToolPackageRuntimeIdentifiers (src/Digger/Digger.csproj), so all of
# them must be packed (on their own OS) and pushed before the pointer package is.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$root/scripts/rid.sh"
rids=("$@")
[[ ${#rids[@]} -eq 0 ]] && rids=("${RID:-$(detect_rid)}")

pack() { dotnet pack "$root/src/Digger" -c Release --nologo -v quiet "$@"; }

pack
for rid in "${rids[@]}"; do
  echo "Packing NativeAOT $rid"
  pack -r "$rid"
done
pack -r any -p:PublishAot=false

ls -1 "$root/artifacts/package/release"
