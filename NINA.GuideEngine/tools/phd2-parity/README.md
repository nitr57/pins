# PHD2 parity harness

Compares `Star.Find` / `GuideStar.AutoFind` with PHD2's own implementation on identical frames.

* `build.sh` copies PHD2's **unmodified** `src/star.cpp`, `src/star.h`, `src/point.h` and the
  `Median3` section of `src/image_math.cpp` (extracted verbatim) into `gen/`, and compiles them with
  `src/main.cpp` in a `gcc:14` container into the static binary `bin/phd2-parity`.
  PHD2 source: `$PHD2_SRC` if set, otherwise a clone of `OpenPHDGuiding/phd2` at commit
  `a6c027227c3dcbc2978185c8abf3979ac1d39d32` cached in `.cache/phd2`.
* `stub/phd.h` replaces PHD2's `phd.h`: minimal `wxString`/`wxRect`/`wxSize`, a no-op debug log, a
  minimal `usImage`, and stand-ins for `pFrame->pGuider` (AutoFind downsample, HFD limits, min SNR),
  `pFrame->GetCameraPixelScale()` and `pCamera` (saturation by ADU) whose values come from each
  request. Nothing algorithmic is stubbed.
* The test `NINA.GuideEngine.Test/Parity/Phd2ParityTests.cs` (`[Category("Parity")]`) renders
  synthetic frames, runs both implementations and compares result codes, centroids, mass, SNR, HFD,
  peak values, the AutoFind primary and the multi-star list. It is skipped when the binary is missing;
  `PHD2_PARITY_CLI` overrides the binary path.

```sh
PHD2_SRC=/path/to/phd2 NINA.GuideEngine/tools/phd2-parity/build.sh   # or without PHD2_SRC to clone
dotnet test NINA.GuideEngine.Test --filter Category=Parity --logger "console;verbosity=normal"
```

CLI job format: see the header of `src/main.cpp`.
