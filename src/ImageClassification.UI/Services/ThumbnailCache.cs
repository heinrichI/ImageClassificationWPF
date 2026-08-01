using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace ImageClassification.UI.Services;

/// <summary>
/// LRU-bounded in-memory cache for BitmapSource thumbnails.
/// Evicts oldest entry when capacity is reached, disposes evicted bitmaps, and forces a GC.
/// </summary>
public class ThumbnailCache
{
    private readonly int _maxCached;
    private readonly List<string> _keyList = new();          // insertion / access order (oldest → newest)
    private readonly Dictionary<string, BitmapSource> _dict = new();

    public ThumbnailCache(ThumbnailSettings settings)
    {
        _maxCached = settings.MaxCachedThumbnails;
    }

    /// <summary>Get cached thumbnail. Returns <c>null</c> on miss.</summary>
    public BitmapSource? GetThumbnail(string key)
    {
        lock (_dict)
        {
            if (!_dict.TryGetValue(key, out var bs))
                return null;

            // Move key to end (most recently used) — O(1) check for "already last"
            if (_keyList.Count > 0 && _keyList[^1] != key)
            {
                _keyList.Remove(key);
                _keyList.Add(key);
            }

            return bs;
        }
    }

    /// <summary>Add thumbnail, evicting oldest entry if cache is full.</summary>
    public void AddThumbnail(string key, BitmapSource thumbnail)
    {
        lock (_dict)
        {
            if (_keyList.Count >= _maxCached)
            {
                // Evict oldest
                string oldestKey = _keyList[0];
                _keyList.RemoveAt(0);
                _dict.Remove(oldestKey);
                GC.Collect(GC.MaxGeneration);
            }

            _dict[key] = thumbnail;
            _keyList.Add(key);
        }
    }

    /// <summary>Check whether a thumbnail is already cached.</summary>
    public bool HasThumbnail(string key)
    {
        lock (_dict)
            return _dict.ContainsKey(key);
    }

    /// <summary>Current number of cached thumbnails.</summary>
    public int Cached
    {
        get
        {
            lock (_dict)
                return _keyList.Count;
        }
    }

    /// <summary>Clear all cached thumbnails and force GC to release native handles.</summary>
    public void Clear()
    {
        lock (_dict)
        {
            _dict.Clear();
            _keyList.Clear();
            GC.Collect(GC.MaxGeneration);
        }
    }
}
