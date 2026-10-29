// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NINA.Equipment.Equipment.MyGuider.Internal;
using NUnit.Framework;

namespace NINA.Test.Equipment.MyGuider.Internal;

/// <summary>
/// The internal guider's stores hold the entries of every profile and mount, so neither a power cut during a save nor an
/// unreadable file may cost them: saves are atomic with a backup, an unreadable file is kept aside, and concurrent
/// read-modify-write steps don't lose each other's entries.
/// </summary>
[TestFixture]
public class StoreFileTests
{
    private string dir = null!;
    private string path = null!;

    [SetUp]
    public void SetUp()
    {
        dir = Path.Combine(Path.GetTempPath(), "pins-stores-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "calibrations.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private int Read() => StoreFile.Read(path, s => int.Parse(s, CultureInfo.InvariantCulture), () => 0, "test store");

    [Test]
    public void Write_KeepsThePreviousFileAsBackup_AndLeavesNoTemporaryFile()
    {
        StoreFile.Write(path, "1");
        StoreFile.Write(path, "2");

        Assert.That(Read(), Is.EqualTo(2));
        Assert.That(File.ReadAllText(StoreFile.BackupPath(path)), Is.EqualTo("1"));
        Assert.That(File.Exists(path + ".tmp"), Is.False);
    }

    [Test]
    public void Read_UsesTheBackup_AndKeepsTheUnreadableFileAside()
    {
        StoreFile.Write(path, "1");
        StoreFile.Write(path, "2");
        File.WriteAllText(path, "{trunc");

        Assert.That(Read(), Is.EqualTo(1));
        Assert.That(Directory.GetFiles(dir, "calibrations.json.unreadable-*"), Has.Length.EqualTo(1));
    }

    [Test]
    public void ASaveAfterAnUnreadableFile_DoesNotDestroyIt()
    {
        File.WriteAllText(path, "{trunc");

        Assert.That(Read(), Is.EqualTo(0), "no backup: an empty store");
        StoreFile.Write(path, "5");

        var aside = Directory.GetFiles(dir, "calibrations.json.unreadable-*").Single();
        Assert.That(File.ReadAllText(aside), Is.EqualTo("{trunc"), "kept for recovery");
        Assert.That(Read(), Is.EqualTo(5));
    }

    [Test]
    public void LockedReadModifyWrite_LosesNoUpdate()
    {
        StoreFile.Write(path, "0");

        Parallel.For(0, 40, _ => StoreFile.Locked(path, () => StoreFile.Write(path, (Read() + 1).ToString(CultureInfo.InvariantCulture))));

        Assert.That(Read(), Is.EqualTo(40));
    }
}
