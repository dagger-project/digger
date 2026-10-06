# Sourced by the other scripts: prints this machine's runtime identifier (e.g. linux-x64).
detect_rid() {
  local os arch
  case "$(uname -s)" in
    Linux)  os=linux; if ldd --version 2>&1 | grep -qi musl; then os=linux-musl; fi ;;
    Darwin) os=osx ;;
    *) echo "unsupported OS: $(uname -s)" >&2; exit 1 ;;
  esac
  case "$(uname -m)" in
    x86_64|amd64)  arch=x64 ;;
    aarch64|arm64) arch=arm64 ;;
    *) echo "unsupported architecture: $(uname -m)" >&2; exit 1 ;;
  esac
  echo "$os-$arch"
}
