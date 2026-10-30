using System;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Core.Models;
using PdfEditorApp.Core.Models.Elements;
using PdfEditorApp.Models;
using PdfEditorApp.Services;
using PdfEditorApp.Services.Typography;

namespace PdfEditorApp.ViewModels.ElementViewModels;

public partial class TextElementViewModel : ElementViewModelBase
{
    [ObservableProperty]
    private string _text = "Enter text here";

    partial void OnTextChanged(string value)
    {
        if (IsInEditMode)
        {
            double reqH = CalculateRequiredHeight();
            if (reqH > Height)
            {
                Height = Math.Ceiling(reqH + 4);
            }
        }
    }

    [ObservableProperty]
    private System.Collections.Generic.List<PdfTextSpan>? _spans;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvaloniaFontFamily))]
    private string _fontFamily = "Arial";

    public FontFamily AvaloniaFontFamily => FontHelper.CreateFontFamily(FontFamily);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComputedLineHeight))]
    private double _fontSize = 14;

    [ObservableProperty]
    private bool _isBold;

    [ObservableProperty]
    private bool _isItalic;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextDecorations))]
    private bool _isUnderline;

    [ObservableProperty]
    private bool _isDoubleUnderline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextDecorations))]
    private bool _isStrikethrough;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextBrush))]
    private string _textColorHex = "#201F1E";

    public IBrush TextBrush => HexToBrush(TextColorHex, "#201F1E");

    [ObservableProperty]
    private double _textOpacity = 1.0;

    [ObservableProperty]
    private TextAlignmentMode _alignment = TextAlignmentMode.Left;

    [ObservableProperty]
    private TextVerticalAlignment _verticalAlignment = TextVerticalAlignment.Top;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComputedLineHeight))]
    private double _lineHeight = 1.4;

    [ObservableProperty]
    private double _characterSpacing = 0;

    [ObservableProperty]
    private double _wordSpacing = 0;

    [ObservableProperty]
    private double _paragraphSpacing = 0;

    [ObservableProperty]
    private bool _textWrap = true;

    // Stroke / Outline
    [ObservableProperty]
    private bool _hasStroke;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrokeBrush))]
    private string _strokeColorHex = "#000000";

    public IBrush StrokeBrush => HexToBrush(StrokeColorHex, "#000000");

    [ObservableProperty]
    private double _strokeWidth = 1.0;

    // Shadow & Glow
    [ObservableProperty]
    private bool _hasShadow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShadowBrush))]
    private string _shadowColorHex = "#80000000";

    public IBrush ShadowBrush => HexToBrush(ShadowColorHex, "#80000000");

    [ObservableProperty]
    private double _shadowOffsetX = 2.0;

    [ObservableProperty]
    private double _shadowOffsetY = 2.0;

    [ObservableProperty]
    private double _shadowBlurRadius = 4.0;

    [ObservableProperty]
    private double _shadowOpacity = 0.5;

    // Box Background, Border & Padding
    [ObservableProperty]
    private double _padding = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundBrush))]
    private string _backgroundColorHex = "#00000000";

    public IBrush BackgroundBrush => HexToBrush(BackgroundColorHex, "#00000000");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BorderBrush))]
    private string _borderColorHex = "#00000000";

    public IBrush BorderBrush => HexToBrush(BorderColorHex, "#00000000");

    [ObservableProperty]
    private double _borderThickness = 0;

    [ObservableProperty]
    private double _cornerRadius = 0;

    // Curved & Circular Typography
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNormalMode))]
    [NotifyPropertyChangedFor(nameof(IsCurvedMode))]
    [NotifyPropertyChangedFor(nameof(IsCircularMode))]
    private TextShapeMode _shapeMode = TextShapeMode.Normal;

    public bool IsNormalMode => ShapeMode == TextShapeMode.Normal;
    public bool IsCurvedMode => ShapeMode == TextShapeMode.Curved;
    public bool IsCircularMode => ShapeMode == TextShapeMode.Circular;
    public bool IsBezierMode => ShapeMode == TextShapeMode.BezierCurve;

    [ObservableProperty]
    private double _curveRadius = 120;

    [ObservableProperty]
    private double _curveArcAngle = 180;

    [ObservableProperty]
    private double _curveStartAngle = 0;

    [ObservableProperty]
    private bool _curveClockwise = true;

    [ObservableProperty]
    private bool _curveInvert;

    [ObservableProperty]
    private CircularTextPlacement _circularPlacement = CircularTextPlacement.TopArc;

    // Per-Character Transforms & Baseline Offsets
    [ObservableProperty]
    private double _baselineShift = 0;

    [ObservableProperty]
    private double _characterRotation = 0;

    [ObservableProperty]
    private double _scaleX = 1.0;

    [ObservableProperty]
    private double _scaleY = 1.0;

    [ObservableProperty]
    private bool _flipX;

    [ObservableProperty]
    private bool _flipY;

    // Bézier Curve Typography (Normalized 0.0 to 1.0)
    [ObservableProperty]
    private BezierCurvePreset _bezierPreset = BezierCurvePreset.Wave;

    [ObservableProperty]
    private double _bezierP0X = 0.0;

    [ObservableProperty]
    private double _bezierP0Y = 0.5;

    [ObservableProperty]
    private double _bezierP1X = 0.33;

    [ObservableProperty]
    private double _bezierP1Y = 0.10;

    [ObservableProperty]
    private double _bezierP2X = 0.67;

    [ObservableProperty]
    private double _bezierP2Y = 0.90;

    [ObservableProperty]
    private double _bezierP3X = 1.0;

    [ObservableProperty]
    private double _bezierP3Y = 0.5;

    public TextDecorationCollection? TextDecorations
    {
        get
        {
            if (IsUnderline && IsStrikethrough)
            {
                var decs = new TextDecorationCollection();
                foreach (var d in Avalonia.Media.TextDecorations.Underline) decs.Add(d);
                foreach (var d in Avalonia.Media.TextDecorations.Strikethrough) decs.Add(d);
                return decs;
            }
            if (IsUnderline) return Avalonia.Media.TextDecorations.Underline;
            if (IsStrikethrough) return Avalonia.Media.TextDecorations.Strikethrough;
            return null;
        }
    }

    public double ComputedLineHeight => FontSize * (LineHeight > 0.1 ? LineHeight : 1.2);

    public override ElementKind Kind => ElementKind.Text;
    public override string DisplayName => Text.Length > 20 ? Text.Substring(0, 17) + "..." : (string.IsNullOrWhiteSpace(Text) ? "Text Box" : Text);

    public TextElementViewModel(PdfTextElement? element = null)
    {
        if (element != null)
        {
            LoadFromModel(element);
        }
    }

    private static IBrush HexToBrush(string? hex, string fallbackHex = "#00000000")
    {
        try
        {
            string targetHex = !string.IsNullOrWhiteSpace(hex) ? hex : fallbackHex;
            if (targetHex.Equals("Transparent", StringComparison.OrdinalIgnoreCase) || targetHex == "#00000000")
            {
                return Brushes.Transparent;
            }
            if (Color.TryParse(targetHex, out var color))
            {
                return new SolidColorBrush(color);
            }
        }
        catch { }
        return Brushes.Transparent;
    }

    public void TransformUppercase()
    {
        if (!string.IsNullOrEmpty(Text)) Text = Text.ToUpperInvariant();
    }

    public void TransformLowercase()
    {
        if (!string.IsNullOrEmpty(Text)) Text = Text.ToLowerInvariant();
    }

    public void TransformTitleCase()
    {
        if (!string.IsNullOrEmpty(Text))
        {
            var ti = System.Globalization.CultureInfo.CurrentCulture.TextInfo;
            Text = ti.ToTitleCase(Text.ToLower());
        }
    }

    public void TransformCapitalize()
    {
        if (!string.IsNullOrEmpty(Text))
        {
            var sentences = Text.Split('.');
            for (int i = 0; i < sentences.Length; i++)
            {
                var s = sentences[i].TrimStart();
                if (s.Length > 0)
                {
                    sentences[i] = char.ToUpperInvariant(s[0]) + (s.Length > 1 ? s.Substring(1) : "");
                }
            }
            Text = string.Join(". ", sentences);
        }
    }

    public void ToggleBulletList()
    {
        if (string.IsNullOrEmpty(Text)) return;
        var lines = Text.Split('\n');
        bool allBulleted = lines.All(l => l.TrimStart().StartsWith("• "));
        if (allBulleted)
        {
            Text = string.Join('\n', lines.Select(l => l.TrimStart().StartsWith("• ") ? l.TrimStart().Substring(2) : l));
        }
        else
        {
            Text = string.Join('\n', lines.Select(l => string.IsNullOrWhiteSpace(l) ? l : (l.TrimStart().StartsWith("• ") ? l : $"• {l}")));
        }
    }

    public void ToggleNumberedList()
    {
        if (string.IsNullOrEmpty(Text)) return;
        var lines = Text.Split('\n');
        int count = 1;
        Text = string.Join('\n', lines.Select(l =>
        {
            if (string.IsNullOrWhiteSpace(l)) return l;
            var trimmed = l.TrimStart();
            int dotIdx = trimmed.IndexOf('.');
            if (dotIdx > 0 && int.TryParse(trimmed.Substring(0, dotIdx), out _))
            {
                return trimmed.Substring(dotIdx + 1).TrimStart();
            }
            return $"{count++}. {trimmed}";
        }));
    }

    public void ApplyTypographyPreset(string presetName)
    {
        switch (presetName.ToLowerInvariant())
        {
            case "archup":
            case "arch-up":
                ShapeMode = TextShapeMode.Curved;
                CurveClockwise = true;
                CurveRadius = Math.Max(60, Width * 0.6);
                CurveArcAngle = 140;
                CurveStartAngle = 0;
                CurveInvert = false;
                break;

            case "archdown":
            case "arch-down":
                ShapeMode = TextShapeMode.Curved;
                CurveClockwise = false;
                CurveRadius = Math.Max(60, Width * 0.6);
                CurveArcAngle = 140;
                CurveStartAngle = 0;
                CurveInvert = false;
                break;

            case "circlebadge":
            case "circle-badge":
                ShapeMode = TextShapeMode.Circular;
                CircularPlacement = CircularTextPlacement.FullCircle;
                CurveRadius = Math.Max(40, Math.Min(Width, Height) / 2.0 - FontSize);
                CurveArcAngle = 360;
                CurveClockwise = true;
                CurveInvert = false;
                break;

            case "toparc":
            case "top-arc":
                ShapeMode = TextShapeMode.Circular;
                CircularPlacement = CircularTextPlacement.TopArc;
                CurveRadius = Math.Max(40, Math.Min(Width, Height) / 2.0 - FontSize);
                CurveArcAngle = 180;
                CurveClockwise = true;
                CurveInvert = false;
                break;

            case "bottomarc":
            case "bottom-arc":
                ShapeMode = TextShapeMode.Circular;
                CircularPlacement = CircularTextPlacement.BottomArc;
                CurveRadius = Math.Max(40, Math.Min(Width, Height) / 2.0 - FontSize);
                CurveArcAngle = 180;
                CurveClockwise = true;
                CurveInvert = false;
                break;

            case "outlined":
                HasStroke = true;
                StrokeColorHex = "#0F6CBD";
                StrokeWidth = 1.5;
                TextColorHex = "#FFFFFF";
                break;

            case "shadowheading":
            case "shadow-heading":
                HasShadow = true;
                ShadowColorHex = "#60000000";
                ShadowOffsetX = 3.0;
                ShadowOffsetY = 3.0;
                ShadowBlurRadius = 6.0;
                ShadowOpacity = 0.6;
                break;

            case "wave":
                ShapeMode = TextShapeMode.BezierCurve;
                ApplyBezierPreset(BezierCurvePreset.Wave);
                break;

            case "scurve":
            case "s-curve":
                ShapeMode = TextShapeMode.BezierCurve;
                ApplyBezierPreset(BezierCurvePreset.SCurve);
                break;

            case "bridge":
                ShapeMode = TextShapeMode.BezierCurve;
                ApplyBezierPreset(BezierCurvePreset.Bridge);
                break;

            case "valley":
                ShapeMode = TextShapeMode.BezierCurve;
                ApplyBezierPreset(BezierCurvePreset.Valley);
                break;

            case "rise":
                ShapeMode = TextShapeMode.BezierCurve;
                ApplyBezierPreset(BezierCurvePreset.Rise);
                break;

            case "neonglow":
            case "neon-glow":
                HasShadow = true;
                ShadowColorHex = "#00F0FF";
                ShadowOffsetX = 0.0;
                ShadowOffsetY = 0.0;
                ShadowBlurRadius = 12.0;
                ShadowOpacity = 0.85;
                TextColorHex = "#FFFFFF";
                break;

            case "reset":
            case "normal":
                ShapeMode = TextShapeMode.Normal;
                HasStroke = false;
                HasShadow = false;
                ScaleX = 1.0;
                ScaleY = 1.0;
                FlipX = false;
                FlipY = false;
                BaselineShift = 0;
                CharacterRotation = 0;
                break;
        }
    }

    public void ApplyBezierPreset(BezierCurvePreset preset)
    {
        BezierPreset = preset;
        var pts = TextLayoutEngine.GetPresetBezierControlPoints(preset);
        BezierP0X = pts.P0.X;
        BezierP0Y = pts.P0.Y;
        BezierP1X = pts.P1.X;
        BezierP1Y = pts.P1.Y;
        BezierP2X = pts.P2.X;
        BezierP2Y = pts.P2.Y;
        BezierP3X = pts.P3.X;
        BezierP3Y = pts.P3.Y;
    }

    public void ResetTransforms()
    {
        ScaleX = 1.0;
        ScaleY = 1.0;
        FlipX = false;
        FlipY = false;
        BaselineShift = 0;
        CharacterRotation = 0;
    }

    public double CalculateRequiredHeight()
    {
        var model = (PdfTextElement)ToModel();
        var dims = TextLayoutEngine.CalculateRequiredDimensions(model);
        return dims.Height;
    }

    public double CalculateRequiredWidth()
    {
        var model = (PdfTextElement)ToModel();
        var dims = TextLayoutEngine.CalculateRequiredDimensions(model);
        return dims.Width;
    }

    [RelayCommand]
    public void AutoFitHeight()
    {
        double req = CalculateRequiredHeight();
        if (req > 0)
        {
            Height = Math.Ceiling(req);
        }
    }

    [RelayCommand]
    public void AutoFitWidth()
    {
        double req = CalculateRequiredWidth();
        if (req > 0)
        {
            Width = Math.Ceiling(req);
        }
    }

    [RelayCommand]
    public void AutoFitBoth()
    {
        var model = (PdfTextElement)ToModel();
        var dims = TextLayoutEngine.CalculateRequiredDimensions(model);
        Width = Math.Ceiling(dims.Width);
        Height = Math.Ceiling(dims.Height);
    }

    public void SetMarkdownText(string markdown)
    {
        string cleanMarkdown = RichTextHelper.StripExtendedTags(markdown);
        var model = (PdfTextElement)ToModel();
        var parsedSpans = RichTextHelper.ParseMarkdownToSpans(cleanMarkdown, model);
        if (parsedSpans.Count > 1 || parsedSpans.Any(s => s.IsBold == true || s.IsItalic == true || s.IsUnderline == true || s.IsStrikethrough == true || !string.IsNullOrEmpty(s.TextColorHex) || s.Script != TextScriptMode.Normal))
        {
            Spans = parsedSpans;
            Text = RichTextHelper.SpansToPlainText(parsedSpans);
        }
        else
        {
            Spans = null;
            Text = RichTextHelper.SpansToPlainText(parsedSpans);
        }
    }

    public string GetMarkdownText()
    {
        if (Spans != null && Spans.Count > 0)
        {
            var model = (PdfTextElement)ToModel();
            return RichTextHelper.StripExtendedTags(RichTextHelper.SpansToMarkdown(Spans, model, includeExtendedTags: false));
        }
        return RichTextHelper.StripExtendedTags(Text ?? string.Empty);
    }

    public void ClearSpans()
    {
        Spans = null;
    }

    [ObservableProperty]
    private int _activeSelectionStart;

    [ObservableProperty]
    private int _activeSelectionLength;

    [ObservableProperty]
    private string _activeSelectedText = string.Empty;

    public bool HasTextSelection => !string.IsNullOrEmpty(ActiveSelectedText) && ActiveSelectionLength > 0;

    public void UpdateTextSelection(int start, int length, string selectedText)
    {
        ActiveSelectionStart = start;
        ActiveSelectionLength = length;
        ActiveSelectedText = selectedText ?? string.Empty;
        OnPropertyChanged(nameof(HasTextSelection));
    }

    public void ClearTextSelection()
    {
        ActiveSelectionStart = 0;
        ActiveSelectionLength = 0;
        ActiveSelectedText = string.Empty;
        OnPropertyChanged(nameof(HasTextSelection));
    }

    public bool ApplyInlineFormatting(InlineFormatType formatType, string? argument = null)
    {
        if (!HasTextSelection || string.IsNullOrEmpty(Text)) return false;

        var result = RichTextHelper.ToggleInlineFormatting(
            Text,
            ActiveSelectionStart,
            ActiveSelectionLength,
            formatType,
            argument);

        if (result.NewText == Text) return false;

        Text = result.NewText;
        ActiveSelectionStart = result.NewSelectionStart;
        ActiveSelectionLength = result.NewSelectionLength;
        ActiveSelectedText = result.NewSelectionLength > 0 && result.NewSelectionStart + result.NewSelectionLength <= Text.Length
            ? Text.Substring(result.NewSelectionStart, result.NewSelectionLength)
            : string.Empty;
        OnPropertyChanged(nameof(HasTextSelection));

        // Update parsed spans in real-time
        var model = (PdfTextElement)ToModel();
        var parsed = RichTextHelper.ParseMarkdownToSpans(Text, model);
        Spans = (parsed.Count > 1 || parsed.Any(s => s.IsBold == true || s.IsItalic == true || s.IsUnderline == true || s.IsStrikethrough == true || !string.IsNullOrEmpty(s.TextColorHex) || s.Script != TextScriptMode.Normal))
            ? parsed
            : null;

        if (!IsInEditMode && Spans != null && Spans.Count > 0)
        {
            Text = RichTextHelper.SpansToPlainText(Spans);
        }

        return true;
    }

    protected override void OnEditModeChanged(bool isInEditMode)
    {
        if (isInEditMode)
        {
            if (Spans != null && Spans.Count > 0)
            {
                Text = GetMarkdownText();
            }
            else
            {
                Text = RichTextHelper.StripExtendedTags(Text);
            }

            // Ensure bounding height accommodates all text so top lines never scroll off
            double reqH = CalculateRequiredHeight();
            if (reqH > Height)
            {
                Height = Math.Ceiling(reqH + 4);
            }
        }
        else
        {
            ClearTextSelection();
            if (!string.IsNullOrEmpty(Text))
            {
                SetMarkdownText(Text);
            }

            double reqH = CalculateRequiredHeight();
            if (reqH > Height)
            {
                Height = Math.Ceiling(reqH + 4);
            }
        }
    }

    public override PdfElementBase ToModel()
    {
        var el = new PdfTextElement
        {
            Id = Id,
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
            ZIndex = ZIndex,
            Rotation = Rotation,
            Opacity = Opacity,
            IsLocked = IsLocked,
            GroupId = GroupId,
            Text = Text,
            FontFamily = FontFamily,
            FontSize = FontSize,
            IsBold = IsBold,
            IsItalic = IsItalic,
            IsUnderline = IsUnderline,
            IsDoubleUnderline = IsDoubleUnderline,
            IsStrikethrough = IsStrikethrough,
            TextColorHex = TextColorHex,
            TextOpacity = TextOpacity,
            Alignment = Alignment,
            VerticalAlignment = VerticalAlignment,
            LineHeight = LineHeight,
            CharacterSpacing = CharacterSpacing,
            WordSpacing = WordSpacing,
            ParagraphSpacing = ParagraphSpacing,
            TextWrap = TextWrap,
            HasStroke = HasStroke,
            StrokeColorHex = StrokeColorHex,
            StrokeWidth = StrokeWidth,
            HasShadow = HasShadow,
            ShadowColorHex = ShadowColorHex,
            ShadowOffsetX = ShadowOffsetX,
            ShadowOffsetY = ShadowOffsetY,
            ShadowBlurRadius = ShadowBlurRadius,
            ShadowOpacity = ShadowOpacity,
            Padding = Padding,
            BackgroundColorHex = BackgroundColorHex,
            BorderColorHex = BorderColorHex,
            BorderThickness = BorderThickness,
            CornerRadius = CornerRadius,
            ShapeMode = ShapeMode,
            CurveRadius = CurveRadius,
            CurveArcAngle = CurveArcAngle,
            CurveStartAngle = CurveStartAngle,
            CurveClockwise = CurveClockwise,
            CurveInvert = CurveInvert,
            CircularPlacement = CircularPlacement,
            BaselineShift = BaselineShift,
            CharacterRotation = CharacterRotation,
            ScaleX = ScaleX,
            ScaleY = ScaleY,
            FlipX = FlipX,
            FlipY = FlipY,
            BezierPreset = BezierPreset,
            BezierP0X = BezierP0X,
            BezierP0Y = BezierP0Y,
            BezierP1X = BezierP1X,
            BezierP1Y = BezierP1Y,
            BezierP2X = BezierP2X,
            BezierP2Y = BezierP2Y,
            BezierP3X = BezierP3X,
            BezierP3Y = BezierP3Y
        };

        if (Spans != null)
        {
            el.Spans = new System.Collections.Generic.List<PdfTextSpan>(Spans.Count);
            foreach (var span in Spans)
            {
                el.Spans.Add(span.Clone());
            }
        }

        return el;
    }

    public override void LoadFromModel(PdfElementBase model)
    {
        if (model is PdfTextElement textModel)
        {
            Id = textModel.Id;
            X = textModel.X;
            Y = textModel.Y;
            Width = textModel.Width;
            Height = textModel.Height;
            ZIndex = textModel.ZIndex;
            Rotation = textModel.Rotation;
            Opacity = textModel.Opacity;
            IsLocked = textModel.IsLocked;
            GroupId = textModel.GroupId;

            Text = textModel.Text;
            FontFamily = textModel.FontFamily;
            FontSize = textModel.FontSize;
            IsBold = textModel.IsBold;
            IsItalic = textModel.IsItalic;
            IsUnderline = textModel.IsUnderline;
            IsDoubleUnderline = textModel.IsDoubleUnderline;
            IsStrikethrough = textModel.IsStrikethrough;
            TextColorHex = textModel.TextColorHex;
            TextOpacity = textModel.TextOpacity;
            Alignment = textModel.Alignment;
            VerticalAlignment = textModel.VerticalAlignment;
            LineHeight = textModel.LineHeight;
            CharacterSpacing = textModel.CharacterSpacing;
            WordSpacing = textModel.WordSpacing;
            ParagraphSpacing = textModel.ParagraphSpacing;
            TextWrap = textModel.TextWrap;
            HasStroke = textModel.HasStroke;
            StrokeColorHex = textModel.StrokeColorHex;
            StrokeWidth = textModel.StrokeWidth;
            HasShadow = textModel.HasShadow;
            ShadowColorHex = textModel.ShadowColorHex;
            ShadowOffsetX = textModel.ShadowOffsetX;
            ShadowOffsetY = textModel.ShadowOffsetY;
            ShadowBlurRadius = textModel.ShadowBlurRadius;
            ShadowOpacity = textModel.ShadowOpacity;
            Padding = textModel.Padding;
            BackgroundColorHex = textModel.BackgroundColorHex;
            BorderColorHex = textModel.BorderColorHex;
            BorderThickness = textModel.BorderThickness;
            CornerRadius = textModel.CornerRadius;
            ShapeMode = textModel.ShapeMode;
            CurveRadius = textModel.CurveRadius;
            CurveArcAngle = textModel.CurveArcAngle;
            CurveStartAngle = textModel.CurveStartAngle;
            CurveClockwise = textModel.CurveClockwise;
            CurveInvert = textModel.CurveInvert;
            CircularPlacement = textModel.CircularPlacement;
            BaselineShift = textModel.BaselineShift;
            CharacterRotation = textModel.CharacterRotation;
            ScaleX = textModel.ScaleX;
            ScaleY = textModel.ScaleY;
            FlipX = textModel.FlipX;
            FlipY = textModel.FlipY;
            BezierPreset = textModel.BezierPreset;
            BezierP0X = textModel.BezierP0X;
            BezierP0Y = textModel.BezierP0Y;
            BezierP1X = textModel.BezierP1X;
            BezierP1Y = textModel.BezierP1Y;
            BezierP2X = textModel.BezierP2X;
            BezierP2Y = textModel.BezierP2Y;
            BezierP3X = textModel.BezierP3X;
            BezierP3Y = textModel.BezierP3Y;

            if (textModel.Spans != null)
            {
                Spans = new System.Collections.Generic.List<PdfTextSpan>(textModel.Spans.Count);
                foreach (var span in textModel.Spans)
                {
                    Spans.Add(span.Clone());
                }
            }
            else
            {
                Spans = null;
            }
        }
    }
}
