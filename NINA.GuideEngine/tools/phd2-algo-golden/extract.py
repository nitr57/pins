#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Extracts the non-GUI parts of PHD2 sources verbatim so they can be compiled against stub headers.

Usage: extract.py <phd2/src> <outdir>

Top-level items (function definitions, class definitions, static declarations) are copied unchanged;
items that mention GUI classes (config/graph panes, BacklashGraph, BacklashTool) are dropped.
"""
import os
import re
import sys

DROP = re.compile(r"Pane|BacklashGraph|BacklashTool|wxDialog|wxBitmap|wxSize")


def top_level_items(text):
    """Yields (header, body) for each top-level item of a C++ file (after the includes)."""
    i, n = 0, len(text)
    start = 0
    depth = 0
    in_str = None
    while i < n:
        c = text[i]
        two = text[i:i + 2]
        if in_str:
            if c == "\\":
                i += 2
                continue
            if c == in_str:
                in_str = None
            i += 1
            continue
        if two == "//":
            j = text.find("\n", i)
            i = n if j < 0 else j
            continue
        if two == "/*":
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
            continue
        if c in "\"'":
            in_str = c
        elif c == "#" and depth == 0 and (i == 0 or text[i - 1] == "\n"):
            j = text.find("\n", i)
            yield text[start:j + 1]
            i = j + 1
            start = i
            continue
        elif c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                j = i + 1
                # class/struct definitions end with '};'
                m = re.match(r"\s*;", text[j:])
                if m:
                    j += m.end()
                yield text[start:j]
                start = j
                i = j
                continue
        elif c == ";" and depth == 0:
            yield text[start:i + 1]
            start = i + 1
        i += 1
    yield text[start:]


def filtered(path, keep_includes=False):
    text = open(path, encoding="utf-8").read()
    out = []
    for item in top_level_items(text):
        head = item.split("{", 1)[0]
        stripped = item.strip()
        if stripped.startswith("#include"):
            if keep_includes and '"' not in stripped:
                out.append(item)
            continue
        if stripped.startswith("#"):
            out.append(item)
            continue
        if DROP.search(head):
            continue
        out.append(item)
    return "".join(out)


def main():
    src, outdir = sys.argv[1], sys.argv[2]
    os.makedirs(outdir, exist_ok=True)
    for name in ["guide_algorithm_identity", "guide_algorithm_hysteresis", "guide_algorithm_lowpass",
                 "guide_algorithm_lowpass2", "guide_algorithm_resistswitch"]:
        body = filtered(os.path.join(src, name + ".cpp"))
        with open(os.path.join(outdir, name + ".cpp"), "w") as f:
            f.write('#include "phd.h"\n' + body)
    body = filtered(os.path.join(src, "backlash_comp.cpp"), keep_includes=True)
    with open(os.path.join(outdir, "backlash_comp.cpp"), "w") as f:
        f.write('#include "phd.h"\n#include "backlash_comp_decl.h"\n' + body)
    # BacklashComp class declaration from the header
    hdr = open(os.path.join(src, "backlash_comp.h"), encoding="utf-8").read()
    m = re.search(r"class BacklashComp\s*\{.*?\n\};", hdr, re.S)
    with open(os.path.join(outdir, "backlash_comp_decl.h"), "w") as f:
        f.write("#pragma once\nclass Scope;\nclass BLCHistory;\n" + m.group(0) + "\n")
    # guiding_stats compiles unchanged
    for name in ["guiding_stats.cpp", "guiding_stats.h"]:
        with open(os.path.join(src, name), encoding="utf-8") as fi, open(os.path.join(outdir, name), "w") as fo:
            fo.write(fi.read())


if __name__ == "__main__":
    main()
