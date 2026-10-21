using ImageClassification.UI.ViewModels;
using Xunit;

namespace ImageClassification.Tests.ViewModels;

/// <summary>
/// Headless-safe coverage of the "GPU not available" dialog view-model: the fields
/// load from the settings store, Save persists them, and the status describes the
/// configured paths. The background auto-detection path is covered by
/// GpuLibraryDetectorTests (the deep detector scans drives, which tests avoid by
/// pre-populating both fields in the fake store).
/// </summary>
public class GpuFallbackViewModelTests
{
    [Fact]
    public void Ctor_LoadsStoredPaths_AndDescribesThem()
    {
        var store = new FakeUserSettingsStore
        {
            CudaPath = "C:\\CUDA\\v12\\bin",
            CudnnPath = "C:\\CUDNN\\v9\\bin\\x64"
        };

        var vm = new GpuFallbackViewModel(store);

        Assert.Equal(@"C:\CUDA\v12\bin", vm.CudaPath);
        Assert.Equal(@"C:\CUDNN\v9\bin\x64", vm.CudnnPath);
        Assert.Contains("CUDA:", vm.StatusText);
        Assert.Contains("cuDNN:", vm.StatusText);
        Assert.Contains("restarted", vm.StatusText);
    }

    [Fact]
    public void SaveCommand_PersistsCurrentFieldValues()
    {
        var store = new FakeUserSettingsStore
        {
            CudaPath = @"C:\old\cuda",
            CudnnPath = @"C:\old\cudnn"
        };
        var vm = new GpuFallbackViewModel(store);

        vm.CudaPath = @"C:\new\cuda";
        vm.CudnnPath = @"C:\new\cudnn";
        vm.SaveCommand.Execute(null);

        Assert.Equal(@"C:\new\cuda", store.CudaPath);
        Assert.Equal(@"C:\new\cudnn", store.CudnnPath);
        Assert.Contains("Saved", vm.StatusText);
    }
}