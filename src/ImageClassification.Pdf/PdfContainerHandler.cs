using ImageClassification.Core.Interfaces;

namespace ImageClassification.Pdf;

/// <summary>
/// Container handler for PDF files. The handler never throws: unopenable/corrupt/encrypted
/// documents become an <see cref="EmptyArchiveSession"/> with a diagnostic note, so the search
/// pipeline can skip the file like any other bad archive.
/// </summary>
internal sealed class PdfContainerHandler : IContainerHandler
{
    public int Priority => 20;

    public bool CanOpen(string path) => path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    public Task<IArchiveSession> OpenSessionAsync(string path)
    {
        try
        {
            return Task.FromResult<IArchiveSession>(new PdfComicSession(path));
        }
        catch (Exception ex)
        {
            return Task.FromResult<IArchiveSession>(EmptyArchiveSession.WithNote(
                $"{Path.GetFullPath(path)}: {ex.Message} — skipped as empty"));
        }
    }
}