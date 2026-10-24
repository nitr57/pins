// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Algorithms;

[TestFixture]
public class DecGuideModeFilterTests
{
    [TestCase(DecGuideMode.Off, GuideDirection.North, 0)]
    [TestCase(DecGuideMode.Off, GuideDirection.South, 0)]
    [TestCase(DecGuideMode.Auto, GuideDirection.North, 300)]
    [TestCase(DecGuideMode.Auto, GuideDirection.South, 300)]
    [TestCase(DecGuideMode.North, GuideDirection.North, 300)]
    [TestCase(DecGuideMode.North, GuideDirection.South, 0)]
    [TestCase(DecGuideMode.South, GuideDirection.North, 0)]
    [TestCase(DecGuideMode.South, GuideDirection.South, 300)]
    [TestCase(DecGuideMode.Off, GuideDirection.East, 300)]
    [TestCase(DecGuideMode.North, GuideDirection.West, 300)]
    public void FiltersAlgorithmMoves(DecGuideMode mode, GuideDirection dir, int expected)
    {
        DecGuideModeFilter.Apply(mode, dir, 300).Should().Be(expected);
    }

    [Test]
    public void DoesNotFilterCalibrationOrManualMoves()
    {
        DecGuideModeFilter.Apply(DecGuideMode.Off, GuideDirection.North, 300, isAlgorithmOrDeducedMove: false).Should().Be(300);
    }
}
