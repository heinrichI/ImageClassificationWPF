using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageClassification.Core.Interfaces;
using Xunit;
using ArchiveReaderFacade = ImageClassification.ArchiveReader.ArchiveReader;

namespace ImageClassification.Tests.Services;

/// <summary>
/// Tests the IArchiveReader facade as a container-handler dispatcher: Priority-ordered
/// dispatch, catch-all fallback, and the missing-file short circuit.
/// </summary>
public sealed class ArchiveReaderDispatcherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ics_disp_test_").FullName;

    private sealed class FakeHandler : IContainerHandler
    {
        private readonly string? _extension;

        public FakeHandler(int priority, string? extension)
        {
            Priority = priority;
            _extension = extension;
        }

        public int Priority { get; }

        public bool CanOpen(string path) =>
            _extension is null || path.EndsWith(_extension, StringComparison.OrdinalIgnoreCase);

        public Task<IArchiveSession> OpenSessionAsync(string path) =>
            Task.FromResult<IArchiveSession>(EmptyArchiveSession.WithNote($"fake:{Priority}"));
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    [Fact]
    public async Task Dispatches_ByPriority_NotRegistrationOrder()
    {
        // The catch-all fallback is registered FIRST, but its priority (1000) must lose
        // to the specific .pdf handler (20) — mirroring AddArchiveReader() + AddPdf().
        var handlers = new IContainerHandler[]
        {
            new FakeHandler(1000, null),
            new FakeHandler(20, ".pdf"),
        };

        var reader = new ArchiveReaderFacade(handlers);

        using var session = await reader.OpenSessionAsync(CreateFile("comic.pdf"));
        Assert.Equal("fake:20", session.LastNote);
    }

    [Fact]
    public async Task SpecificHandlerWinsOverCatchAll_ForOtherFormats()
    {
        var handlers = new IContainerHandler[]
        {
            new FakeHandler(10, ".cbz"),
            new FakeHandler(1000, null),
        };

        var reader = new ArchiveReaderFacade(handlers);

        using var session = await reader.OpenSessionAsync(CreateFile("comic.cbz"));
        Assert.Equal("fake:10", session.LastNote);

        using var other = await reader.OpenSessionAsync(CreateFile("comic.cb7"));
        Assert.Equal("fake:1000", other.LastNote);
    }

    [Fact]
    public async Task MissingFile_ReturnsSharedEmptySession()
    {
        var reader = new ArchiveReaderFacade(new IContainerHandler[] { new FakeHandler(1000, null) });

        using var session = await reader.OpenSessionAsync(Path.Combine(_dir, "absent.cbz"));

        Assert.Same(EmptyArchiveSession.Instance, session);
    }

    [Fact]
    public async Task HandlerThatThrows_IsContainedAsEmptySession()
    {
        var reader = new ArchiveReaderFacade(new IContainerHandler[] { new ThrowingHandler() });

        using var session = await reader.OpenSessionAsync(CreateFile("boom.cbz"));

        Assert.Empty(session.ImageEntries);
        Assert.Contains("deliberate", session.LastNote, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingHandler : IContainerHandler
    {
        public int Priority => 10;

        public bool CanOpen(string path) => true;

        public Task<IArchiveSession> OpenSessionAsync(string path) =>
            throw new InvalidOperationException("deliberate failure");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}