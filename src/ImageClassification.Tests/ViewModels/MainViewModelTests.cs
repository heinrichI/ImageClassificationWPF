using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Services;
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
            new ImageClassification.UI.Configuration.ThumbnailSettings { ThumbnailWidth = 64, ThumbnailHeight = 96 },
            Mock.Of<IComicCoverSearchService>());
        return new ComicCoverSearchViewModel(
            Mock.Of<IComicCoverSearchService>(),
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
        var item = Assert.Single(main.ActiveMenuItems);
        Assert.Equal("Copy results images...", item.Header);

        main.SelectedTabIndex = 0;
        Assert.Empty(main.ActiveMenuItems);
    }
}
