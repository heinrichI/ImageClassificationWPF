using System.IO;
using ImageClassification.Core.Services;
using ImageClassification.UI.Configuration;
using ImageClassification.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
 
namespace ImageClassification.UI;
 
public static class AppServiceFactory
{
    private static IHost? _host;
 
    public static IHost Host => _host ??= CreateHostBuilder().Build();
 
    public static IServiceProvider Services => Host.Services;
 
    private static IHostBuilder CreateHostBuilder() =>
        Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                // Opt-in file sink for performance diagnostics: set ICS_LOG_FILE to a path
                // (WPF has no console, so the default console logger is invisible).
                var logFile = Environment.GetEnvironmentVariable("ICS_LOG_FILE");
                if (!string.IsNullOrWhiteSpace(logFile))
                {
                    // Information+ into the file (Debug stays with the other providers only)
                    logging.AddProvider(new SimpleFileLoggerProvider(logFile, LogLevel.Information));
                }
            })
            .ConfigureServices((context, services) =>
            {
                // Library-level extensions encapsulate all concrete registrations
                services.AddArchiveReader();
                services.AddPdf();
                services.AddVectorStore();
                services.AddImageClassificationCore();

                // User settings: user-settings.json in the program directory, live (file watcher).
                // The store bridges to core via IOnnxRuntimeOptions / IGpuPipelineOptions;
                // the thumbnail pipeline and the settings view-model read it directly.
                services.AddSingleton<IUserSettingsStore, UserSettingsStore>();
                services.AddSingleton<IOnnxRuntimeOptions, UserSettingsOnnxBridge>();
                services.AddSingleton<IGpuPipelineOptions, UserSettingsGpuBridge>();

                // Thumbnail infrastructure (background loader + bounded cache)
                services.AddSingleton<Services.ThumbnailProvider>();

                // Copy comic search result images (in-memory extraction, no temp files)
                services.AddSingleton<Services.ImageCopyService>();
 
                // Windows
                services.AddTransient<MainWindow>();
 
                // ViewModels — Singleton so tabs share state
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<TrainViewModel>();
                services.AddSingleton<EvaluateViewModel>();
                services.AddSingleton<ClassifyViewModel>();
                services.AddSingleton<AnalyzeViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<ComicCoverSearchViewModel>();
                services.AddSingleton<SearchImageViewModel>();
            });
}

/// <summary>
/// Minimal line-oriented file logger used only when ICS_LOG_FILE is set (performance
/// diagnostics runs — WPF has no console for the default console logger). Appends
/// "HH:mm:ss.fff Level Message" lines; safe enough for diagnostic use, not a general sink.
/// </summary>
internal sealed class SimpleFileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly LogLevel _minLevel;
    private readonly object _lock = new();

    public SimpleFileLoggerProvider(string path, LogLevel minLevel)
    {
        _path = path;
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) => new SimpleFileLogger(_path, _minLevel, _lock);

    public void Dispose() { }

    private sealed class SimpleFileLogger : ILogger
    {
        private readonly string _path;
        private readonly LogLevel _minLevel;
        private readonly object _lock;

        public SimpleFileLogger(string path, LogLevel minLevel, object lockObject)
        {
            _path = path;
            _minLevel = minLevel;
            _lock = lockObject;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:HH:mm:ss.fff} {logLevel,-12} {formatter(state, exception)}" +
                       (exception is null ? string.Empty : $" | {exception.Message}");
            lock (_lock)
            {
                try { File.AppendAllText(_path, line + Environment.NewLine); }
                catch { /* diagnostics only — never break the app over logging */ }
            }
        }
    }
}
