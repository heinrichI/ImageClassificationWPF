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
        private readonly ImageClassification.UI.Configuration.ThumbnailSettings _settings;
        private readonly IComicCoverSearchService _coverService;
        private readonly int _maxCacheItems = 500;
        private readonly ConcurrentDictionary<string, BitmapSource> _cache = new();
        private readonly ConcurrentQueue<(string Path, int Width, Action<BitmapSource?>? Callback)> _queue = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _worker;

        public ThumbnailProvider(ILogger<ThumbnailProvider> logger, ImageClassification.UI.Configuration.ThumbnailSettings settings, IComicCoverSearchService coverService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _coverService = coverService ?? throw new ArgumentNullException(nameof(coverService));
            _worker = Task.Run(WorkerAsync);
        }

        public void Enqueue(string path, int width, Action<BitmapSource?>? callback = null)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (_cache.ContainsKey(path)) return;
            _queue.Enqueue((path, width > 0 ? width : _settings.ThumbnailWidth, callback));
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

            if (_cache.TryGetValue(path, out var bs))
            {
                return bs;
            }

            // queue background load and return placeholder
            Enqueue(path, targetWidth, onLoaded);
            var ph = CreatePlaceholder(targetWidth, _settings.ThumbnailHeight);
            onLoaded?.Invoke(ph);
            return ph;
        }

        public void EnsureCoverAndEnqueue(string archivePath, Action<string?>? onCoverExtracted = null, Action<BitmapSource?>? onLoaded = null)
        {
            if (string.IsNullOrEmpty(archivePath)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var coverPath = await _coverService.ExtractCoverAsync(archivePath).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(coverPath))
                    {
                        if (onCoverExtracted is not null)
                        {
                            await Application.Current.Dispatcher.InvokeAsync(() => onCoverExtracted(null));
                        }
                        return;
                    }

                    // Notify UI about extracted cover path so caller may update item.CoverImagePath
                    if (onCoverExtracted is not null)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() => onCoverExtracted(coverPath));
                    }

                    // Enqueue loading of the extracted cover
                    Enqueue(coverPath, _settings.ThumbnailWidth, onLoaded);
                }
                catch (OperationCanceledException) { /* cancelled */ }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "EnsureCoverAndEnqueue failed for {ArchivePath}", archivePath);
                }
            });
        }

        public void Clear()
        {
            _cache.Clear();
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
                            var path = work.Path;
                            var width = work.Width;
                            var callback = work.Callback;

                            if (!File.Exists(path))
                            {
                                // notify null
                                if (callback is not null)
                                    await Application.Current.Dispatcher.BeginInvoke(new Action(() => callback(null)));
                                continue;
                            }

                            var image = new BitmapImage();
                            image.BeginInit();
                            image.CacheOption = BitmapCacheOption.OnLoad;
                            image.UriSource = new Uri(path);
                            image.DecodePixelWidth = width;
                            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                            image.EndInit();
                            image.Freeze();

                            _cache[path] = image;

                            if (callback is not null)
                                await Application.Current.Dispatcher.BeginInvoke(new Action(() => callback(image)));

                            // Simple eviction
                            if (_cache.Count > _maxCacheItems)
                            {
                                var oldest = _cache.Keys.GetEnumerator();
                                if (oldest.MoveNext())
                                {
                                    var key = oldest.Current;
                                    _cache.TryRemove(key, out _);
                                }
                            }
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