namespace ImageClassification.UI.ViewModels;

/// <summary>
/// Implemented by view models whose results-grid tiles lazy-load thumbnails the moment
/// a tile becomes visible (VirtualizingWrapPanel + item template behavior). The
/// behavior calls <see cref="RequestThumbnail"/> with the tile's data context; the view
/// model knows how to queue (or priority-bump) that item in the shared thumbnail
/// pipeline. This is the port between the UI behavior and the view models, so the
/// behavior never references a concrete view-model or item type.
/// </summary>
public interface IThumbnailRequester
{
    /// <summary>Queues (or bumps) the thumbnail of the given result item.</summary>
    void RequestThumbnail(object? item);
}