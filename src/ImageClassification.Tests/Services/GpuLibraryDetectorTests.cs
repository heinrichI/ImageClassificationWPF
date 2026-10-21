using System.IO;
using ImageClassification.UI.Services;
using Xunit;

namespace ImageClassification.Tests.Services;

/// <summary>
/// Filesystem-level behaviour of <see cref="GpuLibraryDetector"/>: CUDA version
/// selection by the cudart marker, cuDNN bin discovery in the v9 (bin\12.x\x64) and
/// v8 (bin\x64) layouts, depth limits and system-folder filtering. The registry part
/// of CUDA detection is machine-dependent, so only its result contract is checked.
/// </summary>
public class GpuLibraryDetectorTests : IDisposable
{
    private readonly string _root;

    public GpuLibraryDetectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ics_gpu_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static void Touch(string dir, string fileName)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), "x");
    }

    [Theory]
    [InlineData("v12.4", true, 12.4)]
    [InlineData("v11", true, 11.0)]
    [InlineData("v12.10", true, 12.10)]
    [InlineData("vx", false, 0)]
    [InlineData("12.4", false, 0)]
    [InlineData("v", false, 0)]
    public void TryParseCudaVersion_ParsesMajorMinor(string name, bool expectedParsed, double expectedVersion)
    {
        Assert.Equal(expectedParsed, GpuLibraryDetector.TryParseCudaVersion(name, out var version));
        if (expectedParsed)
            Assert.Equal(expectedVersion, version, 3);
    }

    [Fact]
    public void BestCudaVersionDirectory_PicksNewestVersion_ThatHasCudart()
    {
        // v12.1 and v12.4 have cudart, v11.8 does not → v12.4 wins over v12.1.
        Touch(Path.Combine(_root, "v11.8", "bin"), "README.txt");
        Touch(Path.Combine(_root, "v12.1", "bin"), "cudart64_12.dll");
        Touch(Path.Combine(_root, "v12.4", "bin"), "cudart64_12.dll");
        // A folder that looks like a version but is not.
        Touch(Path.Combine(_root, "notes"), "cudart64_12.dll");

        var best = GpuLibraryDetector.BestCudaVersionDirectory(_root);

        Assert.Equal(Path.Combine(_root, "v12.4", "bin"), best);
    }

    [Fact]
    public void BestCudaVersionDirectory_NoneHasCudart_ReturnsNull()
    {
        Touch(Path.Combine(_root, "v12.4", "bin"), "README.txt");

        Assert.Null(GpuLibraryDetector.BestCudaVersionDirectory(_root));
    }

    [Fact]
    public void FindCudnnBinInTree_V9Layout_FindsArchFolderAtDepth3()
    {
        // CUDNN\vx.y\bin\12.x\x64\cudnn*.dll (the v9 zip layout).
        Touch(Path.Combine(_root, "v9.23", "bin", "12.9", "x64"), "cudnn64_9.dll");

        var found = GpuLibraryDetector.FindCudnnBinInTree(Path.Combine(_root, "v9.23"), maxDepth: 3);

        Assert.Equal(Path.Combine(_root, "v9.23", "bin", "12.9", "x64"), found);
    }

    [Fact]
    public void FindCudnnBinInTree_V8Layout_FindsArchFolderAtDepth2()
    {
        // CUDNN\vx.y\bin\x64\cudnn*.dll (the v8 layout).
        Touch(Path.Combine(_root, "v8.9", "bin", "x64"), "cudnn_ops64_8.dll");

        var found = GpuLibraryDetector.FindCudnnBinInTree(Path.Combine(_root, "v8.9"), maxDepth: 3);

        Assert.Equal(Path.Combine(_root, "v8.9", "bin", "x64"), found);
    }

    [Fact]
    public void FindCudnnBinInTree_BeyondMaxDepth_ReturnsNull()
    {
        // cudnn at depth 4 — outside the v9 layout scan (maxDepth 3).
        Touch(Path.Combine(_root, "v9", "bin", "12.9", "x64", "nested"), "cudnn64_9.dll");

        Assert.Null(GpuLibraryDetector.FindCudnnBinInTree(_root, maxDepth: 3));
    }

    [Theory]
    [InlineData("Windows", true)]
    [InlineData("ProgramData", true)]
    [InlineData("$Recycle.Bin", true)]
    [InlineData("System Volume Information", true)]
    [InlineData("Recovery", true)]
    [InlineData("PerfLogs", true)]
    [InlineData("cudnn", false)]
    [InlineData("NVIDIA", false)]
    public void IsSystemFolder_RecognizesSystemFolders(string name, bool expected)
    {
        Assert.Equal(expected, GpuLibraryDetector.IsSystemFolder(name));
    }

    [Fact]
    public void MarkerChecks_RecognizeBinContents()
    {
        var cudaDir = Path.Combine(_root, "cuda");
        Touch(cudaDir, "cudart64_12.dll");
        var cudnnDir = Path.Combine(_root, "cudnn");
        Touch(cudnnDir, "cudnn_ops64_9.dll");
        var emptyDir = Path.Combine(_root, "empty");
        Directory.CreateDirectory(emptyDir);

        Assert.True(GpuLibraryDetector.HasCudart(cudaDir));
        Assert.False(GpuLibraryDetector.HasCudart(emptyDir));
        Assert.True(GpuLibraryDetector.ContainsCudnn(cudnnDir));
        Assert.False(GpuLibraryDetector.ContainsCudnn(cudaDir));
        // A missing directory is not an error.
        Assert.False(GpuLibraryDetector.HasCudart(Path.Combine(_root, "no-such-dir")));
    }

    [Fact]
    public void DetectCudaPath_ReturnsNull_OrDirectoryWithCudart()
    {
        // Machine-dependent (registry / Program Files): only the result contract is checked.
        var found = GpuLibraryDetector.DetectCudaPath();

        if (found is not null)
            Assert.True(GpuLibraryDetector.HasCudart(found));
    }

    [Fact]
    public void DetectCudnnPathFast_ReturnsNull_OrDirectoryWithCudnn()
    {
        // Machine-dependent (Program Files layout / drive roots): only the result contract.
        var found = GpuLibraryDetector.DetectCudnnPathFast();

        if (found is not null)
            Assert.True(GpuLibraryDetector.ContainsCudnn(found));
    }
}