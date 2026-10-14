using System.IO;
using System.Windows;
using ImageClassification.UI.Behaviors;
using Xunit;

namespace ImageClassification.Tests.Behaviors;

/// <summary>
/// The testable core of <see cref="DropDirectoriesBehavior"/>: a Windows Explorer drop
/// arrives as a <see cref="DataFormats.FileDrop"/> string array (folders included) — only
/// paths that actually exist on disk as directories survive the filter.
/// </summary>
public class DropDirectoriesBehaviorTests
{
    [Fact]
    public void TryGetDirectories_ExtractsExistingDirectoriesOnly()
    {
        var dirA = Directory.CreateTempSubdirectory("drop-dirs-a-").FullName;
        var dirB = Directory.CreateTempSubdirectory("drop-dirs-b-").FullName;
        try
        {
            var file = Path.Combine(dirA, "x.png");
            File.WriteAllText(file, "");
            var missing = Path.Combine(dirA, "no-such-dir");

            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { dirA, file, missing, dirB, "", "   " });

            var ok = DropDirectoriesBehavior.TryGetDirectories(data, out var directories);

            Assert.True(ok);
            Assert.Equal(new[] { dirA, dirB }, directories);
        }
        finally
        {
            try { Directory.Delete(dirA, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(dirB, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TryGetDirectories_PayloadWithoutFileDrop_ReturnsFalse()
    {
        var data = new DataObject();
        data.SetData(DataFormats.Text, "just some text");

        var ok = DropDirectoriesBehavior.TryGetDirectories(data, out var directories);

        Assert.False(ok);
        Assert.Empty(directories);
    }

    [Fact]
    public void TryGetDirectories_OnlyNonExistingPaths_ReturnsFalse()
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { "Z:\\definitely-not-here-xyz" });

        Assert.False(DropDirectoriesBehavior.TryGetDirectories(data, out _));
    }

    [Fact]
    public void TryGetDirectories_NullPayload_ReturnsFalse()
    {
        var ok = DropDirectoriesBehavior.TryGetDirectories(null, out var directories);

        Assert.False(ok);
        Assert.Empty(directories);
    }
}