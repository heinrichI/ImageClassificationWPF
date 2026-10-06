using ImageClassification.ArchiveReader;
using ImageClassification.Core.Interfaces;

namespace Microsoft.Extensions.DependencyInjection;

public static class ArchiveReaderServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IArchiveReader"/> facade and its container handlers:
    /// <c>ZipContainerHandler</c> (.cbz) and the <c>SevenZipContainerHandler</c> catch-all
    /// (.cbr/.cb7/.cbt/unknown). Additional handlers (e.g. <c>AddPdf()</c>) are picked up
    /// automatically through the <c>IEnumerable&lt;IContainerHandler&gt;</c> constructor argument;
    /// dispatch order is by handler Priority, not registration order.
    /// </summary>
    public static IServiceCollection AddArchiveReader(this IServiceCollection services)
    {
        services.AddSingleton<IContainerHandler, ArchiveReader.ZipContainerHandler>();
        services.AddSingleton<IContainerHandler, ArchiveReader.SevenZipContainerHandler>();
        services.AddSingleton<IArchiveReader, ArchiveReader>();

        return services;
    }
}