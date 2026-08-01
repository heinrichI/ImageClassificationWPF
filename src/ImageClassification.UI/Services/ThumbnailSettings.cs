namespace ImageClassification.UI.Services;

/// <summary>
/// Settings for the thumbnail generation and caching system.
/// </summary>
public class ThumbnailSettings
{
    /// <summary>Default thumbnail width in pixels. Default: 180.</summary>
    public int ThumbnailWidth { get; set; } = 180;

    /// <summary>Maximum number of thumbnails kept in the LRU cache. Default: 400.</summary>
    public int MaxCachedThumbnails { get; set; } = 400;

    /// <summary>Maximum number of items allowed in the background loading queue. Default: 500.</summary>
    public int MaxQueueSize { get; set; } = 500;
}
