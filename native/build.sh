#!/usr/bin/env bash
# Builds libhs_vision.so, the hs_vision C library, for every Linux runtime Home Assistant runs on:
# linux-musl-arm64 and linux-musl-x64 (the Home Assistant container, Alpine) and linux-arm64 and linux-x64
# (glibc 2.31 or later, built on Debian bullseye). Each build runs in Docker from the pinned toolchain, is checked
# (musl: it needs only libc.musl and libgcc_s; glibc: no symbol newer than GLIBC_2.31) and is loaded once
# through ctypes: a musl build in the Home Assistant image it runs in, a glibc build in Debian bullseye.
#
#   native/build.sh [--out DIR] [RID ...]   builds the RIDs given, or all four, into DIR (default native/dist),
#                                           as DIR/libhs_vision-<rid>.so with DIR/SHA256SUMS for them
#   native/build.sh --licenses              rewrites native/THIRD-PARTY-LICENSES and builds nothing
#
# Prerequisites: a Docker engine, and emulation for a RID of another architecture than the engine's (colima:
# colima start --vz-rosetta, or install binfmt). Nothing is installed on the host: the sources go into each
# container as a tar stream and the library comes back on its stdout, with no bind mounts. An emulated build runs
# one job at a time and takes tens of minutes.
#
# native/dist is ignored by git, and binaries are never committed. Only the release workflow publishes them, as
# release assets (.github/workflows/release.yaml). They are rebuilt only when the Rust code changes, under a new
# version; native/SHA256SUMS is a copy of a full run's DIR/SHA256SUMS.
set -euo pipefail

RUST_VERSION=1.99.1
MUSL_IMAGE=rust:1.99.1-alpine
# rust:1.99.1-bullseye does not exist, so rustup installs the toolchain into Debian bullseye (glibc 2.31).
GNU_IMAGE=debian:bullseye
GLIBC_MAX=2.31
HA_IMAGE=homeassistant/home-assistant:2026.11.1
PROBE_IMAGE=alpine:3.22
CARGO_ABOUT_VERSION=0.9.3
ALL_RIDS="linux-musl-arm64 linux-musl-x64 linux-arm64 linux-x64"

here=$(cd "$(dirname "$0")" && pwd)

die() {
  echo "build.sh: $*" >&2
  exit 1
}

usage() {
  echo "usage: native/build.sh [--out DIR] [RID ...] | native/build.sh --licenses" >&2
  echo "RIDs: $ALL_RIDS" >&2
  exit 2
}

# Writes the crate's sources as a tar stream to stdout, without macOS metadata. Extra files from native/ may follow.
sources() {
  if tar --version 2>/dev/null | grep -q bsdtar; then
    COPYFILE_DISABLE=1 tar --no-xattrs --no-mac-metadata -C "$here" -cf - \
      Cargo.toml Cargo.lock rust-toolchain.toml src model "$@"
  else
    tar -C "$here" -cf - Cargo.toml Cargo.lock rust-toolchain.toml src model "$@"
  fi
}

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$@"
  else
    shasum -a 256 "$@"
  fi
}

platform_of() {
  case $1 in
    *-x64) echo linux/amd64 ;;
    *-arm64) echo linux/arm64 ;;
  esac
}

machine_of() {
  case $1 in
    linux/amd64) echo x86_64 ;;
    linux/arm64) echo aarch64 ;;
  esac
}

# Stops unless containers of the platform run, natively or emulated, and sets build_jobs: one when the
# platform is emulated, because QEMU's user emulation (colima's binfmt has QEMU 7.0) crashes gcc now and then when
# several run at once; empty, for cargo's default, when it is native.
check_platform() {
  local platform=$1 machine engine got
  machine=$(machine_of "$platform")
  engine=$(docker info --format '{{.Architecture}}')
  case $engine in arm64) engine=aarch64 ;; amd64) engine=x86_64 ;; esac
  build_jobs=
  [ "$engine" = "$machine" ] && return 0
  build_jobs=1
  got=$(docker run --rm --platform "$platform" "$PROBE_IMAGE" uname -m 2>/dev/null || true)
  [ "$got" = "$machine" ] ||
    die "enable $platform emulation (colima: colima start --vz-rosetta, or install binfmt)"
}

# The container's stdout carries only the library; everything else goes to stderr. The scripts expand in the container.
# shellcheck disable=SC2016
MUSL_BUILD='
exec 3>&1 1>&2
set -eu
apk add --no-cache musl-dev binutils >/dev/null
mkdir /src
tar -xf - -C /src
cd /src
RUSTFLAGS="-C target-feature=-crt-static" cargo build --release --locked --lib
so=target/release/libhs_vision.so
needed=$(readelf -d "$so" | sed -n "s/.*(NEEDED).*\[\(.*\)\]/\1/p")
# A musl build needs libc.musl at least: no NEEDED at all means readelf read nothing, which would pass the loop.
[ -n "$needed" ] || { echo "readelf found no NEEDED entries in $so, so what it needs cannot be checked"; exit 1; }
echo "NEEDED:" $needed
for lib in $needed; do
  case $lib in
    libc.musl-*.so.1 | libgcc_s.so.1) ;;
    *) echo "unexpected NEEDED $lib: a musl build may need only libc.musl-*.so.1 and libgcc_s.so.1"; exit 1 ;;
  esac
done
cat "$so" >&3
'

# shellcheck disable=SC2016
GNU_BUILD='
exec 3>&1 1>&2
set -eu
export DEBIAN_FRONTEND=noninteractive
# bullseye has reached its end of life, and its packages are on archive.debian.org.
printf "deb http://archive.debian.org/debian %s main\n" bullseye bullseye-updates >/etc/apt/sources.list
echo "deb http://archive.debian.org/debian-security bullseye-security main" >>/etc/apt/sources.list
apt-get update -qq
apt-get install -y -qq --no-install-recommends ca-certificates curl gcc libc6-dev binutils python3 >/dev/null
curl --proto "=https" --tlsv1.2 -sSf https://sh.rustup.rs |
  sh -s -- -y -q --profile minimal --default-toolchain "$RUST_VERSION"
. "$HOME/.cargo/env"
mkdir /src
tar -xf - -C /src
cd /src
cargo build --release --locked --lib
so=target/release/libhs_vision.so
needed=$(readelf -d "$so" | sed -n "s/.*(NEEDED).*\[\(.*\)\]/\1/p")
[ -n "$needed" ] || { echo "readelf found no NEEDED entries in $so, so what it needs cannot be checked"; exit 1; }
echo "NEEDED:" $needed
newest=$(objdump -T "$so" | grep -o "GLIBC_[0-9][0-9.]*" | sed "s/^GLIBC_//" | sort -u -V | tail -n 1)
# A glibc build uses versioned glibc symbols: none at all means objdump read nothing, which would pass the comparison.
[ -n "$newest" ] || { echo "objdump found no GLIBC_ symbol versions in $so, so the newest cannot be checked"; exit 1; }
echo "newest glibc symbol version: GLIBC_$newest"
if [ "$(printf "%s\n%s\n" "$newest" "$GLIBC_MAX" | sort -V | tail -n 1)" != "$GLIBC_MAX" ]; then
  echo "GLIBC_$newest is newer than GLIBC_$GLIBC_MAX"
  exit 1
fi
out=$(python3 -c "$SMOKE" "$so")
echo "smoke in $GNU_IMAGE:" $out
[ "$out" = "$(printf "1\nTrue")" ] || { echo "smoke failed"; exit 1; }
cat "$so" >&3
'

SMOKE='import ctypes,sys
l=ctypes.CDLL(sys.argv[1])
l.hs_vision_open.restype=ctypes.c_void_p
print(l.hs_vision_abi_version())
d=l.hs_vision_open()
print(bool(d))
l.hs_vision_close(ctypes.c_void_p(d))'

# Loads a musl build in the Home Assistant image of its platform. Home Assistant publishes both platforms, so a
# failed pull stops the script.
smoke_musl() {
  local platform=$1 so=$2 out
  docker pull -q --platform "$platform" "$HA_IMAGE" >/dev/null ||
    die "could not pull $HA_IMAGE for $platform, so the library cannot be loaded in Home Assistant"
  out=$(docker run -i --rm --platform "$platform" --entrypoint sh -e SMOKE="$SMOKE" "$HA_IMAGE" \
    -c 'cat > /tmp/libhs_vision.so && python3 -c "$SMOKE" /tmp/libhs_vision.so' <"$so")
  echo "smoke in $HA_IMAGE ($platform): $(echo "$out" | tr '\n' ' ')" >&2
  [ "$out" = "$(printf '1\nTrue')" ] || die "smoke failed for $so"
}

build_rid() {
  local rid=$1 platform part file bytes
  platform=$(platform_of "$rid")
  check_platform "$platform"
  file="$out/libhs_vision-$rid.so"
  part="$file.part"
  echo "== $rid ($platform)" >&2
  case $rid in
    linux-musl-*)
      sources | docker run -i --rm --platform "$platform" ${build_jobs:+-e CARGO_BUILD_JOBS="$build_jobs"} \
        "$MUSL_IMAGE" sh -c "$MUSL_BUILD" >"$part"
      smoke_musl "$platform" "$part"
      ;;
    *)
      sources | docker run -i --rm --platform "$platform" ${build_jobs:+-e CARGO_BUILD_JOBS="$build_jobs"} \
        -e RUST_VERSION="$RUST_VERSION" -e GLIBC_MAX="$GLIBC_MAX" -e GNU_IMAGE="$GNU_IMAGE" -e SMOKE="$SMOKE" \
        "$GNU_IMAGE" sh -c "$GNU_BUILD" >"$part"
      ;;
  esac
  mv "$part" "$file"
  bytes=$(wc -c <"$file" | tr -d ' ')
  echo "$(basename "$file"): $bytes bytes ($(awk -v b="$bytes" 'BEGIN { printf "%.2f", b / 1048576 }') MiB)"
}

# Rewrites THIRD-PARTY-LICENSES from cargo-about's report on the linked crates, then the two models' licences.
licenses() {
  local report target="$here/THIRD-PARTY-LICENSES"
  report=$(mktemp)
  # shellcheck disable=SC2064 # the path is fixed now
  trap "rm -f '$report'" EXIT
  sources about.toml about.hbs |
    docker run -i --rm -e CARGO_ABOUT_VERSION="$CARGO_ABOUT_VERSION" "$MUSL_IMAGE" sh -c '
    exec 3>&1 1>&2
    set -eu
    apk add --no-cache musl-dev >/dev/null
    cargo install --locked --quiet --features cli "cargo-about@$CARGO_ABOUT_VERSION"
    mkdir /src
    tar -xf - -C /src
    cd /src
    cargo about generate --locked --fail about.hbs >&3
  ' >"$report" ||
    die "cargo-about failed"
  {
    # A few crates' licence files have CRLF line ends.
    tr -d '\r' <"$report"
    printf '\n================================================================================\n'
    printf 'YuNet (model/face_detection_yunet_2023mar.onnx), embedded in the library: model/LICENSE-yunet\n'
    printf '================================================================================\n\n'
    cat "$here/model/LICENSE-yunet"
    printf '\n================================================================================\n'
    printf 'YOLOX-nano (model/yolox_nano.onnx), embedded in the library: model/LICENSE-yolox\n'
    printf '================================================================================\n\n'
    cat "$here/model/LICENSE-yolox"
  } >"$target"
  echo "wrote $target"
}

out="$here/dist"
rids=""
while [ $# -gt 0 ]; do
  case $1 in
    --licenses)
      [ $# -eq 1 ] || usage
      licenses
      exit 0
      ;;
    --out)
      [ $# -ge 2 ] || usage
      out=$2
      shift 2
      ;;
    -h | --help) usage ;;
    -*) usage ;;
    *)
      case " $ALL_RIDS " in
        *" $1 "*) rids="$rids $1" ;;
        *) die "unknown RID $1 (RIDs: $ALL_RIDS)" ;;
      esac
      shift
      ;;
  esac
done
[ -n "$rids" ] || rids=$ALL_RIDS

command -v docker >/dev/null 2>&1 || die "Docker is needed"
mkdir -p "$out"
out=$(cd "$out" && pwd)
rm -f "$out"/libhs_vision-*.so "$out"/libhs_vision-*.so.part "$out/SHA256SUMS"
trap 'rm -f "$out"/libhs_vision-*.so.part' EXIT

built=""
for rid in $rids; do
  build_rid "$rid"
  built="$built libhs_vision-$rid.so"
done

# shellcheck disable=SC2046,SC2086 # file names without spaces, one per word
(cd "$out" && sha256 $(printf '%s\n' $built | LC_ALL=C sort -u)) >"$out/SHA256SUMS"
echo "$out/SHA256SUMS:"
cat "$out/SHA256SUMS"
