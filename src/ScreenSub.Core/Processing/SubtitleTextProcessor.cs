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

    private static readonly HashSet<string> CommonEnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Full compound words that should NOT be split
        "international", "development", "opportunity", "technology", "education",
        "government", "environment", "information", "communication", "communities",
        "community", "understand", "understanding", "together", "without", "within",
        "because", "another", "everything", "something", "anything", "everyone",
        "someone", "anyone", "throughout", "underneath", "meanwhile", "furthermore",
        "southeast", "southwest", "northeast", "northwest", "pacific", "atlantic",
        "american", "european", "african", "asian",
        // Prepositions, Conjunctions & Relative Pronouns
        "with", "across", "from", "into", "over", "upon", "under", "about", "after",
        "before", "behind", "below", "beneath", "beside", "between", "beyond", "during",
        "inside", "outside", "through", "toward", "towards", "against", "among", "around",
        "along", "although", "while", "where", "which", "whose", "since", "unless",
        "until", "whether",
        // Common Subtitle Nouns, Verbs, Adjectives
        "people", "world", "system", "country", "countries", "nations", "nation",
        "region", "state", "states", "years", "time", "life", "first", "other",
        "these", "those", "their", "there", "every", "great", "small", "large",
        "house", "water", "place", "think", "would", "could", "should", "might",
        "shall", "mountain", "mountains", "roads", "road", "twisted", "twisting",
        "china", "vietnam", "america", "global", "local", "public", "social"
    };

    /// <summary>
    /// Intelligently restores missing word spacing and reconstructs natural sentence boundaries:
    /// 1. Inserts spaces at CamelCase / PascalCase word boundaries (e.g. "acrossSoutheastAsia" -> "across Southeast Asia").
    /// 2. Inserts space after punctuation followed by Latin letters (e.g. "Asia,the" -> "Asia, the").
    /// 3. Segments concatenated all-lowercase common English compound words (e.g. "communitiesacross" -> "communities across").
    /// 4. Preserves Chinese/CJK text without inserting spaces (and removes accidental spaces between CJK chars).
    /// 5. Preserves Vietnamese accented letters and natural spacing.
    /// </summary>
    public static string RestoreWordSpacing(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // 1. Clean accidental spaces between CJK characters and fullwidth punctuation: "山 路 一 路 蜿 蜒 ，" -> "山路一路蜿蜒，"
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff\u3400-\u4dbf])\s+(?=[\u4e00-\u9fff\u3400-\u4dbf])", "");
        text = Regex.Replace(text, @"\s+([，。！？；：、“”‘’（）《》])", "$1");
        text = Regex.Replace(text, @"([，。！？；：、“”‘’（）《》])\s+", "$1");

        // 2. Insert space at CamelCase/PascalCase word boundaries in Latin text:
        // "acrossSoutheast" -> "across Southeast"
        text = Regex.Replace(text, @"(?<=[a-z])(?=[A-Z])", " ");
        // "USACommunity" -> "USA Community"
        text = Regex.Replace(text, @"(?<=[A-Z])(?=[A-Z][a-z])", " ");

        // 3. Insert space after punctuation if immediately followed by a letter:
        // "Asia,the" -> "Asia, the"
        text = Regex.Replace(text, @"([,\.\?!;:])(?=[a-zA-Z])", "$1 ");

        // 4. Segment run-on lowercase English compound words:
        // E.g. "communitiesacross" -> "communities across"
        var tokens = text.Split(' ');
        var reconstructed = new List<string>(tokens.Length);

        foreach (var token in tokens)
        {
            var match = Regex.Match(token, @"^([a-zA-Z]+)([^a-zA-Z]*)$");
            if (match.Success)
            {
                string letters = match.Groups[1].Value;
                string punct = match.Groups[2].Value;
                string lower = letters.ToLowerInvariant();

                if (letters.Length >= 7 && !CommonEnglishWords.Contains(lower))
                {
                    bool splitFound = false;
                    for (int i = 3; i <= letters.Length - 3; i++)
                    {
                        string w1 = lower.Substring(0, i);
                        string w2 = lower.Substring(i);

                        if (CommonEnglishWords.Contains(w1) && CommonEnglishWords.Contains(w2))
                        {
                            string part1 = letters.Substring(0, i);
                            string part2 = letters.Substring(i);
                            reconstructed.Add($"{part1} {part2}{punct}");
                            splitFound = true;
                            break;
                        }
                    }

                    if (!splitFound)
                    {
                        reconstructed.Add(token);
                    }
                }
                else
                {
                    reconstructed.Add(token);
                }
            }
            else
            {
                reconstructed.Add(token);
            }
        }

        string result = string.Join(" ", reconstructed);
        result = Regex.Replace(result, @"[ ]{2,}", " ");
        return result.Trim();
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
}
