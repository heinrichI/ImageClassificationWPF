using System;
using System.Threading.Tasks;

namespace ImageClassification.Core.Interfaces;

/// <summary>
/// A do-nothing <see cref="IArchiveSession"/> returned when a file exists but cannot be opened as a
/// comic container (unsupported/corrupt container, password-protected PDF, container mismatch).
/// <see cref="LastNote"/> carries a human-readable diagnostic surfaced to the UI.
/// Public (exception to the internal-implementation rule): it is a boundary contract shared by every
/// container-handler library.
/// </summary>
public sealed class EmptyArchiveSession : IArchiveSession
{
    public static readonly EmptyArchiveSession Instance = new();

    private readonly string? _note;

    public EmptyArchiveSession(string? note = null)
    {
        _note = note;
    }

    public static EmptyArchiveSession WithNote(string? note)
    {
        return string.IsNullOrEmpty(note) ? Instance : new EmptyArchiveSession(note);
    }

    public IReadOnlyList<string> ImageEntries => Array.Empty<string>();

    public string? LastNote => _note;

    public Task<byte[]?> ExtractToMemoryAsync(int index) => Task.FromResult<byte[]?>(null);

    public Task<byte[]?[]> ExtractAllToMemoryAsync() => Task.FromResult<byte[]?[]>(Array.Empty<byte[]?>());

    public void Dispose()
    {
    }
}