using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ScreenSub.Audio.Capture;
using ScreenSub.Audio.VAD;
using ScreenSub.Capture;
using ScreenSub.Core.Interfaces;
using ScreenSub.Desktop.Services;
using ScreenSub.Desktop.ViewModels;
using ScreenSub.Desktop.Views;
using ScreenSub.Infrastructure;
using ScreenSub.Ocr;
using ScreenSub.Overlay;
using ScreenSub.Speech.Whisper;
using ScreenSub.Translation;
using Serilog;

namespace ScreenSub.Desktop;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Initialize Serilog with Daily Log Rotation & 10MB File Size Limits
        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenSubAI", "Logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                path: Path.Combine(logDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 7,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("=================================================");
        Log.Information("ScreenSub AI Windows Beta v1.0.0 Starting Up");
        Log.Information("Log Directory: {LogDir}", logDir);
        Log.Information("=================================================");

        // 2. Global Exception Trapping
        DispatcherUnhandledException += (s, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled UI Dispatcher Exception.");
            MessageBox.Show($"Đã xảy ra lỗi không mong muốn: {args.Exception.Message}\nChi tiết xem tại: {logDir}",
                "ScreenSub AI Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "Unhandled AppDomain Fatal Exception.");
            }
        };

        AppDomain.CurrentDomain.ProcessExit += (s, args) =>
        {
            Log.Information("ProcessExit signal received. Flushing logs and terminating child processes.");
            Log.CloseAndFlush();
        };

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // Load Settings on startup
        var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
        await settingsService.LoadSettingsAsync();

        // Warm up / Pre-initialize OCR & Neural Translation workers in background
        _ = Task.Run(async () =>
        {
            try
            {
                var ocrEngine = _serviceProvider.GetService<IOcrEngine>();
                if (ocrEngine != null)
                {
                    await ocrEngine.InitializeAsync();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Background OCR Engine warm-up encountered an issue");
            }

            try
            {
                if (LocalNeuralTranslationEngine.IsAnyTranslationModelInstalledOnDisk())
                {
                    var neuralEngine = _serviceProvider.GetService<LocalNeuralTranslationEngine>();
                    if (neuralEngine != null)
                    {
                        await neuralEngine.InitializeAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Background Neural Translation Engine warm-up encountered an issue");
            }
        });

        // Show MainWindow
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Core Services
        services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();
        services.AddSingleton<IFrameChangeDetector, FrameChangeDetector>();
        services.AddSingleton<ISettingsService, SettingsService>();

        // OCR Engine
        services.AddSingleton<IOcrEngine, PaddleOcrEngine>();

        // Audio & Speech Services (Phase 8)
        services.AddSingleton<IAudioCaptureService, WasapiAudioCaptureService>();
        services.AddSingleton<IVoiceActivityDetector, EnergyVoiceActivityDetector>();
        services.AddSingleton<ISpeechRecognitionService, WhisperSpeechRecognitionService>();

        // Translation Services (Online, Offline & Hybrid Resolver)
        services.AddSingleton<HttpClient>();
        services.AddSingleton<TranslationCache>();
        services.AddSingleton(sp => new LocalNeuralTranslationEngine(sp.GetRequiredService<ISettingsService>()));
        services.AddSingleton<LocalTranslationProvider>();
        services.AddSingleton<AzureTranslatorProvider>();
        services.AddSingleton<GeminiTranslationProvider>();
        services.AddSingleton<MockTranslationProvider>();
        services.AddSingleton<ITranslationProviderResolver, TranslationProviderResolver>();

        // Hardware & Model Management (Phase 9)
        services.AddSingleton<IHardwareDetector, HardwareCapabilityDetector>();
        services.AddSingleton<IModelManager, LocalModelManager>();

        // Pipeline Coordinator
        services.AddSingleton<ISubtitleCoordinator>(sp =>
        {
            var capture = sp.GetRequiredService<IScreenCaptureService>();
            var ocr = sp.GetRequiredService<IOcrEngine>();
            var resolver = sp.GetRequiredService<ITranslationProviderResolver>();
            var settings = sp.GetRequiredService<ISettingsService>();
            var changeDetector = sp.GetRequiredService<IFrameChangeDetector>();
            var audioCapture = sp.GetRequiredService<IAudioCaptureService>();
            var vad = sp.GetRequiredService<IVoiceActivityDetector>();
            var speech = sp.GetRequiredService<ISpeechRecognitionService>();

            return new SubtitleCoordinator(capture, ocr, resolver, settings, changeDetector, audioCapture, vad, speech);
        });

        // Overlay Window & Dialog Factories
        services.AddSingleton<SubtitleOverlayWindow>();
        services.AddTransient<Func<RegionSelectionWindow>>(_ => () => new RegionSelectionWindow());

        // ViewModels & Windows
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_serviceProvider != null)
        {
            var coordinator = _serviceProvider.GetService<ISubtitleCoordinator>();
            if (coordinator != null)
            {
                await coordinator.DisposeAsync();
            }

            var ocr = _serviceProvider.GetService<IOcrEngine>();
            if (ocr != null)
            {
                await ocr.DisposeAsync();
            }

            var speech = _serviceProvider.GetService<ISpeechRecognitionService>();
            if (speech != null)
            {
                await speech.DisposeAsync();
            }

            var audioCapture = _serviceProvider.GetService<IAudioCaptureService>();
            if (audioCapture != null)
            {
                if (audioCapture is IAsyncDisposable asyncAudio)
                {
                    await asyncAudio.DisposeAsync();
                }
                else
                {
                    audioCapture.Dispose();
                }
            }

            var neuralEngine = _serviceProvider.GetService<LocalNeuralTranslationEngine>();
            if (neuralEngine != null)
            {
                await neuralEngine.DisposeAsync();
            }

            _serviceProvider.Dispose();
        }

        Log.Information("ScreenSub AI exited cleanly.");
        Log.CloseAndFlush();

        base.OnExit(e);
    }
}
