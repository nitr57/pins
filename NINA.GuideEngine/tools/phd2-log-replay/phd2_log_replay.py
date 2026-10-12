#!/usr/bin/env python3
# Usage: phd2_log_replay.py --mount NAME [--describe TEXT] --out DIR GUIDE_LOG...
#
# Turns PHD2 guide logs (log version 2.5, also the native guider's) into replay CSVs for the engine's tests, one gzip
# file per night: DIR/NAME/<YYYY-MM-DD>.csv.gz (the date of the evening). Format: NINA.GuideEngine.Test/Data/umi/README.md.
# Offsets and corrections are in mount px; PHD2 logs "DITHER by" in mount axes, so the lock changes are taken as they
# are; a correction is the pulse that went out at the run's calibrated rate, positive when it reduces a positive offset
# (West / South). Frames without a star (DROP) are left out. Standard library only.

import argparse
import datetime as dt
import gzip
import os
import re

HEADER = {
    'ps': r'Pixel scale = ([\d.]+) arc-sec/px',
    'focal': r'Focal length = (\d+) mm',
    'camera': r'Camera = ([^,]+)',
    'exp': r'^Exposure = (\d+) ms',
    'xangle': r'xAngle = (-?[\d.]+)',
    'xrate': r'xRate = ([\d.]+)',
    'yangle': r'yAngle = (-?[\d.]+)',
    'yrate': r'yRate = ([\d.]+)',
    'xalgo': r'X guide algorithm = ([^,\n]+)',
    'xgain': r'X guide algorithm = [^,]+, Control gain = ([\d.]+)',
    'xmm': r'^Minimum move = ([\d.]+)',
    'yalgo': r'Y guide algorithm = ([^,\n]+)',
    'ymm': r'Y guide algorithm = [^,]+, Minimum move = ([\d.]+)',
    'yaggr': r'Aggression = (\d+)%',
    'maxra': r'Max RA duration = (\d+)',
    'maxdec': r'Max DEC duration = (\d+)',
    'decmode': r'DEC guide mode = (\w+)',
    'raspeed': r'RA Guide Speed = ([\d.]+) a-s/s',
    'decspeed': r'Dec Guide Speed = ([\d.]+) a-s/s',
    'calstep': r'Calibration Step = (\d+) ms',
    'ra': r'^RA = ([\d.]+) hr',
    'dec': r'Dec = (-?[\d.]+) deg, Hour angle',
    'ha': r'Hour angle = (-?[\d.]+) hr',
    'pier': r'Pier side = (\w+)',
    'alt': r'Alt = (-?[\d.]+) deg',
    'az': r'Az = (-?[\d.]+) deg',
    'ppec': r'Period length periodic kernel = ([\d.]+)',
}
STEP = re.compile(r'^(West|East|Backlash|North|South),(\d+),')
RESULT = re.compile(r'^(West|North|East|South) calibration complete\. Angle = (-?[\d.]+) deg, Rate = ([\d.]+) px/sec')
DITHER = re.compile(r'INFO: DITHER by (-?[\d.]+), (-?[\d.]+), new lock pos')


def ts(s):
    return dt.datetime.strptime(s.strip(), '%Y-%m-%d %H:%M:%S')


def header_value(block, line):
    for k, pat in HEADER.items():
        m = re.search(pat, line)
        if m and k not in block:
            block[k] = m.group(1).strip()


def parse(path):
    """Calibrations and guiding runs of one guide log."""
    runs, cals = [], []
    run = cal = None
    settling = False
    with open(path, errors='replace') as fh:
        for line in fh:
            line = line.rstrip('\n')
            if line.startswith('Guiding Begins at'):
                run = {'start': ts(line[17:]), 'hdr': {}, 'frames': [], 'dithers': {}}
                runs.append(run)
                cal, settling = None, False
            elif line.startswith('Calibration Begins at'):
                cal = {'start': ts(line[21:]), 'hdr': {}, 'results': {}, 'backlash': 0}
                cals.append(cal)
                run = None
            elif line.startswith('Guiding Ends at'):
                run = None
            elif cal is not None:
                m = RESULT.match(line)
                if m:
                    cal['results'][m.group(1)] = (m.group(2), m.group(3))
                elif STEP.match(line):
                    cal['backlash'] += line.startswith('Backlash,')
                else:
                    header_value(cal['hdr'], line)
            elif run is not None:
                if not run['frames']:
                    header_value(run['hdr'], line)
                if line.startswith('INFO:'):
                    if 'Settling started' in line:
                        settling = True
                    elif 'Settling complete' in line or 'Settling failed' in line:
                        settling = False
                    m = DITHER.match(line)
                    if m:
                        d = run['dithers'].setdefault(len(run['frames']), [0.0, 0.0])
                        d[0] += float(m.group(1))
                        d[1] += float(m.group(2))
                elif re.match(r'^\d+,', line):
                    p = line.split(',')
                    if len(p) >= 18:
                        run['frames'].append((p, settling))
    return runs, cals


def num(s, nd):
    t = f"{float(s):.{nd}f}".rstrip('0').rstrip('.')
    return '0' if t in ('-0', '') else t


def export(path, out, mount, describe):
    runs, cals = parse(path)
    runs = [r for r in runs if any(p[2] == '"Mount"' and p[5] != '' for p, _ in r['frames'])]
    if not runs:
        return None
    t0 = runs[0]['start']
    night = (t0 if t0.hour >= 12 else t0 - dt.timedelta(days=1)).strftime('%Y-%m-%d')
    h0 = runs[0]['hdr']
    with open(path, errors='replace') as fh:
        version = fh.readline().split(',')[0].replace('PHD2 version ', '')
    lines = [
        f"# Replay of the PHD2 guide log {os.path.basename(path)} ({describe or mount}).",
        f"# PHD2 {version}, guide speed {h0.get('raspeed')}/{h0.get('decspeed')} a-s/s (RA/Dec), camera {h0.get('camera', '?')}, "
        f"guide scope {h0.get('focal')} mm, {h0.get('ps')}\"/px.",
        f"# t = seconds since the first guiding run of the night began ({t0:%Y-%m-%d %H:%M:%S} local). Offsets in px along the",
        "# mount axes (PHD2 RARawDistance/DECRawDistance); corrections = the pulses that went out, in px at the run's calibrated rate",
        "# (PHD2 has already scaled the RA rate for the declination), positive = reducing a positive offset (West / South pulses).",
        "# dither_ra_px/dither_dec_px: lock position change in mount px just before the frame (PHD2 'DITHER by', mount axes).",
        "# Open-loop position (as if unguided) = offset + cumulative lock change + cumulative corrections, per run.",
        "# Frames without a star (DROP) are left out. settling = 1 between PHD2's 'Settling started' and 'Settling complete/failed'.",
    ]
    for c in cals:
        w, n = c['results'].get('West'), c['results'].get('North')
        if w is None:
            continue
        lines.append(f"#calibration,{c['start']:%Y-%m-%dT%H:%M:%S},dec={c['hdr'].get('dec')},pier={c['hdr'].get('pier')},"
                     f"ra_angle={w[0]},ra_rate_px_s={w[1]},dec_angle={n[0] if n else 'NaN'},dec_rate_px_s={n[1] if n else 'NaN'},"
                     f"step_ms={c['hdr'].get('calstep', 'NaN')},backlash_clear_steps={max(0, c['backlash'] - 1)}")
    lines.append("#run,run,start,ha_h,ra_h,dec_deg,pier,alt_deg,az_deg,ra_rate_px_s,dec_rate_px_s,x_angle,y_angle,exposure_ms,"
                 "ra_algorithm,ra_gain,ra_min_move_px,dec_algorithm,dec_aggression_pct,dec_min_move_px,max_ra_ms,max_dec_ms,dec_mode,"
                 "ppec_period_s,dropped_frames")
    rows = []
    for k, r in enumerate(runs, 1):
        h = r['hdr']
        drops = sum(1 for p, _ in r['frames'] if p[2] != '"Mount"' or p[5] == '')
        lines.append('#run,' + ','.join(str(x) for x in [
            k, f"{r['start']:%Y-%m-%dT%H:%M:%S}", h.get('ha'), h.get('ra'), h.get('dec'), h.get('pier'), h.get('alt'), h.get('az'),
            h.get('xrate'), h.get('yrate'), h.get('xangle'), h.get('yangle'), h.get('exp'), h.get('xalgo', ''), h.get('xgain'),
            h.get('xmm'), h.get('yalgo', ''), h.get('yaggr'), h.get('ymm'), h.get('maxra'), h.get('maxdec'), h.get('decmode'),
            h.get('ppec'), drops]))
        off = (r['start'] - t0).total_seconds()
        xr, yr = float(h['xrate']), float(h['yrate'])
        pending = None
        for i, (p, settling) in enumerate(r['frames']):
            if i in r['dithers']:
                d = r['dithers'][i]
                pending = d if pending is None else [pending[0] + d[0], pending[1] + d[1]]
            if p[2] != '"Mount"' or p[5] == '' or p[6] == '':
                continue
            ra_ms = float(p[9]) if p[9] else 0.0
            dec_ms = float(p[11]) if p[11] else 0.0
            rc = ra_ms / 1000 * xr * (1 if p[10] == 'W' else -1)
            dc = dec_ms / 1000 * yr * (1 if p[12] == 'S' else -1)
            d = pending or [0.0, 0.0]
            rows.append(','.join([p[0], f"{off + float(p[1]):.3f}", '1' if settling else '0', '1' if pending else '0',
                                  num(p[5], 3), num(p[6], 3), num(rc, 4), num(dc, 4), str(k), num(d[0], 3), num(d[1], 3),
                                  num(p[16] or 'nan', 1) if p[16] else 'NaN', num(p[15], 0) if p[15] else 'NaN', p[17] or '0']))
            pending = None
    lines.append('frame,time_s,settling,dither,ra_px,dec_px,ra_corr_px,dec_corr_px,run,dither_ra_px,dither_dec_px,snr,star_mass,error_code')
    os.makedirs(os.path.join(out, mount), exist_ok=True)
    fn = os.path.join(out, mount, f"{night}.csv.gz")
    with gzip.open(fn, 'wt', compresslevel=9) as gz:
        gz.write('\n'.join(lines + rows) + '\n')
    return fn, len(rows), len(runs)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--mount', required=True, help='folder name of the mount, e.g. UMi20S-1')
    ap.add_argument('--describe', default='', help='mount and load for the file header')
    ap.add_argument('--out', required=True, help='Data folder, e.g. NINA.GuideEngine.Test/Data/umi')
    ap.add_argument('logs', nargs='+')
    a = ap.parse_args()
    for log in a.logs:
        res = export(log, a.out, a.mount, a.describe)
        print(log, '->', *(res or ('no guiding',)))


if __name__ == '__main__':
    main()
