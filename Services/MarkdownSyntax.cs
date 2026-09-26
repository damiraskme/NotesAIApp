using System.Text.RegularExpressions;

namespace MyApp.Services;

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

    [GeneratedRegex(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1")]
    private static partial Regex Bold();

    [GeneratedRegex(@"(~~)(?=\S)(.+?)(?<=\S)~~")]
    private static partial Regex Strikethrough();

    [GeneratedRegex(@"(?<![*_\w])([*_])(?![*_\s])(.+?)(?<![*_\s])\1(?![*_\w])")]
    private static partial Regex Italic();

    [GeneratedRegex(@"!?\[([^\]]*)\]\(([^)]*)\)")]
    private static partial Regex Link();

    public static List<(int Start, int Length)> FindMarkup(string text)
    {
        var spans = new List<(int Start, int Length)>();
        bool inCodeBlock = false;
        int offset = 0;

        foreach (string line in text.Split('\r'))
        {
            if (CodeFence().IsMatch(line))
            {
                spans.Add((offset, line.Length));
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

    private static void FindLineMarkup(string line, int offset, List<(int Start, int Length)> spans)
    {
        if (HorizontalRule().IsMatch(line))
        {
            spans.Add((offset, line.Length));
            return;
        }

        AddGroup(Heading().Match(line), 1, offset, spans);
        AddGroup(Quote().Match(line), 1, offset, spans);

        Match list = ListItem().Match(line);
        AddGroup(list, 1, offset, spans);
        AddGroup(list, 2, offset, spans);

        var codeRanges = new List<(int Start, int End)>();
        foreach (Match code in InlineCode().Matches(line))
        {
            codeRanges.Add((code.Index, code.Index + code.Length));
            AddDelimiters(code, 1, offset, spans);
        }

        foreach (Regex emphasis in new[] { Bold(), Strikethrough(), Italic() })
        {
            foreach (Match match in emphasis.Matches(line))
            {
                if (!InsideCode(match, codeRanges)) AddDelimiters(match, 1, offset, spans);
            }
        }

        foreach (Match link in Link().Matches(line))
        {
            if (InsideCode(link, codeRanges)) continue;

            Group label = link.Groups[1];
            spans.Add((offset + link.Index, label.Index - link.Index));
            int afterLabel = label.Index + label.Length;
            spans.Add((offset + afterLabel, link.Index + link.Length - afterLabel));
        }
    }

    private static void AddGroup(Match match, int group, int offset, List<(int Start, int Length)> spans)
    {
        if (match.Success && match.Groups[group].Success && match.Groups[group].Length > 0)
        {
            spans.Add((offset + match.Groups[group].Index, match.Groups[group].Length));
        }
    }

    private static void AddDelimiters(Match match, int group, int offset, List<(int Start, int Length)> spans)
    {
        int length = match.Groups[group].Length;
        spans.Add((offset + match.Index, length));
        spans.Add((offset + match.Index + match.Length - length, length));
    }

    private static bool InsideCode(Match match, List<(int Start, int End)> codeRanges) =>
        codeRanges.Any(r => match.Index >= r.Start && match.Index < r.End);
}
