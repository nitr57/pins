# Tools

* `phd2-parity/` — compares star detection with PHD2's compiled `star.cpp` (see its README).
* `phd2-algo-golden/` — generates golden sequences from PHD2's guide algorithm sources
  (`NINA.GuideEngine.Test/Golden/phd2-golden.json`).
* `guide-log-stats/` — per-block statistics of a guide log for live A/B tests of guide settings (RMS, error
  autocorrelation, overshoot after the largest corrections, share of the error after jumps, pulse lengths, Dec
  reversals); blocks start at every settings change or every `--block-min` minutes:
  `python3 guide_log_stats.py InternalGuider_GuideLog_<night>.txt --block-min 20`. Standard library only, so it runs on the rig.
* `phd2-log-replay/` — turns PHD2 guide logs into replay CSVs for the tests, one gzip file per mount and night (format in
  `NINA.GuideEngine.Test/Data/umi/README.md`):
  `python3 phd2_log_replay.py --mount UMi17S --describe "ProxiSky UMi17S, SQA85" --out NINA.GuideEngine.Test/Data/umi PHD2_GuideLog_*.txt`.
  Standard library only.
