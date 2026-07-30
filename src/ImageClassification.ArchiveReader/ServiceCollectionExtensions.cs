using ImageClassification.ArchiveReader;
using ImageClassification.Core.Interfaces;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering archive reader services in the DI container.
/// </summary>
public static class ArchiveReaderServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IArchiveReader"/> as a singleton service.
    /// </summary>
    public static IServiceCollection AddArchiveReader(this IServiceCollection services)
    {
        services.AddSingleton<IArchiveReader, ArchiveReader>();
        return services;
    }
}