#!/usr/bin/env bash
# SPDX-License-Identifier: MPL-2.0
# Regenerates NINA.GuideEngine.Test/Golden/phd2-golden.json by compiling the algorithmic
# parts of PHD2 (extracted verbatim, GUI code dropped) against stub headers in a gcc container.
# Usage: NINA.GuideEngine/tools/phd2-algo-golden/run.sh <path-to-phd2/src>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
phd2="$(cd "${1:?usage: run.sh <phd2/src>}" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
python3 "$here/extract.py" "$phd2" "$work"
cp "$here/stub/phd.h" "$here/harness.cpp" "$work/"
out="$root/NINA.GuideEngine.Test/Golden/phd2-golden.json"
mkdir -p "$(dirname "$out")"
docker run --rm -u "$(id -u):$(id -g)" -v "$work:/w" -w /w gcc:14 sh -c \
  'g++ -std=c++17 -O0 -ffp-contract=off -w -I. -o golden harness.cpp guiding_stats.cpp backlash_comp.cpp guide_algorithm_*.cpp && ./golden' > "$out"
echo "wrote $out ($(wc -c < "$out") bytes)"
