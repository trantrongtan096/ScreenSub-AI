using FluentAssertions;
using ScreenSub.Capture;
using ScreenSub.Core.Models;
using ScreenSub.Ocr;
using Xunit;

namespace UnitTests;

public class TextNormalizerTests
{
    private readonly TextNormalizer _normalizer = new();

    [Fact]
    public void NormalizeAndMergeLines_ShouldSortLinesByVerticalAndHorizontalReadingOrder()
    {
        // Arrange: lines out of order
        var lines = new List<OcrLine>
        {
            new() { Text = "rảnh không?", Left = 120, Top = 50, Right = 200, Bottom = 70 },
            new() { Text = "Hôm nay bạn có", Left = 10, Top = 52, Right = 110, Bottom = 72 },
            new() { Text = "你今天有时间吗？", Left = 10, Top = 10, Right = 150, Bottom = 30 }
        };

        // Act
        var result = _normalizer.NormalizeAndMergeLines(lines);

        // Assert
        result.Should().Contain("你今天有时间吗？");
        result.Should().Contain("Hôm nay bạn có rảnh không?");
    }

    [Fact]
    public void NormalizeAndMergeLines_WithEmptyList_ShouldReturnEmptyString()
    {
        var result = _normalizer.NormalizeAndMergeLines([]);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplyStabilityFilter_IdenticalText_ShouldNotSignalChange()
    {
        var (t1, c1) = _normalizer.ApplyStabilityFilter("你今天有时间吗？");
        c1.Should().BeTrue();
        t1.Should().Be("你今天有时间吗？");

        // Second time identical
        var (t2, c2) = _normalizer.ApplyStabilityFilter("你今天有时间吗？");
        c2.Should().BeFalse();
        t2.Should().Be("你今天有时间吗？");
    }

    [Fact]
    public void ApplyStabilityFilter_MinorJitterWithinDebounce_ShouldMaintainStableText()
    {
        _normalizer.ApplyStabilityFilter("你今天有时间吗？");

        // Very similar OCR jitter (90% similarity)
        var (t2, c2) = _normalizer.ApplyStabilityFilter("你今天有时间吗?", debounceMs: 500);

        t2.Should().Be("你今天有时间吗？");
        c2.Should().BeFalse();
    }
}

