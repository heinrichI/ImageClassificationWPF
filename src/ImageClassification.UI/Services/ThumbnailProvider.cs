using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageClassification.Core.Services;
using ImageClassification.UI.Configuration;
using Microsoft.Extensions.Logging;

namespace ImageClassification.UI.Services
{
    public class ThumbnailProvider : IDisposable
    {
        private readonly ILogger<ThumbnailProvider> _logger;
        private readonly IUserSettingsStore _settings;
        private readonly IComicCoverSearchService _coverService;
        // LRU-bounded in-memory thumbnail cache: _cacheOrder holds keys from least to
        // recently used; a cache access moves the key to the end (hot thumbnails survive
        // eviction) and entries beyond MaxCachedThumbnails are evicted from the front.
        // All access happens under _cacheLock (UI thread reads, worker thread adds).
        private readonly object _cacheLock = new();
        private readonly Dictionary<string, BitmapSource> _cache = new();
        private readonly List<string> _cacheOrder = new();
        private readonly ConcurrentQueue<(string Key, byte[]? Data, int Width, Action<BitmapSource?>? Callback)> _queue = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _worker;

        public ThumbnailProvider(ILogger<ThumbnailProvider> logger, IUserSettingsStore settings, IComicCoverSearchService coverService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _coverService = coverService ?? throw new ArgumentNullException(nameof(coverService));
            _worker = Task.Run(WorkerAsync);
        }

        public void Enqueue(string path, int width, Action<BitmapSource?>? callback = null)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (CacheTryGet(path, out _)) return; // already loaded — the access marks it hot
            _queue.Enqueue((path, null, width > 0 ? width : _settings.ThumbnailWidth, callback));
        }

        public BitmapSource? GetBitmap(string path, int width, Action<BitmapSource?>? onLoaded = null)
        {
            var targetWidth = width > 0 ? width : _settings.ThumbnailWidth;

            if (string.IsNullOrEmpty(path))
            {
                var placeholder = CreatePlaceholder(targetWidth, _settings.ThumbnailHeight);
                onLoaded?.Invoke(placeholder);
                return placeholder;
            }

            if (CacheTryGet(path, out var bs))
            {
                return bs;
            }

            // queue background load and return placeholder
            Enqueue(path, targetWidth, onLoaded);
            var ph = CreatePlaceholder(targetWidth, _settings.ThumbnailHeight);
            onLoaded?.Invoke(ph);
            return ph;
        }

        /// <summary>
        /// Extracts the page image (page index 0 = cover) of <paramref name="archivePath"/>
        /// into memory and enqueues its decode. The thumbnail cache key is page-specific —
        /// "{path}" for the cover, "{path}|page=N" otherwise (same convention as the
        /// embedding cache keys) — so pages of one archive never alias each other.
        /// </summary>
        public void EnsurePageAndEnqueue(string archivePath, int pageIndex, Action<BitmapSource?>? onLoaded = null)
        {
            if (string.IsNullOrEmpty(archivePath)) return;
            if (pageIndex < 0) pageIndex = 0;

            string cacheKey = pageIndex == 0 ? archivePath : $"{archivePath}|page={pageIndex}";

            _ = Task.Run(async () =>
            {
                try
                {
                    if (CacheTryGet(cacheKey, out _)) return;

                    var bytes = await _coverService.ExtractPageAsync(archivePath, pageIndex).ConfigureAwait(false);
                    if (bytes is null || bytes.Length == 0)
                    {
                        if (onLoaded is not null)
                        {
                            await Application.Current.Dispatcher.InvokeAsync(() => onLoaded(null));
                        }
                        return;
                    }

                    // Enqueue decoding of the in-memory page bytes
                    _queue.Enqueue((cacheKey, bytes, _settings.ThumbnailWidth, onLoaded));
                }
                catch (OperationCanceledException) { /* cancelled */ }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "EnsurePageAndEnqueue failed for {ArchivePath} page {PageIndex}", archivePath, pageIndex);
                }
            });
        }

        public void Clear()
        {
            lock (_cacheLock)
            {
                _cache.Clear();
                _cacheOrder.Clear();
            }
        }

        /// <summary>LRU get: a hit moves the key to the most-recently-used end.</summary>
        private bool CacheTryGet(string key, out BitmapSource? image)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var hit))
                {
                    _cacheOrder.Remove(key);
                    _cacheOrder.Add(key);
                    image = hit;
                    return true;
                }
                image = null;
                return false;
            }
        }

        /// <summary>Adds an entry and evicts the least recently used entries beyond the limit.</summary>
        private void CacheAdd(string key, BitmapSource image, int maxCached)
        {
            lock (_cacheLock)
            {
                if (_cache.ContainsKey(key))
                {
                    _cache[key] = image;
                    _cacheOrder.Remove(key);
                    _cacheOrder.Add(key);
                    return;
                }

                _cache[key] = image;
                _cacheOrder.Add(key);

                while (maxCached > 0 && _cache.Count > maxCached)
                {
                    string oldest = _cacheOrder[0];
                    _cacheOrder.RemoveAt(0);
                    _cache.Remove(oldest);
                }
            }
        }

        private BitmapSource CreatePlaceholder(int width, int height)
        {
            int finalW = Math.Max(8, width);
            int finalH = Math.Max(8, height > 0 ? height : (int)(finalW * 1.333));

            try
            {
                var dpi = 96;
                var pixelFormat = PixelFormats.Pbgra32;
                var stride = (finalW * pixelFormat.BitsPerPixel + 7) / 8;
                var pixels = new byte[stride * finalH];

                // Fill background with checkerboard-like pattern
                for (int y = 0; y < finalH; y++)
                {
                    for (int x = 0; x < finalW; x++)
                    {
                        bool dark = ((x / 8) + (y / 8)) % 2 == 0;
                        var offset = y * stride + x * 4;
                        if (dark)
                        {
                            pixels[offset + 0] = 0xE6; // B
                            pixels[offset + 1] = 0xE6; // G
                            pixels[offset + 2] = 0xE6; // R
                            pixels[offset + 3] = 0xFF; // A
                        }
                        else
                        {
                            pixels[offset + 0] = 0xFF;
                            pixels[offset + 1] = 0xFF;
                            pixels[offset + 2] = 0xFF;
                            pixels[offset + 3] = 0xFF;
                        }
                    }
                }

                var bmp = BitmapSource.Create(finalW, finalH, dpi, dpi, pixelFormat, null, pixels, stride);
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[4], 4);
            }
        }

        private async Task WorkerAsync()
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    if (_queue.TryDequeue(out var work))
                    {
                        try
                        {
                            var key = work.Key;
                            var data = work.Data;
                            var width = work.Width;
                            var callback = work.Callback;

                            BitmapSource? image;
                            if (data is not null)
                            {
                                // Cover extracted in memory — decode from bytes (OnLoad fully decodes, stream can close)
                                using var stream = new MemoryStream(data);
                                var bmp = new BitmapImage();
                                bmp.BeginInit();
                                bmp.CacheOption = BitmapCacheOption.OnLoad;
                                bmp.StreamSource = stream;
                                bmp.DecodePixelWidth = width;
                                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                                bmp.EndInit();
                                bmp.Freeze();
                                image = bmp;
                            }
                            else
                            {
                                if (!File.Exists(key))
                                {
                                    // notify null
                                    if (callback is not null)
                                        await Application.Current.Dispatcher.BeginInvoke(new Action(() => callback(null)));
                                    continue;
                                }

                                var bmp = new BitmapImage();
                                bmp.BeginInit();
                                bmp.CacheOption = BitmapCacheOption.OnLoad;
                                bmp.UriSource = new Uri(key);
                                bmp.DecodePixelWidth = width;
                                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                                bmp.EndInit();
                                bmp.Freeze();
                                image = bmp;
                            }

                            CacheAdd(key, image, _settings.MaxCachedThumbnails);

                            if (callback is not null)
                                await Application.Current.Dispatcher.BeginInvoke(new Action(() => callback(image)));
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Thumbnail load failed");
                        }
                    }
                    else
                    {
                        await Task.Delay(250, _cts.Token);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _worker.Wait(2000); } catch { }
        }
    }
}