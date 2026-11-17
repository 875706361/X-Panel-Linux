#!/usr/bin/env bash
set -euo pipefail

# Xray v26.11.9 needs Go >= 1.27; let the local toolchain fetch it when the
# installed Go is older instead of failing with "go.mod requires go >= 1.27".
export GOTOOLCHAIN="${GOTOOLCHAIN:-auto}"

# Build an Xray 26.11.9 binary with the minimal REALITY legacy-client compatibility patch.
# The patch keeps UUID, short-id, time, version, and REALITY authentication checks.
#
# TARGET selects the linux platform token used by the panel's bin/ layout:
#   amd64 arm64 386 s390x armv7 armv6 armv5   (default: amd64)
# Output: $ROOT/dist/xray-linux-$TARGET

XRAY_VERSION="${XRAY_VERSION:-v26.11.9}"
REALITY_COMMIT="${REALITY_COMMIT:-4bb46b2afd8b4f1f082fbaccae34fd3c7a3f8973}"
TARGET="${TARGET:-amd64}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="${WORK:-${TMPDIR:-/tmp}/xray-compat-build}"
OUT="${OUT:-${ROOT}/dist/xray-linux-${TARGET}}"

case "$TARGET" in
    amd64) GOARCH_="amd64"; GOARM_="" ;;
    arm64) GOARCH_="arm64"; GOARM_="" ;;
    386)   GOARCH_="386";   GOARM_="" ;;
    s390x) GOARCH_="s390x"; GOARM_="" ;;
    armv7) GOARCH_="arm";   GOARM_="7" ;;
    armv6) GOARCH_="arm";   GOARM_="6" ;;
    armv5) GOARCH_="arm";   GOARM_="5" ;;
    *) echo "unsupported TARGET '$TARGET' (amd64|arm64|386|s390x|armv7|armv6|armv5)" >&2; exit 1 ;;
esac

rm -rf "$WORK"
mkdir -p "$WORK" "$(dirname "$OUT")"
git clone --depth 1 --branch "$XRAY_VERSION" https://github.com/XTLS/Xray-core.git "$WORK/xray"
git clone --depth 1 https://github.com/XTLS/REALITY.git "$WORK/reality"
cd "$WORK/reality"
git fetch --depth 1 origin "$REALITY_COMMIT"
git checkout "$REALITY_COMMIT"
python3 - <<'PY'
from pathlib import Path
p = Path("tls.go")
s = p.read_text()
old = """\t\t\tif peerPub2 == nil {\n\t\t\t\tbreak // reject outdated/strange Client Hello that doesn't have X25519MLKEM768 before optional X25519\n\t\t\t}\n\t\t\tif peerPub == nil {\n\t\t\t\tpeerPub = peerPub2 // secondary choice: X25519 in X25519MLKEM768\n\t\t\t}"""
new = """\t\t\tif peerPub == nil {\n\t\t\t\tpeerPub = peerPub2\n\t\t\t}\n\t\t\t// Compatibility: accept legacy X25519-only ClientHello; retain REALITY auth below.\n\t\t\tif peerPub == nil {\n\t\t\t\tbreak\n\t\t\t}"""
if old not in s:
    raise SystemExit("REALITY gate was not found; refuse to build an unverified patch")
p.write_text(s.replace(old, new, 1))
PY
cd "$WORK/xray"
go mod edit -replace="github.com/xtls/reality=../reality"
go mod edit -fmt
go mod tidy

build_env=(CGO_ENABLED=0 GOOS=linux GOARCH="$GOARCH_")
[ -n "$GOARM_" ] && build_env+=(GOARM="$GOARM_")
env "${build_env[@]}" go build -p=1 -trimpath -ldflags='-s -w' -o "$OUT" ./main

echo "built $OUT for linux/$GOARCH_${GOARM_:+ armv$GOARM_}"
# Non-native targets cannot be executed here, so verify from the embedded build info.
go version -m "$OUT" | grep -E 'GOARCH=|GOARM=|vcs.modified='
if [ "$GOARCH_" = "$(go env GOHOSTARCH)" ] && [ -z "$GOARM_" ]; then
    "$OUT" version
fi
sha256sum "$OUT"
