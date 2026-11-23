#!/usr/bin/env bash
# Downloads SFace, the face recognition model, from the OpenCV zoo's Hugging Face mirror into
# native/model/face_recognition_sface_2021dec.onnx, unless a file with its pinned SHA-256 is there already. The tests
# open it from there, and the release workflow publishes it beside the libraries. It is never committed (.gitignore)
# and never embedded in the library: HassSharp downloads it on its own, checked by the same SHA-256.
#
#   native/fetch-model.sh
#
# A file there with another SHA-256 is downloaded again; a download with another SHA-256 fails and is removed.
set -euo pipefail

NAME=face_recognition_sface_2021dec.onnx
URL=https://huggingface.co/opencv/face_recognition_sface/resolve/main/$NAME
SHA256=0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79

here=$(cd "$(dirname "$0")" && pwd)
target="$here/model/$NAME"

die() {
  echo "fetch-model.sh: $*" >&2
  exit 1
}

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  else
    shasum -a 256 "$1" | cut -d ' ' -f 1
  fi
}

if [ -f "$target" ]; then
  if [ "$(sha256_of "$target")" = "$SHA256" ]; then
    echo "$target is there already"
    exit 0
  fi
  echo "$target has another SHA-256: downloading it again"
fi

mkdir -p "$here/model"
part=$(mktemp "$target.XXXXXX")
# shellcheck disable=SC2064 # the path is fixed now
trap "rm -f '$part'" EXIT
curl -fsSL --retry 3 -o "$part" "$URL" || die "could not download $URL"
got=$(sha256_of "$part")
[ "$got" = "$SHA256" ] || die "$URL has SHA-256 $got, not $SHA256"
# mktemp makes the file readable by its owner only.
chmod 644 "$part"
mv "$part" "$target"
echo "downloaded $target"
