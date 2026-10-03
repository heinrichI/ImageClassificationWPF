using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Services;
using ImageClassification.Tests;
using ImageClassification.UI.Configuration;
using ImageClassification.UI.Services;
using ImageClassification.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ImageClassification.Tests.ViewModels;

public class MainViewModelTests
{
    private static ComicCoverSearchViewModel CreateComicVm()
    {
        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);
        var thumbnailProvider = new ThumbnailProvider(
            NullLogger<ThumbnailProvider>.Instance,
            new FakeUserSettingsStore { ThumbnailWidth = 64, ThumbnailHeight = 96 },
            Mock.Of<IComicCoverSearchService>());
        return new ComicCoverSearchViewModel(
            Mock.Of<IComicCoverSearchService>(),
            Mock.Of<IVectorStore>(),
            NullLogger<ComicCoverSearchViewModel>.Instance,
            thumbnailProvider,
            copyService);
    }

    [Fact]
    public void ActiveMenuItems_Empty_ForTabsWithoutProvider()
    {
        var main = new MainViewModel(null!, null!, null!, null!, null!, CreateComicVm());

        Assert.Empty(main.ActiveMenuItems);
    }

    [Fact]
    public void ActiveMenuItems_Rebuilds_WhenSelectedTabChanges()
    {
        var main = new MainViewModel(null!, null!, null!, null!, null!, CreateComicVm());

        main.SelectedTabIndex = 5;
        var items = main.ActiveMenuItems.ToList();
        // Two group headers: Copy and DB
        Assert.Equal(2, items.Count);
        Assert.Equal("Copy", items[0].Header);
        Assert.Equal("DB", items[1].Header);
        Assert.Null(items[0].Command);

        var copyItems = items[0].Children!;
        Assert.Single(copyItems);
        Assert.Equal("Copy results images...", copyItems[0].Header);

        var dbItems = items[1].Children!;
        Assert.Equal(3, dbItems.Count);
        Assert.Equal("Vector cache: Info...", dbItems[0].Header);
        Assert.Equal("Vector cache: Optimize", dbItems[1].Header);
        Assert.Equal("Vector cache: Vacuum...", dbItems[2].Header);

        main.SelectedTabIndex = 0;
        Assert.Empty(main.ActiveMenuItems);
    }
}
