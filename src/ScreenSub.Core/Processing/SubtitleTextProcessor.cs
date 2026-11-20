using System.Text.RegularExpressions;
using ScreenSub.Core.Enums;

namespace ScreenSub.Core.Processing;

public sealed record SubtitleLineAnalysis(string Text, string LanguageCode, int OrderIndex);

public sealed record SubtitleAnalysisResult(
    string PrimarySourceText,
    string DetectedPrimaryLanguage,
    string? ExistingSecondaryText,
    string? ExistingSecondaryLanguage,
    IReadOnlyList<SubtitleLineAnalysis> AllLines
);

/// <summary>
/// Comprehensive subtitle text processor:
/// 1. Splits and segments mixed-language OCR blocks (English + Chinese + Vietnamese).
/// 2. Filters OCR noise and player UI artifacts.
/// 3. Normalizes punctuation and multi-line reading order.
/// 4. Discovers primary source line vs existing video translation line.
/// 5. Applies post-editing heuristics to prevent translation stutter and repetition.
/// </summary>
public static class SubtitleTextProcessor
{
    private static readonly Regex TimestampRegex = new(
        @"^(?:\d{1,2}:)?\d{2}:\d{2}(?:\s*[\/\-~]\s*(?:\d{1,2}:)?\d{2}:\d{2})?$",
        RegexOptions.Compiled);

    private static readonly Regex UiArtifactRegex = new(
        @"^(?:subtitles?|cc|play|pause|auto|720p|1080p|4k|hd|volume|muted?)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VietnameseCharRegex = new(
        @"[àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđÀÁẠẢÃÂẦẤẬẨẪĂẰẮẶẲẴÈÉẸẺẼÊỀẾỆỂỄÌÍỊỈĨÒÓỌỎÕÔỒỐỘỔỖƠỜỚỢỞỠÙÚỤỦŨƯỪỨỰỬỮỲÝỴỶỸĐ]",
        RegexOptions.Compiled);

    private static readonly Regex LowercaseGluedCamelCaseRegex = new(
        @"\b(?!(?:iPhone|iPad|iPod|iMac|iOS|iCloud|macOS|eBay|mRNA|pH)\b)([a-z]{3,})([A-Z][a-zA-Z]+)\b",
        RegexOptions.Compiled);

    private static readonly string[] SafeLongRunPrepositions =
    [
        "across", "between", "through", "without", "within", "because", "against",
        "towards", "toward", "during", "before", "around", "behind", "beyond",
        "beside", "inside", "outside", "although", "whether"
    ];

    /// <summary>
    /// Safely normalizes OCR word and punctuation spacing without any external dictionary dependency:
    /// 1. Cleans accidental spaces between CJK characters and fullwidth punctuation.
    /// 2. Inserts space after clause/sentence punctuation followed by Latin letters (preserving URLs/domains/abbreviations).
    /// 3. Splits only unambiguous lowercase-leading glued runs (e.g. "communitiesacrossSoutheastAsia") while strictly
    ///    preserving PascalCase brands (ScreenSubAI, DuckDB, ClickHouse, PostgreSQL, YouTube), iPhone/macOS,
    ///    valid English words/compounds (therapist, nowhere, conductor, strait, lifeguard), proper nouns,
    ///    Vietnamese text (with or without diacritics), and CJK text untouched.
    /// Word spacing for small/tight Latin subtitles is preserved at the recognition stage via bicubic crop normalization.
    /// </summary>
    public static string RestoreWordSpacing(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Process line-by-line to preserve multi-line subtitle structure (\n)
        var rawLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var processedLines = new List<string>(rawLines.Length);

        foreach (var rawLine in rawLines)
        {
            string line = rawLine;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // 1. Clean accidental spaces between CJK characters and fullwidth punctuation: "山 路 一 路 蜿 蜒 ，" -> "山路一路蜿蜒，"
            line = Regex.Replace(line, @"(?<=[\u4e00-\u9fff\u3400-\u4dbf])\s+(?=[\u4e00-\u9fff\u3400-\u4dbf])", "");
            line = Regex.Replace(line, @"\s+([，。！？；：、“”‘’（）《》])", "$1");
            line = Regex.Replace(line, @"([，。！？；：、“”‘’（）《》])\s+", "$1");

            // 2. Insert space after clause/sentence punctuation if immediately followed by a letter (preserving domains/abbreviations)
            line = Regex.Replace(line, @"([,\?!;:])(?=[a-zA-Z])", "$1 ");
            line = Regex.Replace(line, @"(?<=[a-z]{2})\.(?=[A-Z][a-z])", ". ");

            // 3. Split unambiguous lowercase-leading glued CamelCase tokens (e.g. "communitiesacrossSoutheastAsia" -> "communitiesacross Southeast Asia")
            //    while never touching PascalCase identifiers (ScreenSubAI, DuckDB, ClickHouse, PostgreSQL, YouTube) or Apple brands (iPhone, macOS)
            if (!VietnameseCharRegex.IsMatch(line))
            {
                line = LowercaseGluedCamelCaseRegex.Replace(line, m =>
                {
                    string prefix = m.Groups[1].Value;
                    string tail = Regex.Replace(m.Groups[2].Value, @"(?<=[a-z])(?=[A-Z][a-z])", " ");
                    return $"{prefix} {tail}";
                });

                // Split only ultra-long (>=14 char) all-lowercase tokens ending in a clear multi-char preposition (e.g. "communitiesacross")
                line = Regex.Replace(line, @"\b[a-z]{14,}\b", m =>
                {
                    string w = m.Value;
                    foreach (string prep in SafeLongRunPrepositions)
                    {
                        if (w.EndsWith(prep, StringComparison.Ordinal) && w.Length - prep.Length >= 6)
                        {
                            return $"{w[..^prep.Length]} {prep}";
                        }
                    }
                    return w;
                });
            }

            line = Regex.Replace(line, @"[ ]{2,}", " ").Trim();
            if (line.Length > 0)
            {
                processedLines.Add(line);
            }
        }

        return string.Join("\n", processedLines);
    }

    /// <summary>
    /// Analyzes raw multi-line OCR text and separates it into distinct, deduplicated language blocks.
    /// Determines the primary source line and any secondary pre-existing translation line in the video.
    /// </summary>
    public static SubtitleAnalysisResult AnalyzeOcrText(string rawText, SourceLanguage preferredSource = SourceLanguage.Auto)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new SubtitleAnalysisResult(string.Empty, "zh", null, null, []);
        }

        // 1. Break raw text into individual candidate lines (including mixed CJK-Latin on the same line)
        var rawLines = SplitIntoCandidateLines(rawText);

        // 2. Clean each line and discard noise
        var cleanedLines = new List<(string Text, string Lang)>();
        foreach (var line in rawLines)
        {
            var clean = CleanLineText(line);
            if (string.IsNullOrWhiteSpace(clean)) continue;
            if (IsNoiseOrUiArtifact(clean)) continue;

            string lang = DetectLanguage(clean);
            cleanedLines.Add((clean, lang));
        }

        if (cleanedLines.Count == 0)
        {
            return new SubtitleAnalysisResult(string.Empty, "zh", null, null, []);
        }

        // 3. Group consecutive lines sharing the same language (multi-line subtitles of the same language)
        var segments = GroupConsecutiveLanguageLines(cleanedLines);

        // 4. Deduplicate near-identical lines
        var uniqueSegments = DeduplicateSegments(segments);

        // 5. Build line analysis list
        var lineAnalyses = new List<SubtitleLineAnalysis>();
        for (int i = 0; i < uniqueSegments.Count; i++)
        {
            lineAnalyses.Add(new SubtitleLineAnalysis(uniqueSegments[i].Text, uniqueSegments[i].Lang, i));
        }

        // 6. Select Primary Source vs Existing Secondary Translation line
        string primaryText;
        string primaryLang;
        string? secondaryText = null;
        string? secondaryLang = null;

        if (uniqueSegments.Count == 1)
        {
            primaryText = uniqueSegments[0].Text;
            primaryLang = uniqueSegments[0].Lang;
        }
        else
        {
            int primaryIdx = 0;

            if (preferredSource == SourceLanguage.Chinese)
            {
                int zhIdx = uniqueSegments.FindIndex(s => s.Lang == "zh");
                if (zhIdx >= 0) primaryIdx = zhIdx;
            }
            else if (preferredSource == SourceLanguage.English)
            {
                int enIdx = uniqueSegments.FindIndex(s => s.Lang == "en");
                if (enIdx >= 0) primaryIdx = enIdx;
            }
            else if (preferredSource == SourceLanguage.Vietnamese)
            {
                int viIdx = uniqueSegments.FindIndex(s => s.Lang == "vi");
                if (viIdx >= 0) primaryIdx = viIdx;
            }
            else // Auto
            {
                // In bilingual subtitles, the top line is almost universally the primary spoken language
                primaryIdx = 0;
            }

            primaryText = uniqueSegments[primaryIdx].Text;
            primaryLang = uniqueSegments[primaryIdx].Lang;

            // The remaining segment is an existing secondary subtitle already in the video
            int secondaryIdx = (primaryIdx == 0) ? 1 : 0;
            if (secondaryIdx < uniqueSegments.Count)
            {
                secondaryText = uniqueSegments[secondaryIdx].Text;
                secondaryLang = uniqueSegments[secondaryIdx].Lang;
            }
        }

        return new SubtitleAnalysisResult(
            primaryText,
            primaryLang,
            secondaryText,
            secondaryLang,
            lineAnalyses
        );
    }

    /// <summary>
    /// Detects language of a string: 'zh' (Chinese), 'vi' (Vietnamese), 'en' (English), or 'auto'.
    /// </summary>
    public static string DetectLanguage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "auto";

        int cjkCount = 0;
        int latinCount = 0;
        int viCount = 0;

        foreach (char c in text)
        {
            if (IsCjkCharacter(c))
            {
                cjkCount++;
            }
            else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
            {
                latinCount++;
            }
        }

        var matches = VietnameseCharRegex.Matches(text);
        viCount = matches.Count;

        if (cjkCount > 0 && cjkCount >= Math.Max(1, latinCount / 2))
        {
            return "zh";
        }

        if (viCount > 0)
        {
            return "vi";
        }

        if (latinCount >= 2)
        {
            return "en";
        }

        return "auto";
    }

    /// <summary>
    /// General post-editing heuristics to clean translated sentences without sentence-specific hardcoding:
    /// - Normalizes Unicode compatibility forms (NFKC) and collapses redundant whitespace
    /// - Removes leaked &lt;unk&gt; markers and spurious leading dialogue dashes when not in source
    /// - Fixes stutter punctuation while preserving ellipses
    /// - Removes duplicate adjacent words/phrases and high-overlap repetitive clause loops
    /// - Cleans spacing and fullwidth punctuation in Chinese
    /// </summary>
    public static string CleanTranslationOutput(string text, string? sourceText = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var clean = text.Normalize(System.Text.NormalizationForm.FormC).Trim();

        // 0. Strip leaked <unk> tokens and collapse excessive whitespace
        if (clean.Contains("<unk>", StringComparison.OrdinalIgnoreCase))
        {
            clean = Regex.Replace(clean, @"<unk>", " ", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\s+", " ").Trim();
        }

        // Strip spurious leading dialogue dash emitted by NMT when source did not start with a dash
        if (!string.IsNullOrEmpty(sourceText))
        {
            var srcTrimmed = sourceText.TrimStart();
            bool srcStartsWithDash = srcTrimmed.Length > 0 && (srcTrimmed[0] == '-' || srcTrimmed[0] == '–' || srcTrimmed[0] == '—');
            if (!srcStartsWithDash)
            {
                clean = Regex.Replace(clean, @"^[-–—]\s+", "");
            }
        }

        // 1. Fix stutter punctuation (preserve '...')
        clean = Regex.Replace(clean, @"\.{4,}", "...");
        clean = Regex.Replace(clean, @"(?<!\.)\.\.(?!\.)", ".");
        clean = Regex.Replace(clean, @"([,\?!;:])\1+", "$1");

        // 2. General structural refinement (typographic quotes, spaces around punctuation)
        clean = RefineStructuralFormatting(clean);

        // 3. Remove duplicate consecutive words and multi-word phrases
        clean = DeduplicateAdjacentWordsAndPhrases(clean);

        // 4. Clause deduplication: check if sentence has clauses with high word overlap
        clean = DeduplicateRepeatedClauses(clean);

        // 5. If text is CJK, clean accidental spaces between characters and fullwidth punctuation
        if (clean.Any(IsCjkCharacter))
        {
            clean = Regex.Replace(clean, @"(?<=[\u4e00-\u9fff\u3400-\u4dbf])\s+(?=[\u4e00-\u9fff\u3400-\u4dbf])", "");
            clean = Regex.Replace(clean, @"\s+([，。！？；：、“”‘’（）《》])", "$1");
            clean = Regex.Replace(clean, @"([，。！？；：、“”‘’（）《》])\s+", "$1");
        }

        // 6. Trim again and capitalize first letter if Latin
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        if (clean.Length > 0 && char.IsLower(clean[0]))
        {
            clean = char.ToUpperInvariant(clean[0]) + clean.Substring(1);
        }

        return clean;
    }

    private static string RefineStructuralFormatting(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var t = text;
        // Remove accidental space before standard Latin punctuation (, . ! ? ; :)
        t = Regex.Replace(t, @"\s+([,\.\?!;:])", "$1");
        // Normalize multiple spaces
        t = Regex.Replace(t, @"\s{2,}", " ");
        return t.Trim();
    }

    private static string DeduplicateAdjacentWordsAndPhrases(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count < 2) return text;

        for (int phraseLen = 3; phraseLen >= 1; phraseLen--)
        {
            int i = 0;
            while (i + 2 * phraseLen <= words.Count)
            {
                bool match = true;
                for (int j = 0; j < phraseLen; j++)
                {
                    string w1 = words[i + j].Trim(',', '.', '!', '?', ';', ':');
                    string w2 = words[i + phraseLen + j].Trim(',', '.', '!', '?', ';', ':');
                    if (!string.Equals(w1, w2, StringComparison.OrdinalIgnoreCase))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    words.RemoveRange(i + phraseLen, phraseLen);
                }
                else
                {
                    i++;
                }
            }
        }

        return string.Join(" ", words);
    }

    private static List<string> SplitIntoCandidateLines(string rawText)
    {
        var result = new List<string>();
        var lines = rawText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // Check if line has both CJK characters and Latin words on the same line
            // E.g.: "我们要抓住这次难得的发展机遇。We must seize this rare development opportunity."
            int cjkCount = line.Count(IsCjkCharacter);
            int latinCount = line.Count(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));

            if (cjkCount >= 2 && latinCount >= 4)
            {
                // Split at transition boundary
                var subParts = SplitCjkAndLatin(line);
                if (subParts.Count > 1)
                {
                    result.AddRange(subParts);
                    continue;
                }
            }

            result.Add(line);
        }

        return result;
    }

    private static List<string> SplitCjkAndLatin(string text)
    {
        var parts = new List<string>();
        // Regex matches either a block of CJK characters (and fullwidth punctuation)
        // or a block of Latin/digits/punctuation
        var matches = Regex.Matches(text, @"([\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff\u3000-\u303f\uff01-\uff0f\uff1a-\uff20\uff3b-\uff40\uff5b-\uff65]+|[a-zA-Z0-9\s,\.\?!\""\'\:\;\-]+)");

        foreach (Match m in matches)
        {
            var val = m.Value.Trim();
            if (val.Length >= 2)
            {
                parts.Add(val);
            }
        }

        return parts.Count > 0 ? parts : [text];
    }

    private static string CleanLineText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Strip non-printable control characters
        var cleaned = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();

        // Discard isolated tiny noise punctuation
        if (cleaned.Length == 1 && (cleaned == "_" || cleaned == "-" || cleaned == "~" || cleaned == "`" || cleaned == ":" || cleaned == "." || cleaned == "," || cleaned == "|" || cleaned == "\\"))
        {
            return string.Empty;
        }

        // Strip leading boundary punctuation artifact (e.g. ": are you" -> "are you")
        if (cleaned.Length >= 3 && (cleaned[0] == ':' || cleaned[0] == ';' || cleaned[0] == '~' || cleaned[0] == '`' || cleaned[0] == '|' || cleaned[0] == '-') && cleaned[1] == ' ')
        {
            cleaned = cleaned.Substring(2).TrimStart();
        }

        // Apply intelligent word spacing & sentence reconstruction
        cleaned = RestoreWordSpacing(cleaned);

        // Normalize common CJK visual OCR homographs caused by thick subtitle outlines
        if (cleaned.Contains("婉蜓", StringComparison.Ordinal) ||
            cleaned.Contains("蜿蜓", StringComparison.Ordinal) ||
            cleaned.Contains("婉蜒", StringComparison.Ordinal) ||
            cleaned.Contains("豌蜓", StringComparison.Ordinal) ||
            cleaned.Contains("豌蜒", StringComparison.Ordinal))
        {
            cleaned = cleaned
                .Replace("婉蜓", "蜿蜒", StringComparison.Ordinal)
                .Replace("蜿蜓", "蜿蜒", StringComparison.Ordinal)
                .Replace("婉蜒", "蜿蜒", StringComparison.Ordinal)
                .Replace("豌蜓", "蜿蜒", StringComparison.Ordinal)
                .Replace("豌蜒", "蜿蜒", StringComparison.Ordinal);
        }

        return cleaned;
    }

    private static bool IsNoiseOrUiArtifact(string text)
    {
        if (TimestampRegex.IsMatch(text)) return true;
        if (UiArtifactRegex.IsMatch(text)) return true;
        return false;
    }

    private static List<(string Text, string Lang)> GroupConsecutiveLanguageLines(List<(string Text, string Lang)> lines)
    {
        var groups = new List<(string Text, string Lang)>();

        for (int i = 0; i < lines.Count; i++)
        {
            var (text, lang) = lines[i];

            if (groups.Count > 0 && groups[^1].Lang == lang)
            {
                // Same language as previous: merge them properly
                string prevText = groups[^1].Text;
                string merged;

                if (lang == "zh")
                {
                    // Chinese: join without spaces
                    merged = prevText + text;
                }
                else
                {
                    // English / Vietnamese: join with space
                    merged = prevText + " " + text;
                }

                groups[^1] = (merged, lang);
            }
            else
            {
                groups.Add((text, lang));
            }
        }

        return groups;
    }

    private static List<(string Text, string Lang)> DeduplicateSegments(List<(string Text, string Lang)> segments)
    {
        var result = new List<(string Text, string Lang)>();

        foreach (var seg in segments)
        {
            bool isDuplicate = false;
            foreach (var existing in result)
            {
                if (existing.Lang == seg.Lang && (existing.Text == seg.Text || CalculateSimilarity(existing.Text, seg.Text) >= 0.85))
                {
                    isDuplicate = true;
                    break;
                }
            }

            if (!isDuplicate)
            {
                result.Add(seg);
            }
        }

        return result;
    }

    private static string DeduplicateRepeatedClauses(string text)
    {
        var clauseDelimiters = new[] { ',', ';', '.', '!', '?' };
        var rawClauses = text.Split(clauseDelimiters, StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .ToList();

        if (rawClauses.Count <= 1)
        {
            return text;
        }

        // Compare first clause with second clause
        if (rawClauses.Count == 2)
        {
            var c1 = rawClauses[0];
            var c2 = rawClauses[1];

            var words1 = new HashSet<string>(c1.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var words2 = new HashSet<string>(c2.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

            if (words1.Count > 0 && words2.Count > 0)
            {
                int intersection = words1.Count(w => words2.Contains(w));
                double overlapRatio1 = (double)intersection / words1.Count;
                double overlapRatio2 = (double)intersection / words2.Count;

                if (overlapRatio1 >= 0.60 || overlapRatio2 >= 0.60)
                {
                    // Repetitive clause loop detected! Return the first clean clause
                    char endingPunctuation = text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?')
                        ? text[^1]
                        : '.';
                    return c1 + endingPunctuation;
                }
            }
        }

        return text;
    }

    public static bool IsCjkCharacter(char c)
    {
        return (c >= 0x4E00 && c <= 0x9FFF) ||
               (c >= 0x3400 && c <= 0x4DBF) ||
               (c >= 0xF900 && c <= 0xFAFF) ||
               (c >= 0x3000 && c <= 0x303F) || // CJK symbols and punctuation
               (c >= 0xFF00 && c <= 0xFFEF);   // Halfwidth and fullwidth forms
    }

    public static double CalculateSimilarity(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2)) return 1.0;
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0.0;
        if (s1 == s2) return 1.0;

        int maxLen = Math.Max(s1.Length, s2.Length);
        int distance = LevenshteinDistance(s1, s2);
        return 1.0 - ((double)distance / maxLen);
    }

    private static int LevenshteinDistance(string s, string t)
    {
        int n = s.Length;
        int m = t.Length;
        int[,] d = new int[n + 1, m + 1];

        if (n == 0) return m;
        if (m == 0) return n;

        for (int i = 0; i <= n; d[i, 0] = i++) { }
        for (int j = 0; j <= m; d[0, j] = j++) { }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }

    // =========================================================================
    // Hybrid OCR + Speech Semantic Deduplication Safety
    // =========================================================================

    public const double DefaultMaxHybridOcrFreshnessMs = 4500.0;

    private static readonly HashSet<string> EnglishNegations = new(StringComparer.OrdinalIgnoreCase)
    {
        "not", "no", "never", "none", "nobody", "nothing", "neither", "nor", "nowhere", "without",
        "cannot", "can't", "cant", "won't", "wont", "don't", "dont", "doesn't", "doesnt",
        "didn't", "didnt", "isn't", "isnt", "aren't", "arent", "wasn't", "wasnt", "weren't", "werent",
        "shouldn't", "shouldnt", "wouldn't", "wouldnt", "couldn't", "couldnt", "mustn't", "mustnt",
        "haven't", "havent", "hasn't", "hasnt", "hadn't", "hadnt"
    };

    private static readonly Dictionary<string, long> EnglishNumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15,
        ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20,
        ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70,
        ["eighty"] = 80, ["ninety"] = 90, ["hundred"] = 100, ["thousand"] = 1000, ["million"] = 1000000,
        ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5
    };

    private static readonly (string A, string B)[] EnglishAntonymPairs =
    [
        ("open", "close"), ("opened", "closed"), ("opening", "closing"),
        ("on", "off"), ("start", "stop"), ("started", "stopped"),
        ("begin", "end"), ("enable", "disable"), ("enabled", "disabled"),
        ("lock", "unlock"), ("locked", "unlocked"), ("connect", "disconnect"),
        ("upload", "download"), ("login", "logout"), ("in", "out"),
        ("inside", "outside"), ("up", "down"), ("left", "right"),
        ("before", "after"), ("enter", "exit"), ("enter", "leave"),
        ("buy", "sell"), ("accept", "reject"), ("allow", "deny"),
        ("increase", "decrease"), ("high", "low"), ("fast", "slow"),
        ("true", "false"), ("yes", "no"), ("east", "west"), ("north", "south"),
        ("morning", "evening"), ("morning", "afternoon"), ("morning", "night"),
        ("afternoon", "evening"), ("today", "tomorrow"), ("today", "yesterday"),
        ("tomorrow", "yesterday"), ("am", "pm"), ("arrive", "depart"), ("arrived", "departed"),
        ("encrypt", "decrypt"), ("encrypted", "decrypted"),
        ("import", "export"), ("imported", "exported"),
        ("include", "exclude"), ("included", "excluded"),
        ("internal", "external"), ("maximum", "minimum"), ("max", "min"),
        ("push", "pull"), ("send", "receive"), ("sent", "received"),
        ("public", "private"), ("online", "offline"),
        ("success", "failure"), ("succeed", "fail"), ("succeeded", "failed"),
        ("pass", "fail"), ("passed", "failed"),
        ("approve", "reject"), ("approved", "rejected"),
        ("add", "remove"), ("added", "removed"), ("add", "delete"),
        ("create", "delete"), ("created", "deleted"),
        ("show", "hide"), ("shown", "hidden"),
        ("first", "last"), ("early", "late"),
        ("win", "lose"), ("won", "lost"),
        ("safe", "unsafe"), ("safe", "dangerous"),
        ("above", "below"), ("over", "under"), ("with", "without"),
        ("can", "cannot"), ("will", "wont"), ("do", "dont"), ("did", "didnt"), ("is", "isnt"), ("are", "arent")
    ];

    private static readonly HashSet<string> EnglishModalAndDirectionalContrastWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "must", "may", "might", "can", "could", "should", "shall", "will", "would",
        "before", "after", "until", "unless", "with", "without", "if", "although", "though",
        "all", "some", "any", "none", "every", "few", "many", "most", "both", "either", "neither",
        "always", "never", "often", "rarely", "seldom", "sometimes",
        "up", "down", "in", "out", "on", "off", "left", "right", "east", "west", "north", "south",
        "am", "pm", "mon", "tue", "wed", "thu", "fri", "sat", "sun",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
        "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december",
        "mr", "mrs", "ms", "dr", "prof", "sir", "madam"
    };

    private static readonly HashSet<char> ProtectedCjkSemanticAndEntityChars =
    [
        // Action / Polarity / State / Direction / Time / Quantity / Modal contrasts
        '开', '关', '启', '闭', '进', '出', '入', '离', '升', '降', '增', '减', '加', '删',
        '买', '卖', '收', '发', '推', '拉', '存', '取', '借', '还', '赢', '输', '成', '败',
        '批', '拒', '允', '禁', '是', '非', '有', '无', '多', '少', '大', '小', '高', '低',
        '快', '慢', '早', '晚', '午', '今', '明', '昨', '前', '后', '左', '右', '东', '西',
        '南', '北', '中', '上', '下', '内', '外', '男', '女', '公', '私', '正', '反', '真',
        '假', '对', '错', '好', '坏', '新', '旧', '生', '死', '来', '去',
        '只', '全', '都', '必', '须', '可', '能', '会', '该', '已', '未', '曾', '正', '将', '刚', '才',
        '起', '停', '始', '终', '长', '短', '宽', '窄', '深', '浅', '重', '轻', '强', '弱', '明', '暗',
        '冷', '热', '干', '湿', '空', '满', '真', '伪', '善', '恶', '美', '丑', '贵', '贱', '富', '贫',
        '动', '静', '松', '紧', '软', '硬', '粗', '细', '厚', '薄', '远', '近', '古', '今', '留', '走',
        '装', '卸', '读', '写', '问', '答', '給', '给', '拿', '放', '穿', '脱', '睡', '醒', '哭', '笑',
        // Geography / Cities / Locations
        '京', '沪', '津', '渝', '穗', '蓉', '汉', '宁', '沈', '杭', '深', '广', '川', '蜀',
        '粤', '鲁', '苏', '浙', '皖', '闽', '赣', '豫', '鄂', '湘', '琼', '黔', '滇', '陕',
        '甘', '青', '台', '港', '澳', '海', '州', '湾', '岛', '美', '英', '法', '德', '日',
        '韩', '俄', '越', '泰', '印', '欧', '亚', '厦', '福', '泉', '温', '苏', '锡', '常',
        // Common Surnames
        '王', '李', '张', '刘', '陈', '杨', '赵', '黄', '周', '吴', '徐', '孙', '胡', '朱',
        '高', '林', '何', '郭', '马', '罗', '梁', '宋', '郑', '谢', '唐', '冯', '董', '萧',
        '曹', '袁', '邓', '许', '傅', '彭', '吕', '卢', '蒋', '蔡', '贾', '丁', '魏', '薛',
        '叶', '阎', '潘', '杜', '戴', '夏', '钟', '汪', '田', '任', '姜', '范', '方', '石',
        '姚', '谭', '廖', '邹', '熊', '金', '陆', '郝', '孔', '白', '崔', '康', '毛', '邱',
        '秦', '江', '史', '顾', '侯', '邵', '孟', '龙', '段', '雷', '钱', '汤', '尹', '黎'
    ];

    private static readonly HashSet<char> CjkNameContextMarkers =
    [
        '小', '老', '阿', '总', '师', '生', '医', '博', '教', '授', '经', '理', '长', '员', '哥', '姐', '叔', '姨', '董', '局', '主', '任'
    ];

    /// <summary>
    /// Evaluates whether a recognized speech segment in Hybrid mode should be suppressed because
    /// an equivalent OCR subtitle is already actively displayed on screen within the freshness window.
    /// </summary>
    public static bool ShouldSuppressHybridSpeechDuplicate(
        string? ocrText,
        string? speechText,
        DateTime lastOcrSeenUtc,
        DateTime nowUtc,
        Func<string, string>? cjkSimplifiedNormalizer = null,
        double maxOcrFreshnessMs = DefaultMaxHybridOcrFreshnessMs)
    {
        if (string.IsNullOrWhiteSpace(ocrText) || string.IsNullOrWhiteSpace(speechText))
        {
            return false;
        }

        if (lastOcrSeenUtc == DateTime.MinValue ||
            (nowUtc - lastOcrSeenUtc).TotalMilliseconds > maxOcrFreshnessMs)
        {
            return false;
        }

        return AreSubtitlesSemanticallyEquivalent(ocrText, speechText, cjkSimplifiedNormalizer);
    }

    /// <summary>
    /// Determines whether two subtitles (e.g. Screen OCR and Whisper ASR) represent the same semantic utterance,
    /// tolerating CJK Simplified/Traditional script differences, punctuation/spacing, and minor 1-word ASR homophones,
    /// while strictly rejecting sentences that differ in negation polarity, numbers/times/quantities, actions/directions,
    /// or person/place names.
    /// </summary>
    public static bool AreSubtitlesSemanticallyEquivalent(
        string? ocrText,
        string? speechText,
        Func<string, string>? cjkSimplifiedNormalizer = null)
    {
        if (string.IsNullOrWhiteSpace(ocrText) || string.IsNullOrWhiteSpace(speechText))
        {
            return false;
        }

        string ocrSimp = NormalizeCrossStraitTerms(cjkSimplifiedNormalizer != null ? cjkSimplifiedNormalizer(ocrText.Trim()) : ocrText.Trim());
        string speechSimp = NormalizeCrossStraitTerms(cjkSimplifiedNormalizer != null ? cjkSimplifiedNormalizer(speechText.Trim()) : speechText.Trim());

        string ocrCanon = CanonicalizeSubtitleForComparison(ocrSimp);
        string speechCanon = CanonicalizeSubtitleForComparison(speechSimp);

        if (string.IsNullOrEmpty(ocrCanon) || string.IsNullOrEmpty(speechCanon))
        {
            return false;
        }

        // 1. Hard Veto: Numeric / Time / Quantity slots must match (e.g. 三点 vs 四点, 3 vs 4, 8:00 vs 9:00)
        var ocrNumbers = ExtractCanonicalNumbers(ocrSimp);
        var speechNumbers = ExtractCanonicalNumbers(speechSimp);
        if (!ocrNumbers.SequenceEqual(speechNumbers))
        {
            return false;
        }

        // 2. Hard Veto: Negation polarity must match (e.g. 打开 vs 不要打开, can vs cannot/can't)
        if (CountNegationMarkers(ocrSimp) != CountNegationMarkers(speechSimp))
        {
            return false;
        }

        // 3. Exact canonical or digit-canonical match
        string ocrDigitCanon = NormalizeChineseNumeralsInline(ocrCanon);
        string speechDigitCanon = NormalizeChineseNumeralsInline(speechCanon);
        if (string.Equals(ocrDigitCanon, speechDigitCanon, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool isCjk = ocrDigitCanon.Any(IsCjkCharacter) || speechDigitCanon.Any(IsCjkCharacter);
        if (isCjk)
        {
            return EvaluateCjkSemanticEquivalence(ocrDigitCanon, speechDigitCanon);
        }

        return EvaluateEnglishSemanticEquivalence(ocrText, speechText, ocrDigitCanon, speechDigitCanon);
    }

    private static string NormalizeCrossStraitTerms(string text)
    {
        return text
            .Replace("這一個", "這個", StringComparison.Ordinal)
            .Replace("这一个", "这个", StringComparison.Ordinal)
            .Replace("那一個", "那個", StringComparison.Ordinal)
            .Replace("那一个", "那个", StringComparison.Ordinal)
            .Replace("网路", "网络", StringComparison.Ordinal)
            .Replace("软体", "软件", StringComparison.Ordinal)
            .Replace("资料库", "数据库", StringComparison.Ordinal)
            .Replace("伺服器", "服务器", StringComparison.Ordinal);
    }

    public static string CanonicalizeSubtitleForComparison(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new System.Text.StringBuilder(text.Length);
        bool prevWasSpace = false;
        bool prevWasCjk = false;

        foreach (char c in text.Trim())
        {
            if (char.IsPunctuation(c) || c is '。' or '！' or '？' or '，' or '；' or '：' or '、' or '“' or '”' or '‘' or '’' or '～' or '·')
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!prevWasSpace && !prevWasCjk && sb.Length > 0)
                {
                    sb.Append(' ');
                    prevWasSpace = true;
                }
                continue;
            }

            bool isCjk = c >= 0x4E00 && c <= 0x9FFF;
            if (isCjk && prevWasSpace && sb.Length > 0 && sb[^1] == ' ')
            {
                sb.Length--;
            }

            sb.Append(char.ToLowerInvariant(c));
            prevWasSpace = false;
            prevWasCjk = isCjk;
        }

        return sb.ToString().Trim();
    }

    private static string NormalizeChineseNumeralsInline(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                '零' or '〇' => '0',
                '一' or '壹' => '1',
                '二' or '两' or '贰' or '兩' => '2',
                '三' or '叁' or '參' => '3',
                '四' or '肆' => '4',
                '五' or '伍' => '5',
                '六' or '陆' or '陸' => '6',
                '七' or '柒' => '7',
                '八' or '捌' => '8',
                '九' or '玖' => '9',
                _ => c
            });
        }
        return sb.ToString();
    }

    private static List<long> ExtractCanonicalNumbers(string text)
    {
        var result = new List<long>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsDigit(c))
            {
                long val = 0;
                while (i < text.Length && char.IsDigit(text[i]))
                {
                    val = val * 10 + (text[i] - '0');
                    i++;
                }
                result.Add(val);
                continue;
            }

            if (TryMapCjkNumberChar(c, out int cjkDigit))
            {
                long val = 0;
                long current = 0;
                bool any = false;
                while (i < text.Length && TryMapCjkNumberChar(text[i], out int d))
                {
                    any = true;
                    if (d >= 10)
                    {
                        if (current == 0) current = 1;
                        val += current * d;
                        current = 0;
                    }
                    else
                    {
                        current = current * 10 + d;
                    }
                    i++;
                }
                if (any)
                {
                    result.Add(val + current);
                }
                continue;
            }

            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
            {
                int start = i;
                while (i < text.Length && ((text[i] >= 'a' && text[i] <= 'z') || (text[i] >= 'A' && text[i] <= 'Z')))
                {
                    i++;
                }
                string word = text.Substring(start, i - start);
                if (EnglishNumberWords.TryGetValue(word, out long numVal))
                {
                    result.Add(numVal);
                }
                continue;
            }

            i++;
        }

        return result;
    }

    private static bool TryMapCjkNumberChar(char c, out int value)
    {
        value = c switch
        {
            '零' or '〇' => 0,
            '一' or '壹' => 1,
            '二' or '两' or '兩' or '贰' => 2,
            '三' or '叁' => 3,
            '四' or '肆' => 4,
            '五' or '伍' => 5,
            '六' or '陆' or '陸' => 6,
            '七' or '柒' => 7,
            '八' or '捌' => 8,
            '九' or '玖' => 9,
            '十' or '拾' => 10,
            '百' or '佰' => 100,
            '千' or '仟' => 1000,
            '万' or '萬' => 10000,
            _ => -1
        };
        return value >= 0;
    }

    private static int CountNegationMarkers(string text)
    {
        int count = 0;

        // 1. CJK negations (normalize '没有' / '毫无' as single negation)
        string cjkScan = text
            .Replace("没有", "没", StringComparison.Ordinal)
            .Replace("沒有", "没", StringComparison.Ordinal)
            .Replace("毫无", "无", StringComparison.Ordinal)
            .Replace("毫無", "无", StringComparison.Ordinal);

        foreach (char c in cjkScan)
        {
            if (c is '不' or '没' or '沒' or '未' or '别' or '別' or '勿' or '非' or '莫' or '毋' or '甭' or '无' or '無')
            {
                count++;
            }
        }

        // 2. English word-level negations (including contractions like don't, can't, won't)
        var matches = Regex.Matches(text, @"[a-zA-Z]+(?:'[a-zA-Z]+)?");
        foreach (Match m in matches)
        {
            if (EnglishNegations.Contains(m.Value))
            {
                count++;
            }
        }

        return count;
    }

    private static bool EvaluateCjkSemanticEquivalence(string a, string b)
    {
        int minLen = Math.Min(a.Length, b.Length);
        int maxLen = Math.Max(a.Length, b.Length);
        if (minLen == 0) return false;

        // Substring containment only if the shorter clause covers >= 75% of the full subtitle (and >= 6 chars)
        if (minLen >= 6 && (double)minLen / maxLen >= 0.75 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)))
        {
            return true;
        }

        // Isolate differing core span after stripping shared prefix and shared suffix
        int prefix = 0;
        while (prefix < minLen && a[prefix] == b[prefix]) prefix++;

        int suffix = 0;
        while (suffix < (minLen - prefix) && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;

        string diffA = a.Substring(prefix, a.Length - prefix - suffix);
        string diffB = b.Substring(prefix, b.Length - prefix - suffix);

        // Reject if any differing character is a protected semantic contrast (action, direction, time, place, surname)
        foreach (char c in diffA)
        {
            if (ProtectedCjkSemanticAndEntityChars.Contains(c)) return false;
        }
        foreach (char c in diffB)
        {
            if (ProtectedCjkSemanticAndEntityChars.Contains(c)) return false;
        }

        // Reject if one side inserts or deletes a single critical modifier while the other has nothing (e.g. "我同意" vs "我不同意", "全部删除" vs "只删除")
        if ((diffA.Length == 0 && diffB.Length > 0) || (diffB.Length == 0 && diffA.Length > 0))
        {
            string inserted = diffA.Length > 0 ? diffA : diffB;
            if (inserted.Length <= 2 && inserted.Any(c => ProtectedCjkSemanticAndEntityChars.Contains(c)))
            {
                return false;
            }
        }

        // Reject if the differing span is immediately adjacent to a person name/title marker (e.g. 小王 vs 小张, 李明 vs 李华, 陈医生 vs 林医生)
        if (prefix > 0 && CjkNameContextMarkers.Contains(a[prefix - 1]))
        {
            return false;
        }
        if (suffix > 0 && CjkNameContextMarkers.Contains(a[a.Length - suffix]))
        {
            return false;
        }

        // Allow minor 1-2 char ASR homophone only on sufficiently long utterances (>= 8 chars, similarity >= 0.82, edit distance <= 2)
        int dist = LevenshteinDistance(a, b);
        double sim = 1.0 - ((double)dist / maxLen);
        return maxLen >= 8 && dist <= 2 && sim >= 0.82;
    }

    private static bool EvaluateEnglishSemanticEquivalence(string rawOcr, string rawSpeech, string canonOcr, string canonSpeech)
    {
        // 1. Check explicit English antonym pairs
        var ocrWordsSet = new HashSet<string>(canonOcr.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        var speechWordsSet = new HashSet<string>(canonSpeech.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        foreach (var (wA, wB) in EnglishAntonymPairs)
        {
            if ((ocrWordsSet.Contains(wA) && speechWordsSet.Contains(wB) && !ocrWordsSet.Contains(wB)) ||
                (ocrWordsSet.Contains(wB) && speechWordsSet.Contains(wA) && !ocrWordsSet.Contains(wA)))
            {
                return false;
            }
        }

        // 2. Coalesce 1-word compound splits (e.g. "checks some" <-> "checksum", "post gress" <-> "postgres")
        var wordsA = CoalesceCompoundSplits(canonOcr.Split(' ', StringSplitOptions.RemoveEmptyEntries), canonSpeech.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var wordsB = CoalesceCompoundSplits(canonSpeech.Split(' ', StringSplitOptions.RemoveEmptyEntries), wordsA);

        // Strip shared leading and trailing words
        int minW = Math.Min(wordsA.Count, wordsB.Count);
        int pref = 0;
        while (pref < minW && string.Equals(wordsA[pref], wordsB[pref], StringComparison.OrdinalIgnoreCase)) pref++;

        int suff = 0;
        while (suff < (minW - pref) && string.Equals(wordsA[wordsA.Count - 1 - suff], wordsB[wordsB.Count - 1 - suff], StringComparison.OrdinalIgnoreCase)) suff++;

        var diffWordsA = wordsA.Skip(pref).Take(wordsA.Count - pref - suff).ToList();
        var diffWordsB = wordsB.Skip(pref).Take(wordsB.Count - pref - suff).ToList();

        if (diffWordsA.Count == 0 && diffWordsB.Count == 0)
        {
            return true;
        }

        // If one side has extra words and the other does not, only allow if it's a long clause containment (>= 75% words) without negation
        if (diffWordsA.Count == 0 || diffWordsB.Count == 0)
        {
            int maxW = Math.Max(wordsA.Count, wordsB.Count);
            return minW >= 4 && (double)minW / maxW >= 0.80;
        }

        // If differing word counts don't match after compound coalescing, or more than 2 words differ, reject
        if (diffWordsA.Count != diffWordsB.Count || diffWordsA.Count > 2)
        {
            return false;
        }

        for (int i = 0; i < diffWordsA.Count; i++)
        {
            string wa = diffWordsA[i];
            string wb = diffWordsB[i];

            // Reject modal/directional/quantifier/temporal contrast words (must vs may, all vs some, monday vs tuesday, etc.)
            if (EnglishModalAndDirectionalContrastWords.Contains(wa) || EnglishModalAndDirectionalContrastWords.Contains(wb))
            {
                return false;
            }

            // Reject negative/reversive prefixes (lock vs unlock, enable vs disable, valid vs invalid, etc.)
            if (IsReversivePrefixPair(wa, wb) || IsReversivePrefixPair(wb, wa))
            {
                return false;
            }

            // Require each substituted word to be phonetically/orthographically close (>= 0.65 char similarity)
            // This allows "checksum" vs "checkssome" (0.80), "station" vs "stations" (0.88),
            // while rejecting "smith" vs "jones" (0.00), "london" vs "boston" (0.33), "open" vs "close" (0.20).
            if (CalculateSimilarity(wa, wb) < 0.65)
            {
                return false;
            }
        }

        return CalculateSimilarity(canonOcr, canonSpeech) >= 0.82;
    }

    private static List<string> CoalesceCompoundSplits(IReadOnlyList<string> source, IReadOnlyList<string> reference)
    {
        var result = new List<string>(source.Count);
        int i = 0;
        while (i < source.Count)
        {
            if (i + 1 < source.Count)
            {
                string joined = source[i] + source[i + 1];
                if (reference.Any(r => r.Length >= 5 && CalculateSimilarity(joined, r) >= 0.65))
                {
                    result.Add(joined);
                    i += 2;
                    continue;
                }
            }
            result.Add(source[i]);
            i++;
        }
        return result;
    }

    private static bool IsReversivePrefixPair(string shorter, string longer)
    {
        if (longer.Length <= shorter.Length + 1) return false;
        string[] prefixes = ["un", "dis", "in", "im", "il", "ir", "non", "de", "mis"];
        foreach (string p in prefixes)
        {
            if (longer.StartsWith(p, StringComparison.OrdinalIgnoreCase) &&
                CalculateSimilarity(longer.Substring(p.Length), shorter) >= 0.80)
            {
                return true;
            }
        }
        return false;
    }
}
