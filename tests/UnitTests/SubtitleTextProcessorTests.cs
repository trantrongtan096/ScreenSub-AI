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

    [Theory]
    [InlineData("请在离开办公室之前打开所有的电脑电源。", "请在离开办公室之前关闭所有的电脑电源。")]
    [InlineData("请立即开启服务器防火墙。", "请立即关闭服务器防火墙。")]
    [InlineData("请把这份加密文件上传到内部服务器。", "请把这份加密文件下载到内部服务器。")]
    [InlineData("系统已经允许外部设备的连接请求。", "系统已经拒绝外部设备的连接请求。")]
    [InlineData("我们需要增加数据库连接池的容量。", "我们需要减少数据库连接池的容量。")]
    [InlineData("请在这份合同上面签字确认。", "请在这份合同下面签字确认。")]
    [InlineData("这趟列车是开往上海虹桥方向的。", "这趟列车是离开上海虹桥方向的。")]
    [InlineData("这个季度的服务器负载明显上升了。", "这个季度的服务器负载明显下降了。")]
    [InlineData("请先把重要的数据备份保存起来。", "请先把重要的数据备份删除掉。")]
    [InlineData("Please turn on the main power switch before leaving.", "Please turn off the main power switch before leaving.")]
    [InlineData("Please open the window before leaving the room.", "Please close the window before leaving the room.")]
    [InlineData("Please lock the front door before heading home.", "Please unlock the front door before heading home.")]
    [InlineData("Please enable the firewall on the primary node.", "Please disable the firewall on the primary node.")]
    [InlineData("We should encrypt the backup archive before uploading.", "We should decrypt the backup archive before uploading.")]
    [InlineData("Please import the configuration file from the shared folder.", "Please export the configuration file from the shared folder.")]
    [InlineData("The administrator approved the deployment request.", "The administrator rejected the deployment request.")]
    [InlineData("The security audit succeeded on the production cluster.", "The security audit failed on the production cluster.")]
    [InlineData("Please include the diagnostic logs in the report.", "Please exclude the diagnostic logs in the report.")]
    [InlineData("The maximum latency threshold is configured in settings.", "The minimum latency threshold is configured in settings.")]
    public void HybridDeduplication_OppositeActions_ShouldNeverSuppress(string ocr, string speech)
    {
        var now = DateTime.UtcNow;
        bool suppressed = SubtitleTextProcessor.ShouldSuppressHybridSpeechDuplicate(
            ocr, speech, now.AddMilliseconds(-500), now, ScreenSub.Translation.LocalChineseConverter.ToSimplified);
        suppressed.Should().BeFalse($"opposite actions '{ocr}' vs '{speech}' must never be suppressed as duplicates");
    }

    [Theory]
    [InlineData("我们明天下午三点在台北车站见面。", "我们明天下午四点在台北车站见面。")]
    [InlineData("请问明天的国际航班是早上八点起飞吗？", "请问明天的国际航班是早上九点起飞吗？")]
    [InlineData("我们需要预订两个标准房间。", "我们需要预订三个标准房间。")]
    [InlineData("我们今天下午在会议室讨论方案。", "我们明天下午在会议室讨论方案。")]
    [InlineData("我们昨天已经提交了测试报告。", "我们今天已经提交了测试报告。")]
    [InlineData("这次更新总共修复了十五个安全漏洞。", "这次更新总共修复了五十个安全漏洞。")]
    [InlineData("请在星期三之前完成所有的代码审查。", "请在星期四之前完成所有的代码审查。")]
    [InlineData("The flight departs at 8:00 AM from terminal two.", "The flight departs at 9:00 AM from terminal two.")]
    [InlineData("The flight departs at 8:00 AM from terminal two.", "The flight departs at 8:00 PM from terminal two.")]
    [InlineData("We need two more tickets for tonight's screening.", "We need three more tickets for tonight's screening.")]
    [InlineData("Let's review the benchmark report this morning.", "Let's review the benchmark report this evening.")]
    [InlineData("The release meeting is scheduled for Monday afternoon.", "The release meeting is scheduled for Tuesday afternoon.")]
    [InlineData("We detected fifteen anomalies during the stress test.", "We detected fifty anomalies during the stress test.")]
    public void HybridDeduplication_DifferentNumbersTimesAndQuantities_ShouldNeverSuppress(string ocr, string speech)
    {
        var now = DateTime.UtcNow;
        bool suppressed = SubtitleTextProcessor.ShouldSuppressHybridSpeechDuplicate(
            ocr, speech, now.AddMilliseconds(-500), now, ScreenSub.Translation.LocalChineseConverter.ToSimplified);
        suppressed.Should().BeFalse($"different numbers/times '{ocr}' vs '{speech}' must never be suppressed");
    }

    [Theory]
    [InlineData("我今天下午去北京参加技术会议。", "我今天下午不去北京参加技术会议。")]
    [InlineData("请在离开办公室之前打开电脑电源。", "请在离开办公室之前不要打开电脑电源。")]
    [InlineData("我们已经完成了数据库备份。", "我们没有完成数据库备份。")]
    [InlineData("系统已经检测到异常网络流量。", "系统未检测到异常网络流量。")]
    [InlineData("请把测试数据上传到公共服务器。", "请勿把测试数据上传到公共服务器。")]
    [InlineData("在会议期间可以开启麦克风。", "在会议期间别开启麦克风。")]
    [InlineData("这次升级对现有用户毫无影响。", "这次升级对现有用户有影响。")]
    [InlineData("I can attend the security review meeting tomorrow.", "I cannot attend the security review meeting tomorrow.")]
    [InlineData("I can attend the security review meeting tomorrow.", "I can't attend the security review meeting tomorrow.")]
    [InlineData("Please open the window before leaving.", "Please don't open the window before leaving.")]
    [InlineData("The backup service is running on the secondary node.", "The backup service isn't running on the secondary node.")]
    [InlineData("We should deploy the hotfix with database migration.", "We should deploy the hotfix without database migration.")]
    [InlineData("The client certificate is valid for this domain.", "The client certificate is invalid for this domain.")]
    public void HybridDeduplication_NegationPolarityMismatch_ShouldNeverSuppress(string ocr, string speech)
    {
        var now = DateTime.UtcNow;
        bool suppressed = SubtitleTextProcessor.ShouldSuppressHybridSpeechDuplicate(
            ocr, speech, now.AddMilliseconds(-500), now, ScreenSub.Translation.LocalChineseConverter.ToSimplified);
        suppressed.Should().BeFalse($"negation polarity mismatch '{ocr}' vs '{speech}' must never be suppressed");
    }

    [Theory]
    [InlineData("请问去北京的火车是早上八点出发吗？", "请问去南京的火车是早上八点出发吗？")]
    [InlineData("请问台北车站附近有什么推荐的餐厅吗？", "请问台中车站附近有什么推荐的餐厅吗？")]
    [InlineData("这批货物今天晚上从上海港口出发。", "这批货物今天晚上从深圳港口出发。")]
    [InlineData("小王今天下午在会议室做技术报告。", "小张今天下午在会议室做技术报告。")]
    [InlineData("李明已经到达上海浦东国际机场。", "李华已经到达上海浦东国际机场。")]
    [InlineData("陈医生刚刚完成了今天的手术。", "林医生刚刚完成了今天的手术。")]
    [InlineData("请把这份材料交给赵经理审阅。", "请把这份材料交给黄经理审阅。")]
    [InlineData("Dr. Smith arrived in London for the security conference.", "Dr. Jones arrived in London for the security conference.")]
    [InlineData("Dr. Smith arrived in London for the security conference.", "Dr. Smith arrived in Boston for the security conference.")]
    [InlineData("Professor Taylor will present the keynote speech today.", "Professor Miller will present the keynote speech today.")]
    public void HybridDeduplication_DifferentPersonAndLocationNames_ShouldNeverSuppress(string ocr, string speech)
    {
        var now = DateTime.UtcNow;
        bool suppressed = SubtitleTextProcessor.ShouldSuppressHybridSpeechDuplicate(
            ocr, speech, now.AddMilliseconds(-500), now, ScreenSub.Translation.LocalChineseConverter.ToSimplified);
        suppressed.Should().BeFalse($"different person/location names '{ocr}' vs '{speech}' must never be suppressed");
    }

    [Theory]
    [InlineData("请在离开之前把所有的电源关掉。", "请在离开之前把所有的电源开掉。")]
    [InlineData("这笔资金今天已经转入公司账户。", "这笔资金今天已经转出公司账户。")]
    [InlineData("这是我们团队内部的测试文档。", "这是我们团队外部的测试文档。")]
    [InlineData("请把音量调高一点以便大家听清楚。", "请把音量调低一点以便大家听清楚。")]
    [InlineData("我们必须全部保留这些历史日志。", "我们必须只保留这些历史日志。")]
    [InlineData("You must restart the computer after installing the driver.", "You may restart the computer after installing the driver.")]
    [InlineData("All worker nodes passed the health check.", "Some worker nodes passed the health check.")]
    [InlineData("Please turn left at the second intersection.", "Please turn right at the second intersection.")]
    public void HybridDeduplication_SingleCriticalCharOrModalWordDifference_ShouldNeverSuppress(string ocr, string speech)
    {
        var now = DateTime.UtcNow;
        bool suppressed = SubtitleTextProcessor.ShouldSuppressHybridSpeechDuplicate(
            ocr, speech, now.AddMilliseconds(-500), now, ScreenSub.Translation.LocalChineseConverter.ToSimplified);
        suppressed.Should().BeFalse($"single critical character/modal difference '{ocr}' vs '{speech}' must never be suppressed");
    }

    [Theory]
    [InlineData("Welcome to the station.", "Welcome to the station")]
    [InlineData("你好，欢迎大家参加今天的软件测试会议。", "你好 欢迎大家参加今天的软件测试会议")]
    [InlineData("你好，欢迎大家参加今天的软件测试会议。", "你好 歡迎大家參加今天的軟件測試會議")]
    [InlineData("请问明天的国际航班是早上八点起飞吗？", "請問,明天的國際航班是早上8點起飛嗎?")]
    [InlineData("我们已经在本地服务器上完成了数据库备份。", "我們已經在本地服務器上完成了數據庫備份。")]
    [InlineData("这个软体的网路连接非常稳定。", "這個軟件的網絡連接非常穩定。")]
    [InlineData("请检查伺服器的资料库备份状态。", "請檢查服務器的數據庫備份狀態。")]
    [InlineData("你好，欢迎大家参加今天的软件测试会议。", "你好 歡迎大家參加今天的軟件措施會議")]
    [InlineData("今天下午的天气非常晴朗，适合出去散步。", "今天下午的天氣非常晴浪 適合出去散步")]
    [InlineData("我们需要立即进行系统性能测试。", "我們需要立即進行系統性能側試")]
    [InlineData("Always verify the security checksum before installing updates.", "Always verify the security checks some before installing updates.")]
    [InlineData("The subtitle overlay renders smoothly on the primary monitor.", "The subtitle overlay renders smoothly on the primary monitors.")]
    public void HybridDeduplication_TrueEquivalentSimplifiedTraditionalAndHomophone_ShouldSuppress(string ocr, string speech)
    {
        var now = DateTime.UtcNow;
        bool suppressed = SubtitleTextProcessor.ShouldSuppressHybridSpeechDuplicate(
            ocr, speech, now.AddMilliseconds(-500), now, ScreenSub.Translation.LocalChineseConverter.ToSimplified);
        suppressed.Should().BeTrue($"equivalent or 1-word ASR homophone '{ocr}' vs '{speech}' should be deduplicated");
    }

    [Fact]
    public void HybridDeduplication_OcrRecencyWindow_ShouldRejectStaleOcrAndSuppressFreshOcr()
    {
        var now = DateTime.UtcNow;
        const string ocr = "Welcome to the station.";
        const string speech = "Welcome to the station.";

        // Fresh OCR (seen 1.2s ago <= 4.5s window) -> suppress
        SubtitleTextProcessor.ShouldSuppressHybridSpeechDuplicate(ocr, speech, now.AddMilliseconds(-1200), now)
            .Should().BeTrue();

        // Stale OCR (seen 5.0s ago > 4.5s window) -> must NOT suppress
        SubtitleTextProcessor.ShouldSuppressHybridSpeechDuplicate(ocr, speech, now.AddMilliseconds(-5000), now)
            .Should().BeFalse();

        // Uninitialized OCR timestamp -> must NOT suppress
        SubtitleTextProcessor.ShouldSuppressHybridSpeechDuplicate(ocr, speech, DateTime.MinValue, now)
            .Should().BeFalse();
    }

    [Fact]
    public void HybridDeduplication_FalsePositiveAndFalseNegativeRate_ShouldBeZeroOnAdversarialSuite()
    {
        var now = DateTime.UtcNow;
        var distinctMeaningPairs = new (string Ocr, string Speech)[]
        {
            ("请在离开办公室之前打开所有的电脑电源。", "请在离开办公室之前关闭所有的电脑电源。"),
            ("我们明天下午三点在台北车站见面。", "我们明天下午四点在台北车站见面。"),
            ("我今天下午去北京参加技术会议。", "我今天下午不去北京参加技术会议。"),
            ("请问去北京的火车是早上八点出发吗？", "请问去南京的火车是早上八点出发吗？"),
            ("小王今天下午在会议室做技术报告。", "小张今天下午在会议室做技术报告。"),
            ("Please turn on the main power switch before leaving.", "Please turn off the main power switch before leaving."),
            ("The flight departs at 8:00 AM from terminal two.", "The flight departs at 9:00 AM from terminal two."),
            ("I can attend the security review meeting tomorrow.", "I cannot attend the security review meeting tomorrow."),
            ("Dr. Smith arrived in London for the security conference.", "Dr. Jones arrived in London for the security conference."),
            ("Dr. Smith arrived in London for the security conference.", "Dr. Smith arrived in Boston for the security conference.")
        };

        var trueEquivalentPairs = new (string Ocr, string Speech)[]
        {
            ("Welcome to the station.", "welcome to the station"),
            ("你好，欢迎大家参加今天的软件测试会议。", "你好 歡迎大家參加今天的軟件測試會議"),
            ("请问明天的国际航班是早上八点起飞吗？", "請問,明天的國際航班是早上8點起飛嗎?"),
            ("你好，欢迎大家参加今天的软件测试会议。", "你好 歡迎大家參加今天的軟件措施會議"),
            ("今天下午的天气非常晴朗，适合出去散步。", "今天下午的天氣非常晴浪 適合出去散步"),
            ("Always verify the security checksum before installing updates.", "Always verify the security checks some before installing updates.")
        };

        int legacyFp = 0;
        int newFp = 0;
        foreach (var (ocr, speech) in distinctMeaningPairs)
        {
            string c1 = SubtitleTextProcessor.CanonicalizeSubtitleForComparison(ScreenSub.Translation.LocalChineseConverter.ToSimplified(ocr));
            string c2 = SubtitleTextProcessor.CanonicalizeSubtitleForComparison(ScreenSub.Translation.LocalChineseConverter.ToSimplified(speech));
            if (SubtitleTextProcessor.CalculateSimilarity(c1, c2) >= 0.78) legacyFp++;
            if (SubtitleTextProcessor.ShouldSuppressHybridSpeechDuplicate(ocr, speech, now.AddMilliseconds(-300), now, ScreenSub.Translation.LocalChineseConverter.ToSimplified))
                newFp++;
        }

        int newFn = 0;
        foreach (var (ocr, speech) in trueEquivalentPairs)
        {
            if (!SubtitleTextProcessor.ShouldSuppressHybridSpeechDuplicate(ocr, speech, now.AddMilliseconds(-300), now, ScreenSub.Translation.LocalChineseConverter.ToSimplified))
                newFn++;
        }

        legacyFp.Should().Be(distinctMeaningPairs.Length, "legacy >=0.78 similarity falsely suppressed all high-overlap opposite-meaning sentences");
        newFp.Should().Be(0, "semantic-safe guard must achieve 0% false-positive suppression on opposite/different meaning pairs");
        newFn.Should().Be(0, "semantic-safe guard must achieve 0% false-negative on true equivalent & 1-word homophone pairs");
    }

    // =========================================================================
    // OCR Spacing Normalization & False-Correction Safety Regression Suite
    // (Word spacing is preserved at recognition time via ocr_worker.py bicubic
    //  small-crop normalization; C# post-processing strictly avoids heuristic
    //  dictionary splitting to guarantee 0% false corrections and license safety.)
    // =========================================================================

    [Theory]
    [InlineData("山 路 一 路 蜿 蜒 ，", "山路一路蜿蜒，")]
    [InlineData("你好 ， 欢迎 大家 参加 今天的 会议 。", "你好，欢迎大家参加今天的会议。")]
    [InlineData("Wait a second,did you lock the door?Yes:all clear;thanks!", "Wait a second, did you lock the door? Yes: all clear; thanks!")]
    [InlineData("First sentence.Second sentence starts here.", "First sentence. Second sentence starts here.")]
    [InlineData("Multiple   spaces   between   words.", "Multiple spaces between words.")]
    [InlineData("Line one,with comma.\nLine two?Yes!", "Line one, with comma.\nLine two? Yes!")]
    public void RestoreWordSpacing_CjkAndPunctuationNormalization_ShouldCleanSafely(string rawOcr, string expected)
    {
        string reconstructed = SubtitleTextProcessor.RestoreWordSpacing(rawOcr);
        reconstructed.Should().Be(expected);
    }

    [Theory]
    [InlineData("ScreenSub AI uses RapidOCR, PaddleOCR, EnViT5 and CTranslate2 on Windows.")]
    [InlineData("Launch ScreenSubAI to translate live subtitles cleanly.")]
    [InlineData("She talked to her therapist because there is nowhere left to hide.")]
    [InlineData("He opened his notebook after football and migrated the database to PostgreSQL.")]
    [InlineData("Not a single ship dared cross the strait while the conductor waited.")]
    [InlineData("Watch YouTube on iPhone, iPad, MacBook or PlayStation via GitHub and OpenAI.")]
    [InlineData("We deployed DuckDB, ClickHouse, HuggingFace, DeepSeek, OnePlus, PowerBI and NextJS.")]
    [InlineData("PostgreSQL, MySQL, JavaScript, TypeScript, PowerShell, DirectML and TensorRT.")]
    [InlineData("Dr. McDonald and General MacArthur visited Washington and Johannesburg.")]
    [InlineData("Luke Skywalker arrived at Winterfell while Waterloo connects Westminster to Brighton.")]
    [InlineData("Detective Blackwood drove through Greensboro, Newcastle and Stonehaven.")]
    [InlineData("The lifeguard, housekeeper, eyewitness, caretaker and blacksmith avoided the landslide and hillside.")]
    [InlineData("Their screenplay, broadsword, stronghold, boathouse, workaround and changelog remained intact.")]
    [InlineData("Understanding international communication, development, townships and subtitles.")]
    [InlineData("Visit https://github.com/trantrongtan096/ScreenSub-AI or run app.exe today.")]
    [InlineData("你好，欢迎大家参加今天的软件测试会议。")]
    [InlineData("請問明天的國際航班是早上八點起飛嗎？")]
    [InlineData("Xin chào, hôm nay trời rất đẹp ở Hà Nội và Thành phố Hồ Chí Minh.")]
    [InlineData("Vui long tat toan bo thiet bi dien truoc khi roi khoi phong.")]
    [InlineData("Moi hanh khach can xuat trinh the len may bay tai cong kiem soat.")]
    public void RestoreWordSpacing_ValidWordsBrandsProperNounsAndCjkVi_ShouldNeverOverSplit(string cleanInput)
    {
        string reconstructed = SubtitleTextProcessor.RestoreWordSpacing(cleanInput);
        reconstructed.Should().Be(cleanInput);
    }

    [Fact]
    public void ScreenSubCoreAssembly_ShouldNotEmbedUnlicensedLexiconResource()
    {
        var resources = typeof(SubtitleTextProcessor).Assembly.GetManifestResourceNames();
        resources.Should().NotContain(r => r.Contains("english_lexicon", StringComparison.OrdinalIgnoreCase),
            "unlicensed Windows ISpellChecker-derived lexicon must never be embedded in ScreenSub.Core.dll");
    }
}

