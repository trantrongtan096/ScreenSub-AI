using System;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using ScreenSub.Capture;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;
using ScreenSub.Desktop.Services;
using ScreenSub.Infrastructure;
using ScreenSub.Ocr;
using ScreenSub.Translation;
using Xunit;

namespace IntegrationTests;

public class QuickTranslateAndLifecycleTests
{
    private static SubtitleCoordinator CreateCoordinator()
    {
        var settings = new SettingsService();
        var capture = new ScreenCaptureService();
        var changeDetector = new FrameChangeDetector();
        var ocr = new PaddleOcrEngine();
        var cache = new TranslationCache();
        var azure = new AzureTranslatorProvider(new HttpClient(), settings, cache);
        var mock = new MockTranslationProvider();

        return new SubtitleCoordinator(capture, ocr, azure, mock, settings, changeDetector);
    }

    [Fact]
    public void Coordinator_FullLifecycleStateMachine_ShouldFollowCorrectTransitions()
    {
        var coordinator = CreateCoordinator();

        // 1. Initial State: Idle
        coordinator.State.Should().Be(SystemState.Idle);

        // 2. User presses Hotkey -> SelectingRegion
        coordinator.NotifySelectingRegion();
        coordinator.State.Should().Be(SystemState.SelectingRegion);
        coordinator.PipelineStatusText.Should().Contain("Đang chọn vùng");

        // 3. User selects region -> Auto-Start (Starting -> Translating)
        var region = new ScreenRegion(100, 200, 800, 150);
        coordinator.UpdateSelectedRegionAndAutoStart(region);
        coordinator.State.Should().Be(SystemState.Translating);

        // 4. Pause translation
        coordinator.Pause();
        coordinator.State.Should().Be(SystemState.Paused);
        coordinator.PipelineStatusText.Should().Contain("Tạm dừng");

        // 5. Resume translation
        coordinator.Start();
        coordinator.State.Should().Be(SystemState.Translating);

        // 6. Stop translation
        coordinator.Stop();
        coordinator.State.Should().Be(SystemState.Stopped);
        coordinator.PipelineStatusText.Should().Contain("Đã dừng");
    }

    [Fact]
    public void Coordinator_SelectingRegion_WhenCancelledWhileTranslating_ShouldResumeTranslating()
    {
        var coordinator = CreateCoordinator();

        // Start active session
        var region1 = new ScreenRegion(100, 200, 600, 120);
        coordinator.UpdateSelectedRegionAndAutoStart(region1);
        coordinator.State.Should().Be(SystemState.Translating);

        // User triggers hotkey to select new region
        coordinator.NotifySelectingRegion();
        coordinator.State.Should().Be(SystemState.SelectingRegion);

        // User presses ESC to cancel
        coordinator.CancelSelectingRegion();

        // System must safely resume previous translating session
        coordinator.State.Should().Be(SystemState.Translating);
        coordinator.PipelineStatusText.Should().Contain("Tiếp tục");

        coordinator.Stop();
    }

    [Fact]
    public void Coordinator_SelectingRegion_WhenCancelledWhileIdle_ShouldReturnToPreviousState()
    {
        var coordinator = CreateCoordinator();
        coordinator.State.Should().Be(SystemState.Idle);

        coordinator.NotifySelectingRegion();
        coordinator.State.Should().Be(SystemState.SelectingRegion);

        coordinator.CancelSelectingRegion();
        coordinator.State.Should().Be(SystemState.Idle);
    }

    [Fact]
    public void Coordinator_AutoStart_ShouldGenerateNewSessionIdAndResetBuffers()
    {
        var coordinator = CreateCoordinator();
        string initialSession = coordinator.CurrentSessionId;

        var region = new ScreenRegion(300, 400, 700, 130);
        coordinator.UpdateSelectedRegionAndAutoStart(region);

        coordinator.CurrentSessionId.Should().NotBeNullOrWhiteSpace();
        coordinator.CurrentSessionId.Should().NotBe(initialSession);
        coordinator.State.Should().Be(SystemState.Translating);
        coordinator.CurrentSubtitle.HasContent.Should().BeFalse();

        coordinator.Stop();
    }

    [Fact]
    public void AppSettings_QuickTranslateAndTrayProperties_ShouldDefaultToTrue()
    {
        var settings = new AppSettings();

        settings.QuickTranslateMode.Should().BeTrue();
        settings.MinimizeToTrayOnClose.Should().BeTrue();
        settings.AutoPositionOverlayNearRegion.Should().BeTrue();
        settings.HasCompletedFirstRunGuide.Should().BeFalse();
        settings.OverlayWidth.Should().Be(1200);
    }

    [Fact]
    public void Coordinator_ResetTranslationCacheAndRetranslate_ShouldClearSubtitles()
    {
        var coordinator = CreateCoordinator();
        coordinator.ResetTranslationCacheAndRetranslate();
        coordinator.CurrentSubtitle.HasContent.Should().BeFalse();
    }
}

