namespace ImageClassification.Core.Services;

/// <summary>
/// Ensures all model files exist on disk at startup.
/// </summary>
public interface IAppInitializationService
{
    /// <summary>
    /// Downloads any missing model files.
    /// </summary>
    Task InitializeAsync();
}
