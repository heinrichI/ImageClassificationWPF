using ImageClassification.Core.Services;
using ImageClassification.UI.Configuration;

namespace ImageClassification.UI.Configuration;

/// <summary>
/// Bridges the UI-level <see cref="IUserSettingsStore"/> to the core
/// <see cref="IOnnxRuntimeOptions"/> contract consumed by ONNX session creation sites.
/// </summary>
internal sealed class UserSettingsOnnxBridge : IOnnxRuntimeOptions
{
    private readonly IUserSettingsStore _store;

    public UserSettingsOnnxBridge(IUserSettingsStore store)
    {
        _store = store;
    }

    public int ThreadCount => _store.OnnxThreadCount;
}