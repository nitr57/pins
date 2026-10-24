// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Algorithms;

/// <summary>
/// Feeds a real PHD2-format guide log (the Dec offsets and the Dec pulses that went out) into a play model open loop and
/// prints what it learns. The log is not part of the repository: set PINS_GUIDELOG to its path.
/// </summary>
[TestFixture]
public class PlayModelGuideLogReplay
{
    [Test]
    [Explicit("needs a guide log in PINS_GUIDELOG")]
    public void PrintWhatTheLogTeaches()
    {
        string? path = Environment.GetEnvironmentVariable("PINS_GUIDELOG");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Assert.Ignore("PINS_GUIDELOG not set");
        }

        PlayModelBacklash? play = null;
        double yRate = double.NaN;
        int frames = 0;
        string section = string.Empty;
        foreach (var line in File.ReadLines(path!))
        {
            if (line.StartsWith("Guiding Begins", StringComparison.Ordinal))
            {
                Report(play, section, frames);
                section = line;
                play = new PlayModelBacklash(900);
                frames = 0;
                continue;
            }

            if (play is null)
            {
                continue;
            }

            if (line.Contains("yRate = ", StringComparison.Ordinal))
            {
                string v = line[(line.IndexOf("yRate = ", StringComparison.Ordinal) + 8)..].Split(',')[0];
                yRate = double.Parse(v, CultureInfo.InvariantCulture) / 1000.0; // px/s -> px/ms
                continue;
            }

            if (line.StartsWith("INFO: DITHER", StringComparison.Ordinal) || line.StartsWith("INFO: SET LOCK", StringComparison.Ordinal))
            {
                // the lock position jumped: the window measuring a take-up is void
                play.TrackResults(MoveOptions.RecoveryMove, 0, 0, yRate);
                continue;
            }

            var p = line.Split(',');
            if (p.Length < 17 || !int.TryParse(p[0], out _) || !double.TryParse(p[6], NumberStyles.Float, CultureInfo.InvariantCulture, out double rawDec))
            {
                continue;
            }

            frames++;
            play.TrackResults(MoveOptions.GuideStep, rawDec, 0, yRate);
            if (int.TryParse(p[11], out int decMs) && decMs > 0)
            {
                play.PulseSent(p[12] == "S" ? GuideDirection.South : GuideDirection.North, decMs);
            }
        }

        Report(play, section, frames);
    }

    private static void Report(PlayModelBacklash? play, string section, int frames)
    {
        if (play is null || frames < 50)
        {
            return;
        }

        var v = play.Values;
        TestContext.Out.WriteLine(
            $"{section}: {frames} frames -> full reversal {v.FullReversalMs:F0} ms ({v.Shape}), {v.Reversals} reversals, {v.LearnedWindows:F1} take-ups ({play.LearnCount} learned), noise σ {v.NoiseSigmaPx:F3} px");
    }
}
