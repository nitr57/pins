// SPDX-License-Identifier: MPL-2.0

using NINA.Equipment.Equipment.MyGuider.Internal;
using NINA.GuideEngine.Core;
using NUnit.Framework;

namespace NINA.Test.Equipment.MyGuider.Internal;

[TestFixture]
public class DarkLibraryFilesTests
{
    private const int Width = 8;
    private const int Height = 6;

    private string dir = null!;
    private string native = null!;
    private string phd2 = null!;

    [SetUp]
    public void SetUp()
    {
        dir = Path.Combine(Path.GetTempPath(), "pins-darks-" + Guid.NewGuid().ToString("N"));
        native = Path.Combine(dir, "darks_cam_8x6_bin1.fits");
        phd2 = Path.Combine(dir, "phd2");
        Directory.CreateDirectory(phd2);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static List<GuideFrame> Masters() => [new GuideFrame(Width, Height) { ExposureMs = 1000, Binning = 1 }];

    private (NINA.GuideEngine.Imaging.DarkLibrary Library, string Source)? Load(int gain, int offset, out string? mismatch) =>
        DarkLibraryFiles.Load(native, [phd2], Width, Height, 1, gain, offset, out mismatch);

    [Test]
    public void NativeLibrary_IsUsed_WithTheGainAndOffsetItWasBuiltWith()
    {
        DarkLibraryFiles.Save(native, Masters(), 120, 30);

        var loaded = Load(120, 30, out var mismatch);

        Assert.That(loaded?.Source, Is.EqualTo(native));
        Assert.That(mismatch, Is.Null);
    }

    [TestCase(200, 30, "gain 120 (now 200)")]
    [TestCase(120, -1, "offset 30 (now of the guide camera's settings)")]
    public void NativeLibrary_IsNotUsed_WithAnotherGainOrOffset(int gain, int offset, string expected)
    {
        DarkLibraryFiles.Save(native, Masters(), 120, 30);
        DarkLibraryFiles.Save(Path.Combine(phd2, "PHD2_dark_lib_1.fit"), Masters(), null, null);

        var loaded = Load(gain, offset, out var mismatch);

        Assert.That(loaded, Is.Null, "nor is PHD2's library tried instead");
        Assert.That(mismatch, Does.Contain(expected));
    }

    [Test]
    public void CameraDefaults_MatchALibraryBuiltWithThem()
    {
        DarkLibraryFiles.Save(native, Masters(), null, null);

        Assert.That(Load(-1, -1, out var mismatch)?.Source, Is.EqualTo(native));
        Assert.That(mismatch, Is.Null);
    }

    [Test]
    public void Phd2Library_IsUsed_WithoutANativeOne()
    {
        var file = Path.Combine(phd2, "PHD2_dark_lib_1.fit");
        DarkLibraryFiles.Save(file, Masters(), 120, 30);

        Assert.That(Load(200, 10, out var mismatch)?.Source, Is.EqualTo(file), "a PHD2 library is not checked");
        Assert.That(mismatch, Is.Null);
    }
}
