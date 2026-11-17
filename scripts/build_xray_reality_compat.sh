#!/usr/bin/env bash
set -euo pipefail

# Xray v26.11.9 needs Go >= 1.27; let the local toolchain fetch it when the
# installed Go is older instead of failing with "go.mod requires go >= 1.27".
export GOTOOLCHAIN="${GOTOOLCHAIN:-auto}"

# Build an Xray 26.11.9 binary with the minimal REALITY legacy-client compatibility patch.
# The patch keeps UUID, short-id, time, version, and REALITY authentication checks.

XRAY_VERSION="${XRAY_VERSION:-v26.11.9}"
REALITY_COMMIT="${REALITY_COMMIT:-4bb46b2afd8b4f1f082fbaccae34fd3c7a3f8973}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="${WORK:-${TMPDIR:-/tmp}/xray-compat-build}"
OUT="${OUT:-${ROOT}/dist/xray-linux-amd64-compat}"

rm -rf "$WORK"
mkdir -p "$WORK"
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
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -p=1 -trimpath -ldflags='-s -w' -o "$OUT" ./main
"$OUT" version
sha256sum "$OUT"
