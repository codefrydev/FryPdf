using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfEditorApp.Core.Models.Elements;

namespace PdfEditorApp.Services.Typography;

/// <summary>
/// High-performance parser and serializer for rich text formatting.
/// Translates between inline Markdown/HTML tags, plain text, and structured <see cref="PdfTextSpan"/> runs.
/// </summary>
public static class RichTextHelper
{
    private static readonly Regex ColorTagRegex = new(@"<color\s*=\s*(?<col>[#\w\d]+)>(?<content>.*?)</color>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex UnderlineTagRegex = new(@"<u>(?<content>.*?)</u>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LinkTagRegex = new(@"\[(?<label>[^\]]+)\]\((?<url>[^\)]+)\)", RegexOptions.Compiled);
    private static readonly Regex ExtendedTagsRegex = new(@"</?(?:font|size|color|mark|highlight)(?:\s*=[^>]*?)?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Strips any raw pseudo-HTML markup tags (&lt;font=...&gt;, &lt;size=...&gt;, &lt;color=...&gt;, &lt;mark=...&gt;, etc.) from a text string,
    /// preserving the clean human-readable content and standard Markdown.
    /// </summary>
    public static string StripExtendedTags(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (!text.Contains('<')) return text;
        return ExtendedTagsRegex.Replace(text, string.Empty);
    }

    /// <summary>
    /// Parses an inline formatted string containing Markdown / HTML tag notation into structured <see cref="PdfTextSpan"/> items.
    /// Supports **bold**, *italic*, ~~strikethrough~~, <u>underline</u>, ^superscript^, ~subscript~, &lt;color=#HEX&gt;text&lt;/color&gt;, and [label](url).
    /// </summary>
    public static List<PdfTextSpan> ParseMarkdownToSpans(string? input, PdfTextElement? baseStyle = null)
    {
        var spans = new List<PdfTextSpan>();
        if (string.IsNullOrEmpty(input))
        {
            return spans;
        }

        // Fast path: If no markdown/tag tokens exist, return single span with plain text
        if (!ContainsFormattingTokens(input))
        {
            spans.Add(new PdfTextSpan
            {
                Text = input,
                FontFamily = baseStyle?.FontFamily,
                FontSize = baseStyle?.FontSize,
                IsBold = baseStyle?.IsBold,
                IsItalic = baseStyle?.IsItalic,
                IsUnderline = baseStyle?.IsUnderline,
                IsStrikethrough = baseStyle?.IsStrikethrough,
                TextColorHex = baseStyle?.TextColorHex
            });
            return spans;
        }

        // Token-based recursive/stack parser for nested rich text
        ParseFormattedString(input, spans, baseStyle, new SpanStyleState
        {
            FontFamily = baseStyle?.FontFamily,
            FontSize = baseStyle?.FontSize,
            IsBold = baseStyle?.IsBold ?? false,
            IsItalic = baseStyle?.IsItalic ?? false,
            IsUnderline = baseStyle?.IsUnderline ?? false,
            IsStrikethrough = baseStyle?.IsStrikethrough ?? false,
            TextColorHex = baseStyle?.TextColorHex,
            Script = TextScriptMode.Normal
        });

        return NormalizeSpans(spans);
    }

    private static bool ContainsFormattingTokens(string text)
    {
        return text.Contains("**") || text.Contains('*') ||
               text.Contains("~~") || text.Contains("<u>") || text.Contains("<color") ||
               text.Contains("<size") || text.Contains("<font") ||
               text.Contains('^') || text.Contains('~') || (text.Contains('[') && text.Contains('('));
    }

    private struct SpanStyleState
    {
        public string? FontFamily;
        public double? FontSize;
        public bool IsBold;
        public bool IsItalic;
        public bool IsUnderline;
        public bool IsStrikethrough;
        public string? TextColorHex;
        public string? HighlightColorHex;
        public TextScriptMode Script;
        public string? LinkUrl;

        public SpanStyleState Clone() => (SpanStyleState)MemberwiseClone();
    }

    private static void ParseFormattedString(
        string text,
        List<PdfTextSpan> output,
        PdfTextElement? baseStyle,
        SpanStyleState currentStyle)
    {
        int pos = 0;
        int len = text.Length;
        var buffer = new StringBuilder();

        void FlushBuffer()
        {
            if (buffer.Length > 0)
            {
                output.Add(new PdfTextSpan
                {
                    Text = buffer.ToString(),
                    FontFamily = currentStyle.FontFamily ?? baseStyle?.FontFamily,
                    FontSize = currentStyle.FontSize ?? baseStyle?.FontSize,
                    IsBold = currentStyle.IsBold,
                    IsItalic = currentStyle.IsItalic,
                    IsUnderline = currentStyle.IsUnderline,
                    IsStrikethrough = currentStyle.IsStrikethrough,
                    TextColorHex = currentStyle.TextColorHex ?? baseStyle?.TextColorHex,
                    HighlightColorHex = currentStyle.HighlightColorHex,
                    Script = currentStyle.Script,
                    LinkUrl = currentStyle.LinkUrl
                });
                buffer.Clear();
            }
        }

        while (pos < len)
        {
            // 1. Color tag <color=#HEX>...</color>
            if (text.AsSpan(pos).StartsWith("<color", StringComparison.OrdinalIgnoreCase))
            {
                int tagClose = text.IndexOf('>', pos);
                if (tagClose > pos)
                {
                    string openTag = text.Substring(pos, tagClose - pos + 1);
                    int colEqual = openTag.IndexOf('=');
                    if (colEqual > 0)
                    {
                        string colorVal = openTag.Substring(colEqual + 1).Trim(' ', '>', '"', '\'');
                        int endTag = text.IndexOf("</color>", tagClose + 1, StringComparison.OrdinalIgnoreCase);
                        if (endTag > tagClose)
                        {
                            FlushBuffer();
                            string inner = text.Substring(tagClose + 1, endTag - tagClose - 1);
                            var nextStyle = currentStyle.Clone();
                            nextStyle.TextColorHex = NormalizeHexColor(colorVal);
                            ParseFormattedString(inner, output, baseStyle, nextStyle);
                            pos = endTag + "</color>".Length;
                            continue;
                        }
                    }
                }
            }

            // 1b. Highlight/Mark tag <mark=#HEX>...</mark> or <highlight=#HEX>...</highlight>
            if (text.AsSpan(pos).StartsWith("<mark", StringComparison.OrdinalIgnoreCase) || text.AsSpan(pos).StartsWith("<highlight", StringComparison.OrdinalIgnoreCase))
            {
                bool isMark = text.AsSpan(pos).StartsWith("<mark", StringComparison.OrdinalIgnoreCase);
                string closeTagName = isMark ? "</mark>" : "</highlight>";
                int tagClose = text.IndexOf('>', pos);
                if (tagClose > pos)
                {
                    string openTag = text.Substring(pos, tagClose - pos + 1);
                    int colEqual = openTag.IndexOf('=');
                    string highlightCol = colEqual > 0 ? NormalizeHexColor(openTag.Substring(colEqual + 1).Trim(' ', '>', '"', '\'')) : "#FFEB3B";
                    int endTag = text.IndexOf(closeTagName, tagClose + 1, StringComparison.OrdinalIgnoreCase);
                    if (endTag > tagClose)
                    {
                        FlushBuffer();
                        string inner = text.Substring(tagClose + 1, endTag - tagClose - 1);
                        var nextStyle = currentStyle.Clone();
                        nextStyle.HighlightColorHex = highlightCol;
                        ParseFormattedString(inner, output, baseStyle, nextStyle);
                        pos = endTag + closeTagName.Length;
                        continue;
                    }
                }
            }

            // 1c. Font size tag <size=14>...</size>
            if (text.AsSpan(pos).StartsWith("<size", StringComparison.OrdinalIgnoreCase))
            {
                int tagClose = text.IndexOf('>', pos);
                if (tagClose > pos)
                {
                    string openTag = text.Substring(pos, tagClose - pos + 1);
                    int eq = openTag.IndexOf('=');
                    if (eq > 0 && double.TryParse(openTag.Substring(eq + 1).Trim(' ', '>', '"', '\''), CultureInfo.InvariantCulture, out double sz))
                    {
                        int endTag = text.IndexOf("</size>", tagClose + 1, StringComparison.OrdinalIgnoreCase);
                        if (endTag > tagClose)
                        {
                            FlushBuffer();
                            string inner = text.Substring(tagClose + 1, endTag - tagClose - 1);
                            var nextStyle = currentStyle.Clone();
                            nextStyle.FontSize = sz;
                            ParseFormattedString(inner, output, baseStyle, nextStyle);
                            pos = endTag + "</size>".Length;
                            continue;
                        }
                    }
                }
            }

            // 1d. Font family tag <font=Arial>...</font>
            if (text.AsSpan(pos).StartsWith("<font", StringComparison.OrdinalIgnoreCase))
            {
                int tagClose = text.IndexOf('>', pos);
                if (tagClose > pos)
                {
                    string openTag = text.Substring(pos, tagClose - pos + 1);
                    int eq = openTag.IndexOf('=');
                    if (eq > 0)
                    {
                        string family = openTag.Substring(eq + 1).Trim(' ', '>', '"', '\'');
                        int endTag = text.IndexOf("</font>", tagClose + 1, StringComparison.OrdinalIgnoreCase);
                        if (endTag > tagClose)
                        {
                            FlushBuffer();
                            string inner = text.Substring(tagClose + 1, endTag - tagClose - 1);
                            var nextStyle = currentStyle.Clone();
                            nextStyle.FontFamily = family;
                            ParseFormattedString(inner, output, baseStyle, nextStyle);
                            pos = endTag + "</font>".Length;
                            continue;
                        }
                    }
                }
            }

            // 2. Underline tag <u>...</u>
            if (text.AsSpan(pos).StartsWith("<u>", StringComparison.OrdinalIgnoreCase))
            {
                int endTag = text.IndexOf("</u>", pos + 3, StringComparison.OrdinalIgnoreCase);
                if (endTag > pos)
                {
                    FlushBuffer();
                    string inner = text.Substring(pos + 3, endTag - pos - 3);
                    var nextStyle = currentStyle.Clone();
                    nextStyle.IsUnderline = true;
                    ParseFormattedString(inner, output, baseStyle, nextStyle);
                    pos = endTag + 4;
                    continue;
                }
            }

            // 3. Hyperlink [label](url)
            if (text[pos] == '[')
            {
                int labelEnd = text.IndexOf(']', pos + 1);
                if (labelEnd > pos && labelEnd + 1 < len && text[labelEnd + 1] == '(')
                {
                    int urlEnd = text.IndexOf(')', labelEnd + 2);
                    if (urlEnd > labelEnd)
                    {
                        FlushBuffer();
                        string label = text.Substring(pos + 1, labelEnd - pos - 1);
                        string url = text.Substring(labelEnd + 2, urlEnd - labelEnd - 2);
                        var nextStyle = currentStyle.Clone();
                        nextStyle.LinkUrl = url;
                        nextStyle.IsUnderline = true;
                        if (string.IsNullOrEmpty(nextStyle.TextColorHex))
                        {
                            nextStyle.TextColorHex = "#0F6CBD";
                        }
                        ParseFormattedString(label, output, baseStyle, nextStyle);
                        pos = urlEnd + 1;
                        continue;
                    }
                }
            }

            // 4. Bold **...**
            if (pos + 1 < len && text[pos] == '*' && text[pos + 1] == '*')
            {
                int closing = text.IndexOf("**", pos + 2, StringComparison.Ordinal);
                if (closing > pos + 1)
                {
                    FlushBuffer();
                    string inner = text.Substring(pos + 2, closing - pos - 2);
                    var nextStyle = currentStyle.Clone();
                    nextStyle.IsBold = true;
                    ParseFormattedString(inner, output, baseStyle, nextStyle);
                    pos = closing + 2;
                    continue;
                }
            }

            // 5. Strikethrough ~~...~~
            if (pos + 1 < len && text[pos] == '~' && text[pos + 1] == '~')
            {
                int closing = text.IndexOf("~~", pos + 2, StringComparison.Ordinal);
                if (closing > pos + 1)
                {
                    FlushBuffer();
                    string inner = text.Substring(pos + 2, closing - pos - 2);
                    var nextStyle = currentStyle.Clone();
                    nextStyle.IsStrikethrough = true;
                    ParseFormattedString(inner, output, baseStyle, nextStyle);
                    pos = closing + 2;
                    continue;
                }
            }

            // 6. Subscript ~...~ (single tilde)
            if (text[pos] == '~')
            {
                int closing = text.IndexOf('~', pos + 1);
                if (closing > pos)
                {
                    FlushBuffer();
                    string inner = text.Substring(pos + 1, closing - pos - 1);
                    var nextStyle = currentStyle.Clone();
                    nextStyle.Script = TextScriptMode.Subscript;
                    ParseFormattedString(inner, output, baseStyle, nextStyle);
                    pos = closing + 1;
                    continue;
                }
            }

            // 7. Superscript ^...^
            if (text[pos] == '^')
            {
                int closing = text.IndexOf('^', pos + 1);
                if (closing > pos)
                {
                    FlushBuffer();
                    string inner = text.Substring(pos + 1, closing - pos - 1);
                    var nextStyle = currentStyle.Clone();
                    nextStyle.Script = TextScriptMode.Superscript;
                    ParseFormattedString(inner, output, baseStyle, nextStyle);
                    pos = closing + 1;
                    continue;
                }
            }

            // 8. Italic *...*
            if (text[pos] == '*')
            {
                int closing = text.IndexOf('*', pos + 1);
                if (closing > pos)
                {
                    FlushBuffer();
                    string inner = text.Substring(pos + 1, closing - pos - 1);
                    var nextStyle = currentStyle.Clone();
                    nextStyle.IsItalic = true;
                    ParseFormattedString(inner, output, baseStyle, nextStyle);
                    pos = closing + 1;
                    continue;
                }
            }

            // Regular character
            buffer.Append(text[pos]);
            pos++;
        }

        FlushBuffer();
    }

    private static string NormalizeHexColor(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "#201F1E";
        input = input.Trim();
        if (input.StartsWith("#")) return input;

        // Named CSS color aliases
        return input.ToLowerInvariant() switch
        {
            "red" => "#DC2626",
            "blue" => "#0F6CBD",
            "green" => "#16A34A",
            "yellow" => "#CA8A04",
            "orange" => "#EA580C",
            "purple" => "#9333EA",
            "black" => "#000000",
            "white" => "#FFFFFF",
            "gray" or "grey" => "#6B7280",
            _ => "#" + input
        };
    }

    /// <summary>
    /// Reconstructs a clean Markdown/HTML string representation from structured spans.
    /// Useful for feeding the in-place text editor.
    /// <summary>
    /// Serializes structured <see cref="PdfTextSpan"/> items into Markdown notation.
    /// By default, preserves standard readable Markdown (**bold**, *italic*, <u>underline</u>, ~~strike~~, links)
    /// without polluting user-facing text with raw pseudo-HTML &lt;font=...&gt;, &lt;size=...&gt;, or &lt;color=...&gt; tags.
    /// </summary>
    public static string SpansToMarkdown(IReadOnlyList<PdfTextSpan>? spans, PdfTextElement? baseStyle = null, bool includeExtendedTags = false)
    {
        if (spans == null || spans.Count == 0)
        {
            return baseStyle?.Text ?? string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var span in spans)
        {
            if (string.IsNullOrEmpty(span.Text)) continue;

            string fragment = span.Text;

            // Check script
            if (span.Script == TextScriptMode.Superscript)
            {
                fragment = $"^{fragment}^";
            }
            else if (span.Script == TextScriptMode.Subscript)
            {
                fragment = $"~{fragment}~";
            }

            // Check strikethrough
            if (span.IsStrikethrough == true && baseStyle?.IsStrikethrough != true)
            {
                fragment = $"~~{fragment}~~";
            }

            // Check underline
            if (span.IsUnderline == true && baseStyle?.IsUnderline != true && string.IsNullOrEmpty(span.LinkUrl))
            {
                fragment = $"<u>{fragment}</u>";
            }

            // Check italic
            if (span.IsItalic == true && baseStyle?.IsItalic != true)
            {
                fragment = $"*{fragment}*";
            }

            // Check bold
            if (span.IsBold == true && baseStyle?.IsBold != true)
            {
                fragment = $"**{fragment}**";
            }

            // Extended pseudo-tags (only if explicitly enabled)
            if (includeExtendedTags)
            {
                // Check custom color
                if (!string.IsNullOrEmpty(span.TextColorHex) &&
                    !string.Equals(span.TextColorHex, baseStyle?.TextColorHex, StringComparison.OrdinalIgnoreCase))
                {
                    fragment = $"<color={span.TextColorHex}>{fragment}</color>";
                }

                // Check custom font size
                if (span.FontSize.HasValue && baseStyle?.FontSize != null && Math.Abs(span.FontSize.Value - baseStyle.FontSize) > 0.1)
                {
                    fragment = string.Format(CultureInfo.InvariantCulture, "<size={0:0.#}>{1}</size>", span.FontSize.Value, fragment);
                }

                // Check custom font family
                if (!string.IsNullOrEmpty(span.FontFamily) &&
                    !string.Equals(span.FontFamily, baseStyle?.FontFamily, StringComparison.OrdinalIgnoreCase))
                {
                    fragment = $"<font={span.FontFamily}>{fragment}</font>";
                }
            }

            // Check hyperlink
            if (!string.IsNullOrEmpty(span.LinkUrl))
            {
                fragment = $"[{fragment}]({span.LinkUrl})";
            }

            sb.Append(fragment);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Extracts raw plain text without any markup tokens from a list of spans.
    /// </summary>
    public static string SpansToPlainText(IEnumerable<PdfTextSpan>? spans)
    {
        if (spans == null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var span in spans)
        {
            sb.Append(span.Text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Merges adjacent spans with identical typographic attributes into single unified spans.
    /// </summary>
    /// <summary>
    /// Merges runs of adjacent spans that share the same formatting.
    /// </summary>
    /// <remarks>
    /// Accumulates each run in a StringBuilder rather than with <c>+=</c>, which reallocated
    /// an ever-growing string per span — quadratic in characters for a long uniformly styled
    /// run. The same defect existed in <c>PdfLayoutAnalyzer.NormalizeSpans</c>.
    /// </remarks>
    public static List<PdfTextSpan> NormalizeSpans(List<PdfTextSpan> spans)
    {
        if (spans.Count <= 1) return spans;

        var merged = new List<PdfTextSpan>(spans.Count);
        PdfTextSpan? current = null;
        var runText = new StringBuilder();

        void FlushRun()
        {
            if (current == null) return;
            current.Text = runText.ToString();
            merged.Add(current);
            runText.Clear();
        }

        foreach (var s in spans)
        {
            if (string.IsNullOrEmpty(s.Text)) continue;

            if (current == null)
            {
                current = s.Clone();
                runText.Append(s.Text);
                continue;
            }

            if (CanMergeSpans(current, s))
            {
                runText.Append(s.Text);
            }
            else
            {
                FlushRun();
                current = s.Clone();
                runText.Append(s.Text);
            }
        }

        FlushRun();

        return merged;
    }

    private static bool CanMergeSpans(PdfTextSpan a, PdfTextSpan b)
    {
        return string.Equals(a.FontFamily, b.FontFamily, StringComparison.OrdinalIgnoreCase) &&
               Nullable.Equals(a.FontSize, b.FontSize) &&
               a.IsBold == b.IsBold &&
               a.IsItalic == b.IsItalic &&
               a.IsUnderline == b.IsUnderline &&
               a.IsStrikethrough == b.IsStrikethrough &&
               string.Equals(a.TextColorHex, b.TextColorHex, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.HighlightColorHex, b.HighlightColorHex, StringComparison.OrdinalIgnoreCase) &&
               a.Script == b.Script &&
               string.Equals(a.LinkUrl, b.LinkUrl, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Intelligently wraps or unwraps formatting tokens around a selected text range.
    /// Handles whitespace trimming and toggles existing tags on and off cleanly.
    /// </summary>
    public static InlineFormatResult ToggleInlineFormatting(
        string fullText,
        int selectionStart,
        int selectionLength,
        InlineFormatType formatType,
        string? argument = null)
    {
        if (string.IsNullOrEmpty(fullText) || selectionLength <= 0)
        {
            return new InlineFormatResult(fullText ?? string.Empty, selectionStart, selectionLength);
        }

        int start = Math.Clamp(selectionStart, 0, fullText.Length);
        int length = Math.Clamp(selectionLength, 0, fullText.Length - start);
        if (length <= 0)
        {
            return new InlineFormatResult(fullText, start, 0);
        }

        string rawSelected = fullText.Substring(start, length);

        // Separate any leading or trailing whitespace so users double-clicking words don't format whitespace
        int leadingWsCount = 0;
        while (leadingWsCount < rawSelected.Length && char.IsWhiteSpace(rawSelected[leadingWsCount]))
        {
            leadingWsCount++;
        }

        int trailingWsCount = 0;
        while (trailingWsCount < (rawSelected.Length - leadingWsCount) && char.IsWhiteSpace(rawSelected[rawSelected.Length - 1 - trailingWsCount]))
        {
            trailingWsCount++;
        }

        string leadingWs = rawSelected.Substring(0, leadingWsCount);
        string trailingWs = rawSelected.Substring(rawSelected.Length - trailingWsCount);
        string core = rawSelected.Substring(leadingWsCount, rawSelected.Length - leadingWsCount - trailingWsCount);
        int coreStart = start + leadingWsCount;
        int coreLength = core.Length;

        if (coreLength == 0)
        {
            return new InlineFormatResult(fullText, start, length);
        }

        string beforeCore = fullText.Substring(0, coreStart);
        string afterCore = fullText.Substring(coreStart + coreLength);

        string openTag;
        string closeTag;
        bool isColor = formatType == InlineFormatType.Color;
        bool isSize = formatType == InlineFormatType.Size;
        bool isFont = formatType == InlineFormatType.Font;

        switch (formatType)
        {
            case InlineFormatType.Bold:
                openTag = "**";
                closeTag = "**";
                break;
            case InlineFormatType.Italic:
                openTag = "*";
                closeTag = "*";
                break;
            case InlineFormatType.Underline:
                openTag = "<u>";
                closeTag = "</u>";
                break;
            case InlineFormatType.Strikethrough:
                openTag = "~~";
                closeTag = "~~";
                break;
            case InlineFormatType.Color:
                openTag = $"<color={argument ?? "#0F6CBD"}>";
                closeTag = "</color>";
                break;
            case InlineFormatType.Size:
                openTag = $"<size={argument ?? "14"}>";
                closeTag = "</size>";
                break;
            case InlineFormatType.Font:
                openTag = $"<font={argument ?? "Arial"}>";
                closeTag = "</font>";
                break;
            default:
                return new InlineFormatResult(fullText, start, length);
        }

        // 1. Check if core itself is wrapped with the tags
        if (isColor)
        {
            if (core.StartsWith("<color=", StringComparison.OrdinalIgnoreCase) && core.EndsWith("</color>", StringComparison.OrdinalIgnoreCase))
            {
                int closeTagIndex = core.IndexOf('>');
                if (closeTagIndex > 0)
                {
                    string inner = core.Substring(closeTagIndex + 1, core.Length - closeTagIndex - 1 - "</color>".Length);
                    string updated = $"{openTag}{inner}{closeTag}";
                    string resultText = beforeCore + updated + afterCore;
                    return new InlineFormatResult(resultText, coreStart - leadingWsCount, leadingWs.Length + updated.Length + trailingWs.Length);
                }
            }
        }
        else if (isSize)
        {
            if (core.StartsWith("<size=", StringComparison.OrdinalIgnoreCase) && core.EndsWith("</size>", StringComparison.OrdinalIgnoreCase))
            {
                int closeTagIndex = core.IndexOf('>');
                if (closeTagIndex > 0)
                {
                    string inner = core.Substring(closeTagIndex + 1, core.Length - closeTagIndex - 1 - "</size>".Length);
                    string updated = $"{openTag}{inner}{closeTag}";
                    string resultText = beforeCore + updated + afterCore;
                    return new InlineFormatResult(resultText, coreStart - leadingWsCount, leadingWs.Length + updated.Length + trailingWs.Length);
                }
            }
        }
        else if (isFont)
        {
            if (core.StartsWith("<font=", StringComparison.OrdinalIgnoreCase) && core.EndsWith("</font>", StringComparison.OrdinalIgnoreCase))
            {
                int closeTagIndex = core.IndexOf('>');
                if (closeTagIndex > 0)
                {
                    string inner = core.Substring(closeTagIndex + 1, core.Length - closeTagIndex - 1 - "</font>".Length);
                    string updated = $"{openTag}{inner}{closeTag}";
                    string resultText = beforeCore + updated + afterCore;
                    return new InlineFormatResult(resultText, coreStart - leadingWsCount, leadingWs.Length + updated.Length + trailingWs.Length);
                }
            }
        }
        else
        {
            // For markdown tokens (**, *, <u>, ~~)
            if (formatType == InlineFormatType.Italic)
            {
                if (core.StartsWith("*") && !core.StartsWith("**") && core.EndsWith("*") && !core.EndsWith("**") && core.Length >= 2)
                {
                    string unwrapped = core.Substring(1, core.Length - 2);
                    string resultText = beforeCore + unwrapped + afterCore;
                    return new InlineFormatResult(resultText, coreStart - leadingWsCount, leadingWs.Length + unwrapped.Length + trailingWs.Length);
                }
            }
            else if (core.StartsWith(openTag) && core.EndsWith(closeTag) && core.Length >= (openTag.Length + closeTag.Length))
            {
                string unwrapped = core.Substring(openTag.Length, core.Length - openTag.Length - closeTag.Length);
                string resultText = beforeCore + unwrapped + afterCore;
                return new InlineFormatResult(resultText, coreStart - leadingWsCount, leadingWs.Length + unwrapped.Length + trailingWs.Length);
            }
        }

        // 2. Check if the text surrounding core is wrapped with the tags
        if (formatType == InlineFormatType.Italic)
        {
            if (beforeCore.EndsWith("*") && !beforeCore.EndsWith("**") && afterCore.StartsWith("*") && !afterCore.StartsWith("**"))
            {
                string newBefore = beforeCore.Substring(0, beforeCore.Length - 1);
                string newAfter = afterCore.Substring(1);
                string resultText = newBefore + core + newAfter;
                return new InlineFormatResult(resultText, newBefore.Length - leadingWsCount, leadingWs.Length + core.Length + trailingWs.Length);
            }
        }
        else if (isColor && beforeCore.Contains("<color=") && afterCore.StartsWith("</color>"))
        {
            int lastOpen = beforeCore.LastIndexOf("<color=", StringComparison.OrdinalIgnoreCase);
            int closeOfOpen = beforeCore.IndexOf('>', lastOpen);
            if (lastOpen >= 0 && closeOfOpen > lastOpen && closeOfOpen == beforeCore.Length - 1)
            {
                string newBefore = beforeCore.Substring(0, lastOpen) + openTag;
                string newAfter = afterCore.Substring("</color>".Length) + closeTag;
                string resultText = newBefore + core + newAfter;
                return new InlineFormatResult(resultText, newBefore.Length - leadingWsCount, leadingWs.Length + core.Length + trailingWs.Length);
            }
        }
        else if (beforeCore.EndsWith(openTag) && afterCore.StartsWith(closeTag))
        {
            string newBefore = beforeCore.Substring(0, beforeCore.Length - openTag.Length);
            string newAfter = afterCore.Substring(closeTag.Length);
            string resultText = newBefore + core + newAfter;
            return new InlineFormatResult(resultText, newBefore.Length - leadingWsCount, leadingWs.Length + core.Length + trailingWs.Length);
        }

        // 3. Otherwise: Wrap core
        string wrappedCore = openTag + core + closeTag;
        string finalResultText = beforeCore + wrappedCore + afterCore;
        return new InlineFormatResult(finalResultText, start, leadingWs.Length + wrappedCore.Length + trailingWs.Length);
    }
}

public enum InlineFormatType
{
    Bold,
    Italic,
    Underline,
    Strikethrough,
    Color,
    Size,
    Font
}

public readonly record struct InlineFormatResult(string NewText, int NewSelectionStart, int NewSelectionLength);
