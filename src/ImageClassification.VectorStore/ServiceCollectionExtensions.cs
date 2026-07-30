using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Services;
using ImageClassification.VectorStore;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering vector store services in the DI container.
/// </summary>
public static class VectorStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IVectorStore"/> as a singleton backed by SQLite.
    /// </summary>
    public static IServiceCollection AddVectorStore(this IServiceCollection services)
    {
        services.AddSingleton<IVectorStore, SqliteVectorStore>();
        return services;
    }
}
