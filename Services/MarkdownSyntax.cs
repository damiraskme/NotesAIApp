using System.Text.RegularExpressions;

namespace MyApp.Services;

public enum MarkupKind
{
    Syntax,
    Bold,
    Italic,
    Strikethrough,
}

public readonly record struct MarkupSpan(int Start, int Length, MarkupKind Kind);

public static partial class MarkdownSyntax
{
    [GeneratedRegex(@"^\s{0,3}(```|~~~)")]
    private static partial Regex CodeFence();

    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex HorizontalRule();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})(?=\s|$)")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*(>+)")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^\s*([-*+]|\d+[.)])\s+(\[[ xX]\])?")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"(`+)(?!`)(.+?)(?<!`)\1(?!`)")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"!?\[([^\]]*)\]\(([^)]*)\)")]
    private static partial Regex Link();

    private sealed class DelimiterRun
    {
        public char Char;
        public int Length;
        public int Low;
        public int High;
        public bool CanOpen;
        public bool CanClose;

        public int Remaining => High - Low;
    }

    public static List<MarkupSpan> FindMarkup(string text)
    {
        var spans = new List<MarkupSpan>();
        bool inCodeBlock = false;
        int offset = 0;

        foreach (string line in text.Split('\r'))
        {
            if (CodeFence().IsMatch(line))
            {
                spans.Add(new MarkupSpan(offset, line.Length, MarkupKind.Syntax));
                inCodeBlock = !inCodeBlock;
            }
            else if (!inCodeBlock)
            {
                FindLineMarkup(line, offset, spans);
            }
            offset += line.Length + 1;
        }

        return spans;
    }

    private static void FindLineMarkup(string line, int offset, List<MarkupSpan> spans)
    {
        if (HorizontalRule().IsMatch(line))
        {
            spans.Add(new MarkupSpan(offset, line.Length, MarkupKind.Syntax));
            return;
        }

        int contentStart = 0;
        contentStart = Math.Max(contentStart, AddGroup(Heading().Match(line), 1, offset, spans));
        contentStart = Math.Max(contentStart, AddGroup(Quote().Match(line), 1, offset, spans));

        Match list = ListItem().Match(line);
        contentStart = Math.Max(contentStart, AddGroup(list, 1, offset, spans));
        contentStart = Math.Max(contentStart, AddGroup(list, 2, offset, spans));

        var excluded = new List<(int Start, int End)>();
        foreach (Match code in InlineCode().Matches(line))
        {
            excluded.Add((code.Index, code.Index + code.Length));
            int ticks = code.Groups[1].Length;
            spans.Add(new MarkupSpan(offset + code.Index, ticks, MarkupKind.Syntax));
            spans.Add(new MarkupSpan(offset + code.Index + code.Length - ticks, ticks, MarkupKind.Syntax));
        }

        foreach (Match link in Link().Matches(line))
        {
            if (IsExcluded(link.Index, excluded)) continue;

            Group label = link.Groups[1];
            spans.Add(new MarkupSpan(offset + link.Index, label.Index - link.Index, MarkupKind.Syntax));
            int afterLabel = label.Index + label.Length;
            spans.Add(new MarkupSpan(offset + afterLabel, link.Index + link.Length - afterLabel, MarkupKind.Syntax));
            excluded.Add((afterLabel, link.Index + link.Length));
        }

        FindEmphasis(line, offset, contentStart, excluded, spans);
    }

    private static void FindEmphasis(string line, int offset, int contentStart, List<(int Start, int End)> excluded, List<MarkupSpan> spans)
    {
        var runs = new List<DelimiterRun>();
        for (int i = contentStart; i < line.Length;)
        {
            char c = line[i];
            if (c is not ('*' or '_' or '~') || IsExcluded(i, excluded))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < line.Length && line[i] == c) i++;
            int length = i - start;

            char before = start > 0 ? line[start - 1] : ' ';
            char after = i < line.Length ? line[i] : ' ';
            bool leftFlanking = !char.IsWhiteSpace(after)
                && (!char.IsPunctuation(after) && !char.IsSymbol(after) || char.IsWhiteSpace(before) || char.IsPunctuation(before) || char.IsSymbol(before));
            bool rightFlanking = !char.IsWhiteSpace(before)
                && (!char.IsPunctuation(before) && !char.IsSymbol(before) || char.IsWhiteSpace(after) || char.IsPunctuation(after) || char.IsSymbol(after));

            if (c == '~' && length > 2) continue;

            runs.Add(new DelimiterRun
            {
                Char = c,
                Length = length,
                Low = start,
                High = i,
                CanOpen = c == '_' ? leftFlanking && (!rightFlanking || char.IsPunctuation(before)) : leftFlanking,
                CanClose = c == '_' ? rightFlanking && (!leftFlanking || char.IsPunctuation(after)) : rightFlanking,
            });
        }

        for (int closerIndex = 0; closerIndex < runs.Count; closerIndex++)
        {
            DelimiterRun closer = runs[closerIndex];
            if (!closer.CanClose) continue;

            while (closer.Remaining > 0)
            {
                int openerIndex = FindOpener(runs, closerIndex);
                if (openerIndex < 0) break;

                DelimiterRun opener = runs[openerIndex];
                int use = closer.Char == '~'
                    ? closer.Remaining
                    : opener.Remaining >= 2 && closer.Remaining >= 2 ? 2 : 1;
                MarkupKind kind = closer.Char == '~' ? MarkupKind.Strikethrough
                    : use == 2 ? MarkupKind.Bold
                    : MarkupKind.Italic;

                spans.Add(new MarkupSpan(offset + opener.High - use, use, kind));
                spans.Add(new MarkupSpan(offset + closer.Low, use, kind));
                opener.High -= use;
                closer.Low += use;

                for (int between = openerIndex + 1; between < closerIndex; between++)
                {
                    runs[between].High = runs[between].Low;
                }
            }
        }
    }

    private static int FindOpener(List<DelimiterRun> runs, int closerIndex)
    {
        DelimiterRun closer = runs[closerIndex];
        for (int i = closerIndex - 1; i >= 0; i--)
        {
            DelimiterRun opener = runs[i];
            if (opener.Char != closer.Char || !opener.CanOpen || opener.Remaining == 0) continue;

            if (closer.Char == '~')
            {
                if (opener.Remaining == closer.Remaining) return i;
                continue;
            }

            bool bothSides = opener.CanClose || closer.CanOpen;
            bool ruleOfThree = bothSides
                && (opener.Length + closer.Length) % 3 == 0
                && !(opener.Length % 3 == 0 && closer.Length % 3 == 0);
            if (!ruleOfThree) return i;
        }
        return -1;
    }

    private static int AddGroup(Match match, int group, int offset, List<MarkupSpan> spans)
    {
        if (!match.Success || !match.Groups[group].Success || match.Groups[group].Length == 0) return 0;

        Group g = match.Groups[group];
        spans.Add(new MarkupSpan(offset + g.Index, g.Length, MarkupKind.Syntax));
        return g.Index + g.Length;
    }

    private static bool IsExcluded(int index, List<(int Start, int End)> excluded) =>
        excluded.Any(r => index >= r.Start && index < r.End);
}
