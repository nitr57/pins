#!/usr/bin/env python3
# Usage: guide_log_stats.py GUIDE_LOG [--block-min MIN] [--after HH:MM] [--before HH:MM]
#
# Summarises a native-guider guide log (PHD2 format) per block, for comparing settings live, e.g. the correction pace
# (docs/notes/CORRECTION-PACE.md). A new block starts with every "Guiding Begins" section, every
# "INFO: Guiding parameter change" line and, with --block-min, every MIN minutes. Settling frames, the frames of a
# dither and dropped frames are left out; statistics never span a gap.
#
# Per block and axis:
#   n          guide frames counted
#   rms"       RMS of the measured error in arcsec (the log's pixel scale)
#   acf1..2    autocorrelation of the error at lags 1 and 2 (about 0 when nothing is left to correct; negative at lag 2 =
#              the error swings back two frames later)
#   over2      after the largest 10 % of corrections: share of cases with the error on the other side two frames later
#              (about 50 % in a well-tuned loop, more = over-correction)
#   jump%      share of the squared error in the 4 frames after an excursion beyond 4 robust sigma
#   pulse ms   median / 90th percentile of the non-zero pulse lengths
#   rev        share of pulses that reverse the direction of the previous pulse
# Only the Python standard library is used, so it runs on the rig too.
import argparse
import math
import re
import statistics
import sys
from datetime import datetime, timedelta

ap = argparse.ArgumentParser()
ap.add_argument("log")
ap.add_argument("--block-min", type=float, default=0)
ap.add_argument("--after", help="only frames at or after this local time (HH:MM)")
ap.add_argument("--before", help="only frames before this local time (HH:MM)")
args = ap.parse_args()


def hhmm(s):
    h, m = s.split(":")
    return int(h) * 60 + int(m)


class Block:
    def __init__(self, start, label):
        self.start = start
        self.label = label
        self.runs = [[]]  # unbroken sequences of frames: (time, ra px, dec px, ra corr px, dec corr px, ra ms, dec ms)

    def add(self, frame):
        self.runs[-1].append(frame)

    def gap(self):
        if self.runs[-1]:
            self.runs.append([])


blocks = []
settings = {}
pixel_scale = 1.0
x_rate = y_rate = None
begin = None
block = None
settling = False
last_frame = None


def new_block(t, why):
    global block
    label = "; ".join(f"{k} {v}" for k, v in settings.items())
    block = Block(t, f"{why}: {label}" if label else why)
    blocks.append(block)


def read_settings(line):
    m = re.match(r"(?:INFO: Guiding parameter change, )?([XY]) guide algorithm = (\w+)(.*)", line)
    if m:
        axis = "RA" if m.group(1) == "X" else "Dec"
        pace = re.search(r"Correction pace = ([\d.]+)", m.group(3))
        settings[axis] = m.group(2) + (f" pace {pace.group(1)}" if pace else "")
        return True
    m = re.match(r"(?:INFO: Guiding parameter change, )?Exposure = (\d+) ms", line)
    if m:
        settings["exp"] = f"{int(m.group(1)) / 1000:g} s"
        return True
    return False


with open(args.log, encoding="utf-8", errors="replace") as f:
    for raw in f:
        line = raw.rstrip("\n").strip()
        m = re.match(r"Guiding Begins at (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)", line)
        if m:
            begin = datetime.strptime(m.group(1), "%Y-%m-%d %H:%M:%S")
            settings.clear()
            block = None
            last_frame = None
            settling = False
            continue
        if begin is None:
            continue
        m = re.match(r"Pixel scale = ([\d.]+) arc-sec/px", line)
        if m:
            pixel_scale = float(m.group(1))
        m = re.search(r"xRate = ([\d.]+), yAngle = [-\d.]+, yRate = ([\d.]+)", line)
        if m:
            x_rate, y_rate = float(m.group(1)), float(m.group(2))
        if line.startswith("INFO: Guiding parameter change"):
            if read_settings(line) and block is not None:
                new_block(block.runs[-1][-1][0] if block.runs[-1] else block.start, "changed")
            continue
        if read_settings(line):
            continue
        if "Settling started" in line:
            settling = True
            if block:
                block.gap()
            continue
        if "Settling complete" in line or "Settling failed" in line:
            settling = False
            continue
        if line.startswith("INFO: DITHER"):
            if block:
                block.gap()
            continue
        f_ = line.split(",")
        if len(f_) < 17 or not f_[0].isdigit():
            continue
        frame, t = int(f_[0]), begin + timedelta(seconds=float(f_[1]))
        if f_[2] != '"Mount"' or settling:
            if block:
                block.gap()
            continue
        minutes = t.hour * 60 + t.minute
        if (args.after and minutes < hhmm(args.after)) or (args.before and minutes >= hhmm(args.before)):
            continue
        if block is None or (args.block_min and (t - block.start).total_seconds() >= args.block_min * 60):
            new_block(t, "start" if block is None else "time")
        if last_frame is not None and frame != last_frame + 1:
            block.gap()
        last_frame = frame

        def pulse(ms, d, positive):
            v = float(ms) if ms else 0.0
            return v if d == positive else -v if d else 0.0

        ra_ms, dec_ms = pulse(f_[9], f_[10], "W"), pulse(f_[11], f_[12], "S")
        rx = (x_rate or 1.0) / 1000.0
        ry = (y_rate or 1.0) / 1000.0
        block.add((t, float(f_[5]), float(f_[6]), ra_ms * rx, dec_ms * ry, ra_ms, dec_ms))


def axis_stats(runs, i_z, i_c, i_ms):
    z_all = [fr[i_z] for run in runs for fr in run]
    n = len(z_all)
    if n < 20:
        return None
    mean = sum(z_all) / n
    var = sum((z - mean) ** 2 for z in z_all) / n
    rms = math.sqrt(sum(z * z for z in z_all) / n)

    def acf(k):
        num = sum((run[j][i_z] - mean) * (run[j + k][i_z] - mean) for run in runs for j in range(len(run) - k))
        return num / (var * n) if var > 0 else float("nan")

    pairs = [(abs(run[j][i_c]), run[j + 2][i_z] * run[j][i_c] < 0) for run in runs for j in range(len(run) - 2) if run[j][i_c]]
    pairs.sort(key=lambda p: -p[0])
    top = pairs[:max(1, len(pairs) // 10)] if pairs else []
    over2 = sum(p[1] for p in top) / len(top) if top else float("nan")

    med = statistics.median(z_all)
    mad = 1.4826 * statistics.median(abs(z - med) for z in z_all)
    total = sum(z * z for z in z_all)
    jump_sq = 0.0
    for run in runs:
        marked = [False] * len(run)
        for j, fr in enumerate(run):
            if mad > 0 and abs(fr[i_z] - med) > 4 * mad:
                for q in range(j, min(len(run), j + 5)):
                    marked[q] = True
        jump_sq += sum(fr[i_z] ** 2 for fr, mk in zip(run, marked) if mk)

    ms = [abs(fr[i_ms]) for run in runs for fr in run if fr[i_ms]]
    signs = [math.copysign(1, fr[i_ms]) for run in runs for fr in run if fr[i_ms]]
    rev = sum(1 for a, b in zip(signs, signs[1:]) if a != b) / (len(signs) - 1) if len(signs) > 1 else float("nan")
    q90 = sorted(ms)[int(0.9 * (len(ms) - 1))] if ms else float("nan")
    return dict(n=n, rms=rms * pixel_scale, acf1=acf(1), acf2=acf(2), over2=over2,
                jump=jump_sq / total if total > 0 else float("nan"),
                pmed=statistics.median(ms) if ms else float("nan"), p90=q90, rev=rev)


if not blocks:
    sys.exit("no guide frames found")
print(f"{args.log}: pixel scale {pixel_scale} arcsec/px")
print("{:45} {:4} {:>4} {:>6} {:>6} {:>6} {:>6} {:>6} {:>11} {:>5}".format(
    "block", "axis", "n", 'rms"', "acf1", "acf2", "over2", "jump%", "pulse ms", "rev"))
for b in blocks:
    runs = [r for r in b.runs if len(r) > 3]
    head = f"{b.start:%H:%M} {b.label}"[:45]
    for axis, i_z, i_c, i_ms in (("RA", 1, 3, 5), ("Dec", 2, 4, 6)):
        s = axis_stats(runs, i_z, i_c, i_ms)
        if s is None:
            continue
        print(f"{head:45} {axis:4} {s['n']:4d} {s['rms']:6.2f} {s['acf1']:+6.2f} {s['acf2']:+6.2f} {100 * s['over2']:5.0f}% "
              f"{100 * s['jump']:5.0f}% {s['pmed']:5.0f}/{s['p90']:<5.0f} {100 * s['rev']:4.0f}%")
        head = ""
