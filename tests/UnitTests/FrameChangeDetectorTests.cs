using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using ScreenSub.Capture;
using Xunit;

namespace UnitTests;

public class FrameChangeDetectorTests
{
    private readonly FrameChangeDetector _detector = new();

    private static byte[] RenderTextImage(string text, int width = 300, int height = 60)
    {
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Arial"),
                24,
                Brushes.White,
                1.0);
            dc.DrawText(ft, new Point(10, 15));
        }
        rtb.Render(dv);

        using var ms = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        enc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void HasFrameChanged_FirstFrame_ShouldReturnTrue()
    {
        byte[] frame = RenderTextImage("Initial Frame");
        bool changed = _detector.HasFrameChanged(frame);
        changed.Should().BeTrue();
    }

    [Fact]
    public void HasFrameChanged_IdenticalFrame_ShouldReturnFalse()
    {
        byte[] frame = RenderTextImage("Static Frame");

        _detector.HasFrameChanged(frame); // first
        bool second = _detector.HasFrameChanged(frame); // identical
        second.Should().BeFalse();
    }

    [Fact]
    public void HasFrameChanged_DifferentSubtitles_ShouldDetectChange()
    {
        byte[] frameA = RenderTextImage("Hello everyone");
        byte[] frameB = RenderTextImage("Welcome back");

        var detector = new FrameChangeDetector();
        detector.HasFrameChanged(frameA).Should().BeTrue(); // First frame
        bool changed = detector.HasFrameChanged(frameB); // Different subtitle text
        changed.Should().BeTrue();
    }

    [Fact]
    public void HasFrameChanged_SameImageReEncoded_ShouldReturnFalse()
    {
        byte[] frameA1 = RenderTextImage("Hello everyone");
        byte[] frameA2 = RenderTextImage("Hello everyone"); // Same pixels

        var detector = new FrameChangeDetector();
        detector.HasFrameChanged(frameA1).Should().BeTrue();
        bool changed = detector.HasFrameChanged(frameA2);
        changed.Should().BeFalse();
    }

    [Fact]
    public void Reset_ShouldForceNextFrameAsChanged()
    {
        byte[] frame = RenderTextImage("Reset Test Frame");

        _detector.HasFrameChanged(frame);
        _detector.Reset();
        bool changed = _detector.HasFrameChanged(frame);
        changed.Should().BeTrue();
    }

    private static byte[] RenderWideBannerImage(string text, byte textAlpha = 255, byte bgShade = 16, int width = 1920, int height = 180, double fontSize = 20)
    {
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bgBrush = new SolidColorBrush(Color.FromRgb(bgShade, (byte)(bgShade + 4), (byte)(bgShade + 10)));
            dc.DrawRectangle(bgBrush, null, new Rect(0, 0, width, height));
            if (!string.IsNullOrEmpty(text))
            {
                var fgBrush = new SolidColorBrush(Color.FromArgb(textAlpha, 248, 250, 252));
                var ft = new FormattedText(
                    text,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    fontSize,
                    fgBrush,
                    1.0);
                double x = (width - ft.Width) / 2.0;
                double y = (height - ft.Height) / 2.0;
                dc.DrawText(ft, new Point(x, y));
            }
        }
        rtb.Render(dv);

        using var ms = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        enc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void HasFrameChanged_SmallLocalizedSubtitleOnWideBanner_ShouldDetectChange()
    {
        var detector = new FrameChangeDetector();
        byte[] blank = RenderWideBannerImage(string.Empty);
        byte[] shortSub = RenderWideBannerImage("Why?", textAlpha: 255, width: 1920, height: 180, fontSize: 22);

        detector.HasFrameChanged(blank, 0.03).Should().BeTrue();
        detector.HasFrameChanged(shortSub, 0.03).Should().BeTrue("localized block check must detect short subtitles on wide HD banners without threshold misses");
    }

    [Fact]
    public void HasFrameChanged_GradualFadeIn_ShouldDetectOnceVisibleAgainstBaseline()
    {
        var detector = new FrameChangeDetector();
        byte[] blank = RenderWideBannerImage(string.Empty);
        byte[] fadeTiny = RenderWideBannerImage("Hold on a moment.", textAlpha: 25, width: 1280, height: 180, fontSize: 28);
        byte[] fadeMid = RenderWideBannerImage("Hold on a moment.", textAlpha: 140, width: 1280, height: 180, fontSize: 28);
        byte[] fadeFull = RenderWideBannerImage("Hold on a moment.", textAlpha: 255, width: 1280, height: 180, fontSize: 28);

        detector.HasFrameChanged(blank, 0.03).Should().BeTrue();
        _ = detector.HasFrameChanged(fadeTiny, 0.03); // below threshold -> must NOT overwrite blank baseline
        bool detectedMid = detector.HasFrameChanged(fadeMid, 0.03);
        bool detectedFull = detector.HasFrameChanged(fadeFull, 0.03);

        (detectedMid || detectedFull).Should().BeTrue("fading-in subtitle must trigger as opacity rises against unchanged baseline");
    }

    [Fact]
    public void HasFrameChanged_MultiLineAndSceneCut_ShouldDetectChangesCleanly()
    {
        var detector = new FrameChangeDetector();
        byte[] singleLine = RenderWideBannerImage("First line", bgShade: 16, width: 1280, height: 200, fontSize: 30);
        byte[] multiLine = RenderWideBannerImage("First line\nSecond subtitle line here", bgShade: 16, width: 1280, height: 200, fontSize: 30);
        byte[] hardSceneCut = RenderWideBannerImage("First line\nSecond subtitle line here", bgShade: 110, width: 1280, height: 200, fontSize: 30);

        detector.HasFrameChanged(singleLine, 0.03).Should().BeTrue();
        detector.HasFrameChanged(multiLine, 0.03).Should().BeTrue();
        detector.HasFrameChanged(hardSceneCut, 0.03).Should().BeTrue();
        detector.HasFrameChanged(hardSceneCut, 0.03).Should().BeFalse();
    }
}

