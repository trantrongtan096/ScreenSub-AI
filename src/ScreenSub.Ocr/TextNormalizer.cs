using ScreenSub.Core.Models;

namespace ScreenSub.Ocr;

public sealed class TextNormalizer
{
    private string _lastStableText = string.Empty;
    private DateTime _lastUpdateTime = DateTime.MinValue;

    /// <summary>
    /// Normalizes and merges OCR lines in natural reading order (Top-to-Bottom, Left-to-Right).
    /// </summary>
    public string NormalizeAndMergeLines(IReadOnlyList<OcrLine> lines)
    {
        if (lines == null || lines.Count == 0)
        {
            return string.Empty;
        }

        // Sort lines primarily by Top position (vertical band), then Left position
        // If two lines have tops within 15px of each other, consider them on the same line.
        var sortedLines = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text) && l.Text.Trim().Length > 0)
            .OrderBy(l => l.Top)
            .ThenBy(l => l.Left)
            .ToList();

        if (sortedLines.Count == 0)
        {
            return string.Empty;
        }

        var lineGroups = new List<List<OcrLine>>();
        foreach (var line in sortedLines)
        {
            var cleanText = CleanLineText(line.Text);
            if (string.IsNullOrWhiteSpace(cleanText)) continue;

            line.Text = cleanText;

            bool added = false;
            foreach (var group in lineGroups)
            {
                var avgTop = group.Average(g => g.Top);
                if (Math.Abs(line.Top - avgTop) < 18)
                {
                    group.Add(line);
                    added = true;
                    break;
                }
            }

            if (!added)
            {
                lineGroups.Add([line]);
            }
        }

        var mergedParagraphs = new List<string>();
        foreach (var group in lineGroups)
        {
            var orderedGroup = group.OrderBy(g => g.Left);
            var rowText = string.Join(" ", orderedGroup.Select(g => g.Text));
            if (!string.IsNullOrWhiteSpace(rowText))
            {
                mergedParagraphs.Add(rowText);
            }
        }

        return string.Join("\n", mergedParagraphs);
    }

    /// <summary>
    /// Filters out temporary flickering and unstable OCR noise.
    /// </summary>
    public (string StableText, bool HasChanged) ApplyStabilityFilter(string rawText, int debounceMs = 250)
    {
        var trimmed = rawText.Trim();
        var now = DateTime.UtcNow;

        if (string.Equals(trimmed, _lastStableText, StringComparison.Ordinal))
        {
            return (_lastStableText, false);
        }

        // Check character overlap / similarity (80% threshold accommodates short CJK subtitle variations)
        double similarity = CalculateSimilarity(_lastStableText, trimmed);
        if (similarity >= 0.80 && (now - _lastUpdateTime).TotalMilliseconds < debounceMs)
        {
            // Minor jitter, maintain stable text
            return (_lastStableText, false);
        }

        _lastStableText = trimmed;
        _lastUpdateTime = now;
        return (_lastStableText, true);
    }

    public void Reset()
    {
        _lastStableText = string.Empty;
        _lastUpdateTime = DateTime.MinValue;
    }

    private static string CleanLineText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Remove unprintable control characters
        var cleaned = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();

        // Discard isolated tiny noise punctuation
        if (cleaned.Length == 1 && (cleaned == "_" || cleaned == "-" || cleaned == "~" || cleaned == "`"))
        {
            return string.Empty;
        }

        return cleaned;
    }

    private static double CalculateSimilarity(string s1, string s2)
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
