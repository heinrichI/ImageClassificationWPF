using System.Threading.Tasks;

namespace ImageClassification.Core.Interfaces;

/// <summary>
/// A handler that opens one comic-page container format (zip-based .cbz, 7z-based .cbr/.cb7/.cbt,
/// PDF, ...) as an <see cref="IArchiveSession"/>. Handlers are registered in the DI container as
/// <see cref="IContainerHandler"/>; the <see cref="IArchiveReader"/> facade dispatches to the first
/// handler whose <see cref="CanOpen"/> matches, trying lower <see cref="Priority"/> values first.
/// Adding a new container format = a new library registering a new handler; the facade and the
/// domain never change.
/// </summary>
public interface IContainerHandler
{
    /// <summary>
    /// Dispatch order, lower first (zip = 10, pdf = 20). Catch-all fallback handlers that also claim
    /// other formats' files (e.g. the 7z handler, which can read zip/tar containers) must use a much
    /// larger value (1000) so they are tried last.
    /// </summary>
    int Priority { get; }

    /// <summary>True if this handler can open the file at <paramref name="path"/>.</summary>
    bool CanOpen(string path);

    /// <summary>
    /// Opens the container. Implementations must not throw for missing/corrupt/protected files:
    /// return <see cref="EmptyArchiveSession"/> with a diagnostic <see cref="IArchiveSession.LastNote"/>.
    /// </summary>
    Task<IArchiveSession> OpenSessionAsync(string path);
}