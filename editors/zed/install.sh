#!/usr/bin/env bash
# Builds the Digger Zed extension and installs it into Zed's extensions directory.
# Requires Rust with the wasm32-wasip2 target:  rustup target add wasm32-wasip2
#
# Alternative without this script: in Zed run "zed: install dev extension" and pick this
# folder (Zed then builds it itself, which also needs rustup).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$here"
cargo build --release --target wasm32-wasip2

case "$(uname -s)" in
  Darwin) zed_data="$HOME/Library/Application Support/Zed" ;;
  *)      zed_data="${XDG_DATA_HOME:-$HOME/.local/share}/zed" ;;
esac

target_dir="${CARGO_TARGET_DIR:-$here/target}"
dest="$zed_data/extensions/installed/digger"
rm -rf "$dest"
mkdir -p "$dest"
cp extension.toml "$dest/"
cp -r debug_adapter_schemas "$dest/"
cp "$target_dir/wasm32-wasip2/release/zed_digger.wasm" "$dest/extension.wasm"
echo "Installed the Digger Zed extension to $dest (restart Zed to load it)."
