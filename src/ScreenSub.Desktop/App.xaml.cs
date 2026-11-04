using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ScreenSub.Capture;
using ScreenSub.Core.Interfaces;
using ScreenSub.Desktop.Services;
using ScreenSub.Desktop.ViewModels;
using ScreenSub.Desktop.Views;
using ScreenSub.Infrastructure;
using ScreenSub.Ocr;
using ScreenSub.Overlay;
using ScreenSub.Translation;

namespace ScreenSub.Desktop;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // Load Settings on startup
        var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
        await settingsService.LoadSettingsAsync();

        // Show MainWindow
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
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

        // Translation Services
        services.AddSingleton<HttpClient>();
        services.AddSingleton<TranslationCache>();
        services.AddSingleton<AzureTranslatorProvider>();
        services.AddSingleton<MockTranslationProvider>();

        // Pipeline Coordinator
        services.AddSingleton<ISubtitleCoordinator>(sp =>
        {
            var capture = sp.GetRequiredService<IScreenCaptureService>();
            var ocr = sp.GetRequiredService<IOcrEngine>();
            var azure = sp.GetRequiredService<AzureTranslatorProvider>();
            var mock = sp.GetRequiredService<MockTranslationProvider>();
            var settings = sp.GetRequiredService<ISettingsService>();
            var changeDetector = sp.GetRequiredService<IFrameChangeDetector>();

            return new SubtitleCoordinator(capture, ocr, azure, mock, settings, changeDetector);
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

            _serviceProvider.Dispose();
        }

        base.OnExit(e);
    }
}
