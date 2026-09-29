using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MyApp.Services;

public static partial class RichTextMarkdown
{
    public const string CodeFont = "Cascadia Mono";
    private const char ObjectChar = '￼';
    private const string RuleText = "———";
    private static readonly float[] HeadingScale = { 1.6f, 1.35f, 1.15f, 1.05f, 1.0f, 1.0f };

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UsePreciseSourceLocation()
        .Build();

    [Flags]
    private enum Style
    {
        None = 0,
        Bold = 1,
        Italic = 2,
        Underline = 4,
        Strike = 8,
        Code = 16,
    }

    private enum Kind { Normal, Heading, Bullet, Number, Quote, Code, Rule }

    private readonly record struct Paragraph(int Start, int Length, Kind Kind, int Level);

    private readonly record struct Span(int Start, int Length, Style Style, string? Link);

    private readonly record struct Picture(int Position, string Asset);

    [GeneratedRegex(@"\\(pngblip|jpegblip)")]
    private static partial Regex BlipPattern();

    [GeneratedRegex(@"!\\\[(.*?)\\\]\((assets/[^)\s]+)\)")]
    private static partial Regex EscapedImageReference();

    [GeneratedRegex(@"wzDescription\}\{\\sv ([^}]*)\}")]
    private static partial Regex PictureDescription();

    public static IReadOnlyDictionary<string, string> Load(
        RichEditTextDocument document, string markdown, IReadOnlyDictionary<string, byte[]> assets, double maxImageWidth, double scale)
    {
        var builder = new Builder(markdown, assets);
        builder.Build();

        document.SetText(TextSetOptions.None, builder.Text.ToString());
        float baseSize = document.GetDefaultCharacterFormat().Size;

        foreach (Paragraph paragraph in builder.Paragraphs)
        {
            ITextRange range = document.GetRange(paragraph.Start, paragraph.Start + paragraph.Length);
            switch (paragraph.Kind)
            {
                case Kind.Heading:
                    range.CharacterFormat.Size = baseSize * HeadingScale[Math.Clamp(paragraph.Level, 1, 6) - 1];
                    range.CharacterFormat.Bold = FormatEffect.On;
                    break;
                case Kind.Bullet:
                    range.ParagraphFormat.ListType = MarkerType.Bullet;
                    break;
                case Kind.Number:
                    break;
                case Kind.Quote:
                    range.ParagraphFormat.SetIndents(0, 18, 0);
                    break;
                case Kind.Code:
                    range.CharacterFormat.Name = CodeFont;
                    break;
            }
        }

        for (int i = 0; i < builder.Paragraphs.Count; i++)
        {
            if (builder.Paragraphs[i].Kind != Kind.Number) continue;

            int first = i;
            while (i + 1 < builder.Paragraphs.Count && builder.Paragraphs[i + 1].Kind == Kind.Number) i++;
            Paragraph last = builder.Paragraphs[i];

            ITextRange list = document.GetRange(builder.Paragraphs[first].Start, last.Start + last.Length);
            list.ParagraphFormat.ListType = MarkerType.Arabic;
            list.ParagraphFormat.ListStyle = MarkerStyle.Period;
            list.ParagraphFormat.ListStart = 1;
        }

        foreach (Span span in builder.Spans)
        {
            ITextRange range = document.GetRange(span.Start, span.Start + span.Length);
            if (span.Style.HasFlag(Style.Bold)) range.CharacterFormat.Bold = FormatEffect.On;
            if (span.Style.HasFlag(Style.Italic)) range.CharacterFormat.Italic = FormatEffect.On;
            if (span.Style.HasFlag(Style.Underline)) range.CharacterFormat.Underline = UnderlineType.Single;
            if (span.Style.HasFlag(Style.Strike)) range.CharacterFormat.Strikethrough = FormatEffect.On;
            if (span.Style.HasFlag(Style.Code)) range.CharacterFormat.Name = CodeFont;
        }

        var knownAssets = new Dictionary<string, string>();
        foreach (var (name, bytes) in assets)
        {
            knownAssets[Hash(bytes)] = name;
        }

        foreach (Picture picture in builder.Pictures.OrderByDescending(p => p.Position))
        {
            byte[] bytes = assets[picture.Asset];
            (int pixelWidth, int pixelHeight) = ImageSize(bytes);
            double width = pixelWidth / scale;
            double height = pixelHeight / scale;
            if (width > maxImageWidth)
            {
                height *= maxImageWidth / width;
                width = maxImageWidth;
            }

            ITextRange range = document.GetRange(picture.Position, picture.Position + 1);
            range.SetText(TextSetOptions.None, string.Empty);
            range.InsertImage((int)Math.Round(width), (int)Math.Round(height), 0,
                VerticalCharacterAlignment.Baseline, picture.Asset, new MemoryStream(bytes).AsRandomAccessStream());
        }

        foreach (Span span in builder.Spans.Where(s => s.Link is not null).OrderByDescending(s => s.Start))
        {
            document.GetRange(span.Start, span.Start + span.Length).Link = "\"" + span.Link + "\"";
        }

        return knownAssets;
    }

    public static TextPackContent Save(
        RichEditTextDocument document, IReadOnlyDictionary<string, string> knownAssets, IReadOnlyDictionary<string, byte[]> originalAssets)
    {
        document.GetText(TextGetOptions.None, out string text);
        if (text.EndsWith('\r')) text = text[..^1];
        document.GetText(TextGetOptions.FormatRtf, out string rtf);

        var pictures = new Queue<(byte[] Bytes, string Extension, string? Description)>(ExtractPictures(rtf));
        var assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        float baseSize = document.GetDefaultCharacterFormat().Size;

        var markdown = new StringBuilder();
        bool inCodeBlock = false;
        int number = 0;
        int offset = 0;

        foreach (string line in text.Split('\r'))
        {
            int start = offset;
            offset += line.Length + 1;

            ITextRange whole = document.GetRange(start, start + line.Length);
            ITextParagraphFormat paragraphFormat = document.GetRange(start, start).ParagraphFormat;
            MarkerType listType = paragraphFormat.ListType;

            bool isCode = line.Length > 0 && whole.CharacterFormat.Name == CodeFont && !line.Contains(ObjectChar);
            if (isCode)
            {
                if (!inCodeBlock) markdown.Append("```\n");
                inCodeBlock = true;
                markdown.Append(line).Append('\n');
                continue;
            }
            if (inCodeBlock)
            {
                markdown.Append("```\n");
                inCodeBlock = false;
            }

            number = listType == MarkerType.Arabic ? number + 1 : 0;

            if (line == RuleText && listType is MarkerType.None or MarkerType.Undefined)
            {
                markdown.Append("***\n");
                continue;
            }

            int headingLevel = line.Length > 0 ? HeadingLevel(whole.CharacterFormat.Size, baseSize) : 0;
            string prefix = headingLevel > 0 ? new string('#', headingLevel) + " "
                : listType == MarkerType.Bullet ? "- "
                : listType == MarkerType.Arabic ? $"{number}. "
                : paragraphFormat.LeftIndent > 1 && line.Length > 0 ? "> "
                : string.Empty;

            markdown.Append(prefix);
            var writer = new InlineWriter(markdown, escapeLineStart: prefix.Length == 0, ignoreBold: headingLevel > 0);
            WriteRuns(document, text, start, line.Length, writer, pictures, assets, knownAssets, originalAssets);
            writer.Finish();
            markdown.Append('\n');
        }

        if (inCodeBlock) markdown.Append("```\n");

        string result = EscapedImageReference().Replace(markdown.ToString(), match =>
        {
            string asset = Uri.UnescapeDataString(match.Groups[2].Value);
            if (!originalAssets.TryGetValue(asset, out byte[]? bytes)) return match.Value;
            assets[asset] = bytes;
            return "![" + match.Groups[1].Value + "](" + match.Groups[2].Value + ")";
        });
        return new TextPackContent(result, assets);
    }

    private static void WriteRuns(
        RichEditTextDocument document, string text, int start, int length, InlineWriter writer,
        Queue<(byte[] Bytes, string Extension, string? Description)> pictures, Dictionary<string, byte[]> assets,
        IReadOnlyDictionary<string, string> knownAssets, IReadOnlyDictionary<string, byte[]> originalAssets)
    {
        int end = start + length;
        int position = start;
        while (position < end)
        {
            ITextRange run = document.GetRange(position, position + 1);
            run.Expand(TextRangeUnit.CharacterFormat);
            int runEnd = Math.Clamp(run.EndPosition, position + 1, end);

            ITextCharacterFormat format = run.CharacterFormat;
            if (format.Hidden == FormatEffect.On)
            {
                position = runEnd;
                continue;
            }

            Style style = Style.None;
            if (format.Bold == FormatEffect.On) style |= Style.Bold;
            if (format.Italic == FormatEffect.On) style |= Style.Italic;
            if (format.Underline is not (UnderlineType.None or UnderlineType.Undefined)) style |= Style.Underline;
            if (format.Strikethrough == FormatEffect.On) style |= Style.Strike;
            if (format.Name == CodeFont) style |= Style.Code;
            string? link = string.IsNullOrEmpty(run.Link) ? null : run.Link.Trim('"');

            string chunk = text[position..runEnd];
            int segmentStart = 0;
            for (int i = 0; i <= chunk.Length; i++)
            {
                if (i < chunk.Length && chunk[i] != ObjectChar) continue;

                if (i > segmentStart) writer.WriteText(chunk[segmentStart..i], style, link);
                if (i < chunk.Length)
                {
                    if (pictures.Count > 0)
                    {
                        var (bytes, extension, description) = pictures.Dequeue();
                        string name;
                        if (description is not null && originalAssets.TryGetValue(description, out byte[]? original))
                        {
                            name = description;
                            bytes = original;
                        }
                        else
                        {
                            string hash = Hash(bytes);
                            name = knownAssets.TryGetValue(hash, out string? known) ? known : TextPackService.AssetsFolder + hash[..16] + extension;
                        }
                        assets[name] = bytes;
                        writer.WriteImage(name, style & ~Style.Code, link);
                    }
                }
                segmentStart = i + 1;
            }

            position = runEnd;
        }
    }

    private static int HeadingLevel(float size, float baseSize)
    {
        if (float.IsNaN(size) || size <= 0 || baseSize <= 0 || size < baseSize * 1.1f) return 0;
        int best = 0;
        float bestDistance = float.MaxValue;
        for (int level = 1; level <= 3; level++)
        {
            float distance = Math.Abs(size - baseSize * HeadingScale[level - 1]);
            if (distance < bestDistance)
            {
                best = level;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static List<(byte[] Bytes, string Extension, string? Description)> ExtractPictures(string rtf)
    {
        var pictures = new List<(byte[] Bytes, string Extension, string? Description)>();
        foreach (Match match in BlipPattern().Matches(rtf))
        {
            var hex = new StringBuilder();
            int i = match.Index + match.Length;
            while (i < rtf.Length)
            {
                char c = rtf[i];
                if (c == '{')
                {
                    int depth = 0;
                    do
                    {
                        if (rtf[i] == '\\') i++;
                        else if (rtf[i] == '{') depth++;
                        else if (rtf[i] == '}') depth--;
                        i++;
                    }
                    while (i < rtf.Length && depth > 0);
                }
                else if (c == '\\' && hex.Length == 0)
                {
                    i++;
                    while (i < rtf.Length && char.IsAsciiLetter(rtf[i])) i++;
                    while (i < rtf.Length && (char.IsAsciiDigit(rtf[i]) || rtf[i] == '-')) i++;
                    if (i < rtf.Length && rtf[i] == ' ') i++;
                }
                else if (Uri.IsHexDigit(c))
                {
                    hex.Append(c);
                    i++;
                }
                else if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else
                {
                    break;
                }
            }

            if (hex.Length % 2 == 1) hex.Length--;
            string extension = match.Groups[1].Value == "jpegblip" ? ".jpg" : ".png";
            int pictStart = rtf.LastIndexOf("\\pict", match.Index, StringComparison.Ordinal);
            string header = pictStart >= 0 ? rtf[pictStart..match.Index] : string.Empty;
            Match description = PictureDescription().Match(header);
            pictures.Add((Convert.FromHexString(hex.ToString()), extension, description.Success ? description.Groups[1].Value : null));
        }
        return pictures;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static (int Width, int Height) ImageSize(byte[] b)
    {
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
        {
            return (b[16] << 24 | b[17] << 16 | b[18] << 8 | b[19], b[20] << 24 | b[21] << 16 | b[22] << 8 | b[23]);
        }
        if (b.Length > 10 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F')
        {
            return (b[6] | b[7] << 8, b[8] | b[9] << 8);
        }
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF)
                {
                    i++;
                    continue;
                }
                byte marker = b[i + 1];
                int segment = b[i + 2] << 8 | b[i + 3];
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    return (b[i + 7] << 8 | b[i + 8], b[i + 5] << 8 | b[i + 6]);
                }
                i += 2 + segment;
            }
        }
        return (0, 0);
    }

    private static bool IsDisplayable(byte[] bytes) => ImageSize(bytes) is { Width: > 0, Height: > 0 };

    private sealed class Builder
    {
        private readonly string _markdown;
        private readonly IReadOnlyDictionary<string, byte[]> _assets;
        private readonly int[] _lineStarts;
        private readonly Dictionary<Style, int> _styleDepth = new();
        private string? _link;
        private int _paragraphStart;
        private Kind _kind;
        private int _level;
        private int _lastLine = -1;

        public StringBuilder Text { get; } = new();
        public List<Paragraph> Paragraphs { get; } = new();
        public List<Span> Spans { get; } = new();
        public List<Picture> Pictures { get; } = new();

        public Builder(string markdown, IReadOnlyDictionary<string, byte[]> assets)
        {
            _markdown = markdown.Replace("\r\n", "\n");
            _assets = assets;
            _lineStarts = _markdown.Select((c, i) => (c, i)).Where(x => x.c == '\n').Select(x => x.i + 1).Prepend(0).ToArray();
        }

        public void Build()
        {
            MarkdownDocument document = Markdown.Parse(_markdown, Pipeline);
            foreach (Block block in document) WalkBlock(block, Kind.Normal);
            if (Text.Length > 0 && Text[^1] == '\r') Text.Length--;
        }

        private int LineOf(int index)
        {
            int line = Array.BinarySearch(_lineStarts, Math.Max(0, index));
            return line >= 0 ? line : ~line - 1;
        }

        private void AddBlankLinesBefore(Block block)
        {
            int blank = block.Line - (_lastLine + 1);
            for (int i = 0; i < blank; i++)
            {
                Begin(Kind.Normal, 0);
                End();
            }
        }

        private void MarkEnd(Block block) => _lastLine = LineOf(Math.Max(block.Span.Start, block.Span.End));

        private void WalkBlock(Block block, Kind container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    AddBlankLinesBefore(heading);
                    Begin(Kind.Heading, heading.Level);
                    WalkInlines(heading.Inline);
                    End();
                    MarkEnd(heading);
                    break;

                case ParagraphBlock paragraph:
                    AddBlankLinesBefore(paragraph);
                    Begin(container, 0);
                    WalkInlines(paragraph.Inline);
                    End();
                    MarkEnd(paragraph);
                    break;

                case ListBlock list:
                    foreach (Block item in list)
                    {
                        if (item is not ListItemBlock listItem) continue;
                        bool first = true;
                        foreach (Block child in listItem)
                        {
                            WalkBlock(child, first ? (list.IsOrdered ? Kind.Number : Kind.Bullet) : Kind.Normal);
                            first = false;
                        }
                    }
                    break;

                case QuoteBlock quote:
                    foreach (Block child in quote) WalkBlock(child, Kind.Quote);
                    break;

                case CodeBlock code:
                    AddBlankLinesBefore(code);
                    for (int i = 0; i < code.Lines.Count; i++)
                    {
                        Begin(Kind.Code, 0);
                        AddText(code.Lines.Lines[i].Slice.ToString());
                        End();
                    }
                    MarkEnd(code);
                    break;

                case ThematicBreakBlock rule:
                    AddBlankLinesBefore(rule);
                    Begin(Kind.Rule, 0);
                    AddText(RuleText);
                    End();
                    MarkEnd(rule);
                    break;

                case ContainerBlock containerBlock:
                    foreach (Block child in containerBlock) WalkBlock(child, container);
                    break;

                default:
                    AddBlankLinesBefore(block);
                    string raw = _markdown.Substring(block.Span.Start, Math.Max(0, block.Span.Length)).TrimEnd('\n');
                    foreach (string line in raw.Split('\n'))
                    {
                        Begin(Kind.Normal, 0);
                        AddText(line);
                        End();
                    }
                    MarkEnd(block);
                    break;
            }
        }

        private void WalkInlines(ContainerInline? container)
        {
            if (container is null) return;

            foreach (Inline inline in container)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        AddText(literal.Content.ToString());
                        break;
                    case EmphasisInline emphasis:
                        Style style = emphasis.DelimiterChar == '~' ? Style.Strike
                            : emphasis.DelimiterCount >= 2 ? Style.Bold
                            : Style.Italic;
                        Push(style);
                        WalkInlines(emphasis);
                        Pop(style);
                        break;
                    case CodeInline code:
                        Push(Style.Code);
                        AddText(code.Content);
                        Pop(Style.Code);
                        break;
                    case LinkInline { IsImage: true } image:
                        string url = Uri.UnescapeDataString(image.Url ?? string.Empty);
                        if (_assets.TryGetValue(url, out byte[]? bytes) && IsDisplayable(bytes))
                        {
                            Pictures.Add(new Picture(Text.Length, url));
                            Text.Append(' ');
                        }
                        else
                        {
                            AddText(_markdown.Substring(image.Span.Start, image.Span.Length));
                        }
                        break;
                    case LinkInline link:
                        string? previous = _link;
                        _link = link.Url;
                        WalkInlines(link);
                        _link = previous;
                        break;
                    case AutolinkInline autolink:
                        string? before = _link;
                        _link = autolink.Url;
                        AddText(autolink.Url);
                        _link = before;
                        break;
                    case LineBreakInline:
                        Kind kind = _kind is Kind.Bullet or Kind.Number ? Kind.Normal : _kind;
                        int level = _level;
                        End();
                        Begin(kind, level);
                        break;
                    case HtmlInline html when html.Tag.Equals("<u>", StringComparison.OrdinalIgnoreCase):
                        Push(Style.Underline);
                        break;
                    case HtmlInline html when html.Tag.Equals("</u>", StringComparison.OrdinalIgnoreCase):
                        Pop(Style.Underline);
                        break;
                    case HtmlInline html:
                        AddText(html.Tag);
                        break;
                    case HtmlEntityInline entity:
                        AddText(entity.Transcoded.ToString());
                        break;
                    case ContainerInline other:
                        WalkInlines(other);
                        break;
                }
            }
        }

        private void Push(Style style) => _styleDepth[style] = _styleDepth.GetValueOrDefault(style) + 1;

        private void Pop(Style style) => _styleDepth[style] = Math.Max(0, _styleDepth.GetValueOrDefault(style) - 1);

        private Style CurrentStyle => _styleDepth.Where(p => p.Value > 0).Aggregate(Style.None, (all, p) => all | p.Key);

        private void Begin(Kind kind, int level)
        {
            _paragraphStart = Text.Length;
            _kind = kind;
            _level = level;
        }

        private void End()
        {
            Paragraphs.Add(new Paragraph(_paragraphStart, Text.Length - _paragraphStart, _kind, _level));
            Text.Append('\r');
        }

        private void AddText(string text)
        {
            if (text.Length == 0) return;
            text = text.Replace("\r", string.Empty).Replace('\n', ' ');

            int start = Text.Length;
            Text.Append(text);
            Style style = CurrentStyle;
            if (style != Style.None || _link is not null) Spans.Add(new Span(start, text.Length, style, _link));
        }
    }

    private sealed class InlineWriter
    {
        private static readonly Style[] Order = { Style.Strike, Style.Bold, Style.Italic, Style.Underline, Style.Code };

        private readonly StringBuilder _output;
        private readonly bool _ignoreBold;
        private readonly List<Style> _open = new();
        private string? _openLink;
        private bool _atLineStart;
        private string _pendingSpace = string.Empty;

        public InlineWriter(StringBuilder output, bool escapeLineStart, bool ignoreBold)
        {
            _output = output;
            _atLineStart = escapeLineStart;
            _ignoreBold = ignoreBold;
        }

        public void WriteText(string text, Style style, string? link)
        {
            if (_ignoreBold) style &= ~Style.Bold;

            int lead = text.Length - text.TrimStart().Length;
            string core = text.Trim();
            if (core.Length == 0)
            {
                _pendingSpace += text;
                return;
            }

            Transition(style, link, text[..lead]);
            _output.Append(style.HasFlag(Style.Code) ? core : Escape(core));
            _atLineStart = false;
            _pendingSpace = text[(lead + core.Length)..];
        }

        public void WriteImage(string asset, Style style, string? link)
        {
            if (_ignoreBold) style &= ~Style.Bold;
            Transition(style, link, string.Empty);
            _output.Append("![](").Append(asset.Replace(" ", "%20")).Append(')');
            _atLineStart = false;
        }

        public void Finish()
        {
            CloseFrom(0);
            if (_openLink is not null) CloseLink();
        }

        private void Transition(Style style, string? link, string leadingSpace)
        {
            if (_openLink != link)
            {
                CloseFrom(0);
                if (_openLink is not null) CloseLink();
            }
            else
            {
                int firstStale = _open.FindIndex(s => !style.HasFlag(s));
                if (firstStale >= 0) CloseFrom(firstStale);
            }

            _output.Append(_pendingSpace).Append(leadingSpace);
            _pendingSpace = string.Empty;

            if (link is not null && _openLink is null)
            {
                _output.Append('[');
                _openLink = link;
            }

            foreach (Style s in Order)
            {
                if (!style.HasFlag(s) || _open.Contains(s)) continue;
                _output.Append(Opener(s));
                _open.Add(s);
            }
        }

        private void CloseFrom(int index)
        {
            for (int i = _open.Count - 1; i >= index; i--)
            {
                _output.Append(Closer(_open[i]));
                _open.RemoveAt(i);
            }
        }

        private void CloseLink()
        {
            _output.Append("](").Append(_openLink).Append(')');
            _openLink = null;
        }

        private static string Opener(Style style) => style switch
        {
            Style.Strike => "~~",
            Style.Bold => "**",
            Style.Italic => "*",
            Style.Underline => "<u>",
            _ => "`",
        };

        private static string Closer(Style style) => style == Style.Underline ? "</u>" : Opener(style);

        private string Escape(string text)
        {
            var escaped = new StringBuilder(text.Length + 8);
            foreach (char c in text)
            {
                if (c is '\\' or '`' or '*' or '_' or '~' or '[' or ']' or '<' or '>') escaped.Append('\\');
                escaped.Append(c);
            }

            string result = escaped.ToString();
            if (_atLineStart && (Regex.IsMatch(result, @"^(#|\+|-|=|\d+[.)])") || result == RuleText)) result = "\\" + result;
            return result;
        }
    }
}
