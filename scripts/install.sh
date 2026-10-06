#!/usr/bin/env bash
# Packs digger from this checkout and installs it as a global .NET tool, so `digger` runs from
# anywhere (the SDK puts ~/.dotnet/tools on PATH). Re-run it to reinstall after changes.
#
#   scripts/install.sh            # NativeAOT: one native binary + libdbgshim, starts instantly
#   scripts/install.sh --jit      # framework-dependent build (needs the .NET 10 runtime)
#
# Environment: RID (e.g. linux-x64, linux-arm64, osx-arm64).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$root/scripts/rid.sh"
aot=true
[[ "${1:-}" == "--jit" ]] && aot=false

rid="${RID:-$(detect_rid)}"
version="$(dotnet msbuild "$root/src/Digger" -getProperty:Version)"
package="$(dotnet msbuild "$root/src/Digger" -getProperty:PackageId)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "Packing digger $version ($rid, aot=$aot)"
if $aot; then
  # The pointer package lists only this machine's RID, so the install can't pick another one.
  dotnet pack "$root/src/Digger" -c Release -o "$work/feed" -p:ToolPackageRuntimeIdentifiers="$rid" --nologo -v quiet
  dotnet pack "$root/src/Digger" -c Release -o "$work/feed" -r "$rid" -p:ToolPackageRuntimeIdentifiers="$rid" --nologo -v quiet
else
  dotnet pack "$root/src/Digger" -c Release -o "$work/feed" -p:PublishAot=false -p:ToolPackageRuntimeIdentifiers= --nologo -v quiet
fi

# Same version every time: uninstall first, and restore through an empty package cache so a
# previously cached digger $version can't be picked up instead of this build.
dotnet tool uninstall -g "$package" >/dev/null 2>&1 || true
NUGET_PACKAGES="$work/packages" dotnet tool install -g "$package" --version "$version" --add-source "$work/feed"

tools="${DOTNET_CLI_HOME:-$HOME}/.dotnet/tools"
case ":$PATH:" in
  *":$tools:"*) ;;
  *) echo "note: $tools is not on PATH" ;;
esac
