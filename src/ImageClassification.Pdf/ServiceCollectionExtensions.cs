using ImageClassification.Core.Interfaces;
using ImageClassification.Pdf;

namespace Microsoft.Extensions.DependencyInjection;

public static class PdfServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PDF container handler (<see cref="PdfContainerHandler"/>). The handler is
    /// picked up by the <c>IArchiveReader</c> facade through its
    /// <c>IEnumerable&lt;IContainerHandler&gt;</c> constructor argument; dispatch order is by
    /// handler Priority (pdf = 20, before the 7z catch-all fallback), not registration order.
    /// </summary>
    public static IServiceCollection AddPdf(this IServiceCollection services)
    {
        services.AddSingleton<IContainerHandler, PdfContainerHandler>();

        return services;
    }
}