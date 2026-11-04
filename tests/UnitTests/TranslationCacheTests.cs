using FluentAssertions;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;
using ScreenSub.Translation;
using Xunit;

namespace UnitTests;

public class TranslationCacheTests
{
    private readonly TranslationCache _cache = new();

    [Fact]
    public void Cache_ShouldStoreAndRetrieveResults()
    {
        var result = new TranslationResult
        {
            OriginalText = "Hello",
            Translations = new Dictionary<TargetLanguage, string>
            {
                [TargetLanguage.Vietnamese] = "Xin chào"
            },
            ProviderName = "Test"
        };

        var targets = new[] { TargetLanguage.Vietnamese };
        _cache.Set("Hello", SourceLanguage.English, targets, result);

        bool found = _cache.TryGet("Hello", SourceLanguage.English, targets, out var cached);

        found.Should().BeTrue();
        cached.Should().NotBeNull();
        cached!.Translations[TargetLanguage.Vietnamese].Should().Be("Xin chào");
        cached.IsFromCache.Should().BeTrue();
    }

    [Fact]
    public void Cache_DifferentTarget_ShouldReturnMiss()
    {
        var result = new TranslationResult
        {
            OriginalText = "Hello",
            Translations = new Dictionary<TargetLanguage, string>
            {
                [TargetLanguage.Vietnamese] = "Xin chào"
            }
        };

        _cache.Set("Hello", SourceLanguage.English, [TargetLanguage.Vietnamese], result);

        bool found = _cache.TryGet("Hello", SourceLanguage.English, [TargetLanguage.Chinese], out var cached);
        found.Should().BeFalse();
        cached.Should().BeNull();
    }
}

public class ScreenRegionTests
{
    [Fact]
    public void FormattedSize_ShouldReturnWidthByHeight()
    {
        var region = new ScreenRegion(100, 200, 1280, 160);
        region.FormattedSize.Should().Be("1280 × 160");
        region.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void EmptyRegion_ShouldReturnIsEmptyTrue()
    {
        var region = ScreenRegion.Empty;
        region.IsEmpty.Should().BeTrue();
    }
}
