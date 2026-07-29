using ImageClassification.Core.Models;
using ImageClassification.Core.Services;

namespace ImageClassification.Tests.Services;

public class TimestampConflictTests
{
    [Fact]
    public void FindTimestampConflicts_SameTimeDifferentClasses_ReturnsWarning()
    {
        var classifications = new List<(string Path, string Class, DateTime Created)>
        {
            ("a.jpg", "cat", new DateTime(2024, 1, 1, 12, 0, 0)),
            ("b.jpg", "dog", new DateTime(2024, 1, 1, 12, 0, 30)), // 30s apart, different class
        };

        var warnings = ImageSorter.FindTimestampConflicts(classifications, 1f);

        Assert.Single(warnings);
        Assert.Equal(2, warnings[0].FilePaths.Count);
        Assert.Contains("cat", warnings[0].PredictedClasses);
        Assert.Contains("dog", warnings[0].PredictedClasses);
    }

    [Fact]
    public void FindTimestampConflicts_SameTimeSameClass_NoWarning()
    {
        var classifications = new List<(string Path, string Class, DateTime Created)>
        {
            ("a.jpg", "cat", new DateTime(2024, 1, 1, 12, 0, 0)),
            ("b.jpg", "cat", new DateTime(2024, 1, 1, 12, 0, 30)),
        };

        var warnings = ImageSorter.FindTimestampConflicts(classifications, 1f);

        Assert.Empty(warnings);
    }

    [Fact]
    public void FindTimestampConflicts_DifferentTimeDifferentClasses_NoWarning()
    {
        var classifications = new List<(string Path, string Class, DateTime Created)>
        {
            ("a.jpg", "cat", new DateTime(2024, 1, 1, 12, 0, 0)),
            ("b.jpg", "dog", new DateTime(2024, 1, 1, 12, 10, 0)), // 10 min apart
        };

        var warnings = ImageSorter.FindTimestampConflicts(classifications, 1f);

        Assert.Empty(warnings);
    }

    [Fact]
    public void FindTimestampConflicts_ThreeFilesTwoClasses_WarningWithAll()
    {
        var classifications = new List<(string Path, string Class, DateTime Created)>
        {
            ("a.jpg", "cat", new DateTime(2024, 1, 1, 12, 0, 0)),
            ("b.jpg", "dog", new DateTime(2024, 1, 1, 12, 0, 20)),
            ("c.jpg", "cat", new DateTime(2024, 1, 1, 12, 0, 40)),
        };

        var warnings = ImageSorter.FindTimestampConflicts(classifications, 1f);

        Assert.Single(warnings);
        Assert.Equal(3, warnings[0].FilePaths.Count);
        Assert.Contains("cat", warnings[0].PredictedClasses);
        Assert.Contains("dog", warnings[0].PredictedClasses);
    }

    [Fact]
    public void FindTimestampConflicts_TwoSeparateGroups_ReturnsTwoWarnings()
    {
        var classifications = new List<(string Path, string Class, DateTime Created)>
        {
            // Group 1: 12:00
            ("a.jpg", "cat", new DateTime(2024, 1, 1, 12, 0, 0)),
            ("b.jpg", "dog", new DateTime(2024, 1, 1, 12, 0, 30)),
            // Group 2: 12:05 (5 min later, outside window)
            ("c.jpg", "bird", new DateTime(2024, 1, 1, 12, 5, 0)),
            ("d.jpg", "fish", new DateTime(2024, 1, 1, 12, 5, 20)),
        };

        var warnings = ImageSorter.FindTimestampConflicts(classifications, 1f);

        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void FindTimestampConflicts_EmptyList_ReturnsNoWarnings()
    {
        var classifications = new List<(string Path, string Class, DateTime Created)>();

        var warnings = ImageSorter.FindTimestampConflicts(classifications, 1f);

        Assert.Empty(warnings);
    }

    [Fact]
    public void FindTimestampConflicts_SingleFile_ReturnsNoWarnings()
    {
        var classifications = new List<(string Path, string Class, DateTime Created)>
        {
            ("a.jpg", "cat", new DateTime(2024, 1, 1, 12, 0, 0)),
        };

        var warnings = ImageSorter.FindTimestampConflicts(classifications, 1f);

        Assert.Empty(warnings);
    }

    [Fact]
    public void FindTimestampConflicts_WindowMinutes0_DisablesCheck()
    {
        var classifications = new List<(string Path, string Class, DateTime Created)>
        {
            ("a.jpg", "cat", new DateTime(2024, 1, 1, 12, 0, 0)),
            ("b.jpg", "dog", new DateTime(2024, 1, 1, 12, 0, 30)),
        };

        var warnings = ImageSorter.FindTimestampConflicts(classifications, 0f);

        Assert.Empty(warnings);
    }

    [Fact]
    public void FindTimestampConflicts_ExactlyOnBoundary_NoWarning()
    {
        var classifications = new List<(string Path, string Class, DateTime Created)>
        {
            ("a.jpg", "cat", new DateTime(2024, 1, 1, 12, 0, 0)),
            ("b.jpg", "dog", new DateTime(2024, 1, 1, 12, 1, 0)), // Exactly 1 minute
        };

        var warnings = ImageSorter.FindTimestampConflicts(classifications, 1f);

        // Exactly on boundary = outside window (12:01:00 - 12:00:00 = 1min > 1min window)
        Assert.Empty(warnings);
    }

    [Fact]
    public void SortWarning_Summary_FormatsCorrectly()
    {
        var warning = new SortWarning
        {
            Message = "Test",
            FilePaths = new List<string> { "a.jpg", "b.jpg" },
            PredictedClasses = new List<string> { "cat", "dog" },
            TimeWindowStart = new DateTime(2024, 1, 1, 12, 0, 0),
            TimeWindowEnd = new DateTime(2024, 1, 1, 12, 0, 30)
        };

        var summary = warning.Summary;

        Assert.Contains("30s", summary);
        Assert.Contains("cat", summary);
        Assert.Contains("dog", summary);
        Assert.Contains("12:00:00", summary);
    }

    [Fact]
    public void SortResult_HasWarningsAndConflictsSkipped()
    {
        var result = new SortResult
        {
            Warnings = new List<SortWarning>
            {
                new() { Message = "Conflict" }
            },
            ConflictsSkipped = 5
        };

        Assert.Single(result.Warnings);
        Assert.Equal(5, result.ConflictsSkipped);
    }
}
