using FluentAssertions;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Processing;
using Xunit;

namespace UnitTests;

public class SubtitleTextProcessorTests
{
    [Fact]
    public void AnalyzeOcrText_BilingualChineseEnglish_ShouldSeparateLanguagesCleanly()
    {
        // Video has Chinese subtitle on line 1, English translation on line 2
        string rawOcr = "我们要抓住这次难得的发展机遇。\nWe must seize this rare development opportunity.";

        var result = SubtitleTextProcessor.AnalyzeOcrText(rawOcr, SourceLanguage.Auto);

        result.PrimarySourceText.Should().Be("我们要抓住这次难得的发展机遇。");
        result.DetectedPrimaryLanguage.Should().Be("zh");
        result.ExistingSecondaryText.Should().Be("We must seize this rare development opportunity.");
        result.ExistingSecondaryLanguage.Should().Be("en");
    }

    [Fact]
    public void AnalyzeOcrText_PreferredSourceEnglish_ShouldPrioritizeEnglishLine()
    {
        string rawOcr = "我们要抓住这次难得的发展机遇。\nWe must seize this rare development opportunity.";

        var result = SubtitleTextProcessor.AnalyzeOcrText(rawOcr, SourceLanguage.English);

        result.PrimarySourceText.Should().Be("We must seize this rare development opportunity.");
        result.DetectedPrimaryLanguage.Should().Be("en");
        result.ExistingSecondaryText.Should().Be("我们要抓住这次难得的发展机遇。");
        result.ExistingSecondaryLanguage.Should().Be("zh");
    }

    [Fact]
    public void AnalyzeOcrText_SingleLineMixedCjkAndLatin_ShouldSplitIntoTwoSegments()
    {
        string rawOcr = "你要去哪里？ Where are you going tonight?";

        var result = SubtitleTextProcessor.AnalyzeOcrText(rawOcr, SourceLanguage.Chinese);

        result.PrimarySourceText.Should().Be("你要去哪里？");
        result.DetectedPrimaryLanguage.Should().Be("zh");
        result.ExistingSecondaryText.Should().Be("Where are you going tonight?");
        result.ExistingSecondaryLanguage.Should().Be("en");
    }

    [Fact]
    public void AnalyzeOcrText_MultiLineSingleLanguageChinese_ShouldMergeWithoutSpaces()
    {
        string rawOcr = "我们要抓住这次\n难得的发展机遇。";

        var result = SubtitleTextProcessor.AnalyzeOcrText(rawOcr, SourceLanguage.Auto);

        result.PrimarySourceText.Should().Be("我们要抓住这次难得的发展机遇。");
        result.DetectedPrimaryLanguage.Should().Be("zh");
        result.ExistingSecondaryText.Should().BeNull();
    }

    [Fact]
    public void AnalyzeOcrText_MultiLineSingleLanguageEnglish_ShouldMergeWithSpace()
    {
        string rawOcr = "We must seize this rare\ndevelopment opportunity.";

        var result = SubtitleTextProcessor.AnalyzeOcrText(rawOcr, SourceLanguage.Auto);

        result.PrimarySourceText.Should().Be("We must seize this rare development opportunity.");
        result.DetectedPrimaryLanguage.Should().Be("en");
        result.ExistingSecondaryText.Should().BeNull();
    }

    [Fact]
    public void AnalyzeOcrText_MultiLineSingleLanguageVietnamese_ShouldMergeWithSpace()
    {
        string rawOcr = "Hôm nay thời tiết\nthật sự rất đẹp.";

        var result = SubtitleTextProcessor.AnalyzeOcrText(rawOcr, SourceLanguage.Auto);

        result.PrimarySourceText.Should().Be("Hôm nay thời tiết thật sự rất đẹp.");
        result.DetectedPrimaryLanguage.Should().Be("vi");
        result.ExistingSecondaryText.Should().BeNull();
    }

    [Fact]
    public void AnalyzeOcrText_WithOcrNoiseAndUiArtifacts_ShouldFilterOutJunk()
    {
        string rawOcr = ": are you ready?\n_\n1080p\n01:23 / 45:00";

        var result = SubtitleTextProcessor.AnalyzeOcrText(rawOcr, SourceLanguage.Auto);

        result.PrimarySourceText.Should().Be("are you ready?");
        result.DetectedPrimaryLanguage.Should().Be("en");
        result.ExistingSecondaryText.Should().BeNull();
    }

    [Theory]
    [InlineData("你今天有时间吗？", "zh")]
    [InlineData("Hello, how are you today?", "en")]
    [InlineData("Hôm nay bạn có rảnh không?", "vi")]
    [InlineData("Thời tiết Hà Nội hôm nay rất dễ chịu.", "vi")]
    public void DetectLanguage_ShouldClassifyAccurately(string text, string expectedLang)
    {
        var lang = SubtitleTextProcessor.DetectLanguage(text);
        lang.Should().Be(expectedLang);
    }

    [Fact]
    public void CleanTranslationOutput_RepetitiveClauseLoop_ShouldSuppressDuplicateClause()
    {
        // Exact cases mentioned in prompt:
        // "The sun is shining, the sun is also coming out."
        // "Mặt trời sáng ra, mặt trời cũng ra."
        string repeatedEn = "The sun is shining, the sun is also coming out.";
        var cleanEn = SubtitleTextProcessor.CleanTranslationOutput(repeatedEn);
        cleanEn.Should().Be("The sun is shining.");

        string repeatedVi = "Mặt trời sáng ra, mặt trời cũng ra.";
        var cleanVi = SubtitleTextProcessor.CleanTranslationOutput(repeatedVi);
        cleanVi.Should().Be("Mặt trời sáng ra.");
    }

    [Fact]
    public void CleanTranslationOutput_DuplicateAdjacentWords_ShouldDeduplicate()
    {
        string input = "mặt trời mặt trời đang chiếu sáng";
        var clean = SubtitleTextProcessor.CleanTranslationOutput(input);
        clean.Should().Be("Mặt trời đang chiếu sáng");
    }

    [Fact]
    public void CleanTranslationOutput_StutterPunctuation_ShouldNormalize()
    {
        string input = "Xin chào.. bạn khỏe không??";
        var clean = SubtitleTextProcessor.CleanTranslationOutput(input);
        clean.Should().Be("Xin chào. bạn khỏe không?");
    }

    [Fact]
    public void RestoreWordSpacing_EnglishRunOn_ShouldRestoreSpaces()
    {
        string input = "with communitiesacrossSoutheastAsia,";
        var restored = SubtitleTextProcessor.RestoreWordSpacing(input);
        restored.Should().Be("with communities across Southeast Asia,");
    }

    [Fact]
    public void RestoreWordSpacing_Chinese_ShouldNotAddSpaces()
    {
        string input = "山路一路蜿蜒，";
        var restored = SubtitleTextProcessor.RestoreWordSpacing(input);
        restored.Should().Be("山路一路蜿蜒，");
    }

    [Fact]
    public void RestoreWordSpacing_ChineseAccidentalSpaces_ShouldRemoveSpaces()
    {
        string input = "山 路 一 路 蜿 蜒 ，";
        var restored = SubtitleTextProcessor.RestoreWordSpacing(input);
        restored.Should().Be("山路一路蜿蜒，");
    }

    [Fact]
    public void RestoreWordSpacing_Vietnamese_ShouldPreserveDiacriticsAndSpaces()
    {
        string input = "Những con đường núi quanh co uốn lượn.";
        var restored = SubtitleTextProcessor.RestoreWordSpacing(input);
        restored.Should().Be("Những con đường núi quanh co uốn lượn.");
    }

    [Fact]
    public void RestoreWordSpacing_PunctuationBoundary_ShouldAddSpace()
    {
        string input = "Asia,the mountain";
        var restored = SubtitleTextProcessor.RestoreWordSpacing(input);
        restored.Should().Be("Asia, the mountain");
    }

    [Fact]
    public void CleanTranslationOutput_SpuriousLeadingDash_AndUnkCleanup_ShouldBeGeneral()
    {
        SubtitleTextProcessor.CleanTranslationOutput("- Đừng đi vội.", "别急着走。")
            .Should().Be("Đừng đi vội.");
        SubtitleTextProcessor.CleanTranslationOutput("- Wait a second", "- Wait a second")
            .Should().Be("- Wait a second");
        SubtitleTextProcessor.CleanTranslationOutput("Đường núi <unk> rất đẹp.")
            .Should().Be("Đường núi rất đẹp.");
        SubtitleTextProcessor.CleanTranslationOutput("Chờ một chút... xem sao....")
            .Should().Be("Chờ một chút... xem sao...");
    }

    [Fact]
    public void CleanTranslationOutput_ChineseWithInternalSpaces_ShouldRemoveSpaces()
    {
        string input = "你好 ， 我 很 高 兴 见 到 你 。";
        var result = SubtitleTextProcessor.CleanTranslationOutput(input);
        result.Should().Be("你好，我很高兴见到你。");
    }
}

