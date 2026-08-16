#!/usr/bin/env bash
# Fetches the proprietary SDRplay API (lib + service + headers) into vendor/sdrplay/ for the
# Docker build. Not committed to git (proprietary); run once per checkout.
#
# SDRplay pulled the standalone API installer from their site during the SDRconnect transition;
# the Wayback Machine copy below is byte-identical to the official release (sha256 matches the
# AUR libsdrplay 3.15.2 checksum).
set -euo pipefail

VERSION="3.15.2"
SHA256="3a97ca764263bbe76fb0f2220e6408942357e8864c19e1408a6d6987af382fe3"
URL="https://web.archive.org/web/2024/https://www.sdrplay.com/software/SDRplay_RSP_API-Linux-${VERSION}.run"

cd "$(dirname "$0")/.."
mkdir -p vendor
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

echo "→ downloading SDRplay API ${VERSION}"
curl -fL --retry 3 -o "$WORK/api.run" "$URL"
echo "${SHA256}  $WORK/api.run" | sha256sum -c -

cd "$WORK" && sh api.run --tar xf >/dev/null && cd - >/dev/null

rm -rf vendor/sdrplay
mkdir -p vendor/sdrplay
cp -r "$WORK/amd64" "$WORK/arm64" "$WORK/inc" "$WORK/sdrplay_license.txt" vendor/sdrplay/
echo "✓ vendor/sdrplay populated:"
ls vendor/sdrplay/amd64
