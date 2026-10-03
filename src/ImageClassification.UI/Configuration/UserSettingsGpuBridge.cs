using ImageClassification.Core.Services;
using ImageClassification.UI.Configuration;

namespace ImageClassification.UI.Configuration;

/// <summary>
/// Bridges the UI-level <see cref="IUserSettingsStore"/> to the core
/// <see cref="IGpuPipelineOptions"/> contract consumed by the batch encoder
/// and the comic search pipeline.
/// </summary>
internal sealed class UserSettingsGpuBridge : IGpuPipelineOptions
{
    private readonly IUserSettingsStore _store;

    public UserSettingsGpuBridge(IUserSettingsStore store)
    {
        _store = store;
    }

    public int BatchSize => _store.GpuBatchSize;
}