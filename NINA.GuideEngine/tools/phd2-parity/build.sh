#!/usr/bin/env bash
# Builds tools/phd2-parity/bin/phd2-parity: PHD2's unmodified star.cpp (Star::Find, GuideStar::AutoFind)
# plus the Median3 part of image_math.cpp, compiled against a small stub phd.h (no wxWidgets), inside
# a gcc docker image. The binary is linked statically so it also runs in the .NET SDK container.
#
# PHD2 source: $PHD2_SRC (a PHD2 checkout) if set, else a cached clone in .cache/phd2 at the pinned commit.
set -euo pipefail
cd "$(dirname "$0")"

COMMIT=a6c027227c3dcbc2978185c8abf3979ac1d39d32
GCC_IMAGE=${GCC_IMAGE:-gcc:14}
SRC=${PHD2_SRC:-}

if [ -z "$SRC" ]; then
    SRC=$PWD/.cache/phd2
    if [ ! -d "$SRC/.git" ]; then
        mkdir -p .cache
        git clone -q --filter=blob:none --no-checkout https://github.com/OpenPHDGuiding/phd2.git "$SRC"
    fi
    if [ "$(git -C "$SRC" rev-parse HEAD 2>/dev/null || true)" != "$COMMIT" ]; then
        git -C "$SRC" fetch -q origin "$COMMIT" || true
        git -C "$SRC" checkout -q "$COMMIT"
    fi
fi

if [ -d "$SRC/.git" ] || [ -f "$SRC/.git" ]; then
    head=$(git -C "$SRC" rev-parse HEAD)
    if [ "$head" != "$COMMIT" ]; then
        echo "warning: $SRC is at $head, parity reference is $COMMIT" >&2
    fi
fi

for f in star.cpp star.h point.h image_math.cpp; do
    [ -f "$SRC/src/$f" ] || { echo "missing $SRC/src/$f" >&2; exit 1; }
done

rm -rf gen && mkdir -p gen bin
# Unmodified PHD2 sources; they must sit next to the stub phd.h because star.cpp uses #include "phd.h".
cp "$SRC/src/star.cpp" "$SRC/src/star.h" "$SRC/src/point.h" gen/
cp stub/phd.h gen/phd.h
# Median3(usImage&), the median helpers and Median3(dst, src, size, rect) from image_math.cpp, verbatim.
{
    echo '#include "phd.h"'
    awk '/^bool Median3\(usImage& img\)/{p=1} /^static unsigned short MedianBorderingPixels/{p=0} p' "$SRC/src/image_math.cpp"
} > gen/image_math_median3.cpp
grep -q 'void Median3(unsigned short \*dst' gen/image_math_median3.cpp || { echo "Median3 extraction failed" >&2; exit 1; }

docker run --rm -u "$(id -u):$(id -g)" -v "$PWD:/w" -w /w "$GCC_IMAGE" \
    g++ -O2 -std=c++17 -static -Wall -Wno-unused-variable -Wno-unused-but-set-variable -Wno-sign-compare -Wno-misleading-indentation \
    -I gen -o bin/phd2-parity src/main.cpp gen/star.cpp gen/image_math_median3.cpp

echo "built $(pwd)/bin/phd2-parity from $SRC ($COMMIT)"
