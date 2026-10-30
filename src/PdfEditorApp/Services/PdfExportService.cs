using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QRCoder;
using PdfEditorApp.Core.Analysis;
using PdfEditorApp.Core.Models;
using PdfEditorApp.Core.Models.Elements;
using PdfEditorApp.Core.Utils;
using PdfEditorApp.Models;
using PdfEditorApp.Core.Plugins;
using PdfEditorApp.Core.Plugins.Pipelines;
using PdfEditorApp.Services.MathEngine;
using PdfEditorApp.Services.Typography;

namespace PdfEditorApp.Services;

public class PdfExportService : IPdfExportService
{
    private readonly IFryPluginContext? _pluginContext;

    public PdfExportService(IFryPluginContext? pluginContext = null)
    {
        _pluginContext = pluginContext;
    }

    static PdfExportService()
    {
        RegisterEmbeddedFonts();
    }

    private static void RegisterEmbeddedFonts()
    {
        try
        {
            // 1. User on-demand font cache directory
            string userFontDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FryPDF", "Fonts");
            if (Directory.Exists(userFontDir))
            {
                foreach (var fontFile in Directory.GetFiles(userFontDir, "*.*"))
                {
                    string ext = Path.GetExtension(fontFile).ToLowerInvariant();
                    if (ext == ".ttf" || ext == ".otf")
                    {
                        try
                        {
                            using var stream = File.OpenRead(fontFile);
                            QuestPdfFontRegistry.Register(stream);
                        }
                        catch { }
                    }
                }
            }

            // 2. Application bundled fonts
            string[] searchPaths =
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "Fonts"),
                Path.Combine(Directory.GetCurrentDirectory(), "src", "PdfEditorApp", "Assets", "Fonts"),
                Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Fonts")
            };

            foreach (var basePath in searchPaths)
            {
                if (Directory.Exists(basePath))
                {
                    var ttfFiles = Directory.GetFiles(basePath, "*.ttf");
                    foreach (var ttf in ttfFiles)
                    {
                        try
                        {
                            using var stream = File.OpenRead(ttf);
                            QuestPdfFontRegistry.Register(stream);
                        }
                        catch { }
                    }
                    break;
                }
            }
        }
        catch { }
    }

    public byte[] GeneratePdfBytes(PdfDocumentModel model)
    {
        return GeneratePdfBytes(model, null);
    }

    /// <summary>
    /// Synchronous export. Prefer <see cref="ExportToBytesAsync"/> on any UI-thread path.
    /// </summary>
    /// <remarks>
    /// The plugin waterfall is dispatched through <see cref="Task.Run(Func{Task})"/> before
    /// being waited on. Blocking on it directly deadlocked the app whenever a plugin's
    /// middleware posted back to the Avalonia dispatcher, because the continuation could
    /// never run on the thread already blocked here.
    /// </remarks>
    public byte[] GeneratePdfBytes(PdfDocumentModel model, IProgress<double>? progress)
    {
        var context = new PdfExportPipelineContext(model, progress);

        if (_pluginContext != null)
        {
            var pluginContext = _pluginContext;
            Task.Run(() => pluginContext.ExecuteWaterfallAsync("document:export", context, () =>
            {
                context.ResultPdfBytes = CompileModelToBytes(context.Document, context.Progress);
                return Task.CompletedTask;
            })).GetAwaiter().GetResult();

            return ResolvePipelineResult(context, model, progress);
        }

        return CompileModelToBytes(model, progress);
    }

    /// <summary>
    /// Returns the bytes the export pipeline produced, recompiling only if middleware
    /// short-circuited without producing any.
    /// </summary>
    private static byte[] ResolvePipelineResult(
        PdfExportPipelineContext context, PdfDocumentModel model, IProgress<double>? progress)
    {
        if (context.ResultPdfBytes != null)
        {
            return context.ResultPdfBytes;
        }

        // Middleware suppressed the terminal step without supplying a document. Recompiling
        // silently discards that decision and does all the work twice, so say so.
        AppLogService.Instance.Log(AppLogLevel.Warning, "PdfExport",
            "The 'document:export' pipeline completed without producing bytes; recompiling the " +
            "document directly. A plugin short-circuited the pipeline without setting ResultPdfBytes.");

        return CompileModelToBytes(model, progress);
    }

    private static byte[] CompileModelToBytes(PdfDocumentModel model, IProgress<double>? progress)
    {
        var document = new QuestPdfDocumentWrapper(model, progress);
        byte[] rawPdf = document.GeneratePdf();

        // If scrub metadata is requested, do not embed internal model
        if (model.SecuritySettings.ScrubMetadataOnExport)
        {
            return rawPdf;
        }

        // Embed full lossless FryPDF model metadata for 100% perfect roundtrip re-editing
        return FryPdfEmbeddingHelper.EmbedModelInPdfBytes(rawPdf, model);
    }

    public async Task<byte[]> ExportToBytesAsync(PdfDocumentModel model, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var context = new PdfExportPipelineContext(model, progress, ct);

        if (_pluginContext != null)
        {
            await _pluginContext.ExecuteWaterfallAsync("document:export", context, () =>
            {
                context.ResultPdfBytes = CompileModelToBytes(context.Document, context.Progress);
                return Task.CompletedTask;
            });

            return ResolvePipelineResult(context, model, progress);
        }

        return await Task.Run(() => CompileModelToBytes(model, progress), ct);
    }

    public async Task ExportToFileAsync(PdfDocumentModel model, string filePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        byte[] bytes = await ExportToBytesAsync(model, progress, ct);

        string tempPath = filePath + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, ct);

            if (File.Exists(filePath))
            {
                File.Replace(tempPath, filePath, null);
            }
            else
            {
                File.Move(tempPath, filePath);
            }
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }
}

internal class QuestPdfDocumentWrapper : IDocument
{
    private readonly PdfDocumentModel _model;
    private readonly IProgress<double>? _progress;

    public QuestPdfDocumentWrapper(PdfDocumentModel model, IProgress<double>? progress = null)
    {
        _model = model;
        _progress = progress;
    }

    public DocumentMetadata GetMetadata()
    {
        if (_model.SecuritySettings.ScrubMetadataOnExport)
        {
            return new DocumentMetadata
            {
                Title = "Document",
                Author = "Anonymous",
                Subject = "",
                CreationDate = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ModifiedDate = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Creator = "FryPDF",
                Producer = "codefrydev.in"
            };
        }

        return new DocumentMetadata
        {
            Title = _model.Title,
            Author = _model.Author,
            Subject = _model.Subject,
            Keywords = _model.Keywords,
            CreationDate = _model.CreatedDate,
            ModifiedDate = _model.ModifiedDate,
            Creator = string.IsNullOrWhiteSpace(_model.Creator) ? "FryPDF" : _model.Creator,
            Producer = string.IsNullOrWhiteSpace(_model.Producer) ? "codefrydev.in" : _model.Producer
        };
    }

    public void Compose(IDocumentContainer container)
    {
        int totalPages = Math.Max(1, _model.Pages.Count);
        int pageIndex = 0;
        foreach (var pageModel in _model.Pages)
        {
            container.Page(page =>
            {
                float pageW = (float)pageModel.Width;
                float pageH = (float)pageModel.Height;

                if (pageW <= 0) pageW = 595.28f; // Standard A4 points
                if (pageH <= 0) pageH = 841.89f;

                page.Size(new PageSize(pageW, pageH));
                page.Margin(0);
                page.PageColor(pageModel.BackgroundColorHex);
                page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(10).FontColor("#201F1E"));

                // Absolute Layout Content via Layers
                page.Content().Layers(layers =>
                {
                    // Primary Base Layer
                    layers.PrimaryLayer().Element(c => c.Width(pageW).Height(pageH));

                    // Header Zone (if enabled)
                    if (pageModel.ShowHeaderFooter && (!string.IsNullOrEmpty(pageModel.HeaderLeft) || !string.IsNullOrEmpty(pageModel.HeaderCenter) || !string.IsNullOrEmpty(pageModel.HeaderRight)))
                    {
                        layers.Layer().PaddingLeft(36).PaddingTop(18).Width(pageW - 72).Height(24).Row(row =>
                        {
                            if (!string.IsNullOrEmpty(pageModel.HeaderLeft))
                                row.RelativeItem().Text(pageModel.HeaderLeft).FontSize(8).FontColor(Colors.Grey.Medium);
                            if (!string.IsNullOrEmpty(pageModel.HeaderCenter))
                                row.RelativeItem().AlignCenter().Text(pageModel.HeaderCenter).FontSize(8).FontColor(Colors.Grey.Medium);
                            if (!string.IsNullOrEmpty(pageModel.HeaderRight))
                                row.RelativeItem().AlignRight().Text(pageModel.HeaderRight).FontSize(8).FontColor(Colors.Grey.Medium);
                        });
                    }

                    // Watermark (if present)
                    if (pageModel.Watermark != null && !string.IsNullOrWhiteSpace(pageModel.Watermark.Text))
                    {
                        layers.Layer().AlignCenter().AlignMiddle().Rotate((float)pageModel.Watermark.Angle)
                            .Text(pageModel.Watermark.Text)
                            .FontSize((float)pageModel.Watermark.FontSize)
                            .FontColor(pageModel.Watermark.ColorHex)
                            .Bold();
                    }

                    // Elements ordered by ZIndex
                    var sortedElements = pageModel.Elements
                        .OrderBy(e => e.ZIndex)
                        .ThenBy(e => e.Y)
                        .ThenBy(e => e.X)
                        .ToList();

                    var redactionBoxes = pageModel.Elements.OfType<PdfRedactionElement>().ToList();

                    foreach (var element in sortedElements)
                    {
                        if (_model.SecuritySettings.RemoveCommentsOnExport && element is PdfStickyNoteElement)
                        {
                            continue;
                        }

                        float elX = (float)element.X;
                        float elY = (float)element.Y;
                        float elW = (float)Math.Max(1, element.Width);
                        float elH = (float)Math.Max(1, element.Height);

                        if (element is PdfTextElement textEl)
                        {
                            var reqDims = TextLayoutEngine.CalculateRequiredDimensions(textEl);
                            if (reqDims.Height > elH)
                            {
                                elH = (float)Math.Ceiling(reqDims.Height + 2);
                            }
                        }

                        // Permanent Redaction Sanitization: Strip underlying text/images completely covered by a redaction box
                        if (element is PdfTextElement or PdfImageElement)
                        {
                            bool isFullyCovered = redactionBoxes.Any(r =>
                                r.X <= elX + 1 &&
                                r.Y <= elY + 1 &&
                                (r.X + r.Width) >= (elX + elW - 1) &&
                                (r.Y + r.Height) >= (elY + elH - 1));

                            if (isFullyCovered)
                            {
                                continue;
                            }
                        }

                        layers.Layer().Element(layerContainer =>
                        {
                            var c = layerContainer
                                .PaddingLeft(elX)
                                .PaddingTop(elY)
                                .Width(elW);

                            if (element.Rotation != 0)
                            {
                                c = c.Height(elH).Rotate((float)element.Rotation);
                            }
                            else
                            {
                                c = c.MinHeight(elH);
                            }

                            ComposeElement(c, element);
                        });
                    }

                    // Footer Zone (if enabled)
                    if (pageModel.ShowHeaderFooter)
                    {
                        layers.Layer().PaddingLeft(36).PaddingTop(pageH - 32).Width(pageW - 72).Height(24).Row(row =>
                        {
                            row.RelativeItem().Text(pageModel.FooterLeft ?? "CONFIDENTIAL & PROPRIETARY").FontSize(8).FontColor(Colors.Grey.Medium);
                            if (!string.IsNullOrEmpty(pageModel.FooterCenter))
                            {
                                row.RelativeItem().AlignCenter().Text(pageModel.FooterCenter).FontSize(8).FontColor(Colors.Grey.Medium);
                            }
                            row.RelativeItem().AlignRight().Text(text =>
                            {
                                text.DefaultTextStyle(x => x.FontSize(8).FontColor(Colors.Grey.Medium));
                                text.Span("Page ");
                                text.CurrentPageNumber();
                                text.Span(" of ");
                                text.TotalPages();
                            });
                        });
                    }
                });
            });

            pageIndex++;
            _progress?.Report(Math.Min(100.0, pageIndex * 100.0 / totalPages));
        }
    }

    private void ComposeElement(IContainer container, PdfElementBase element)
    {
        switch (element)
        {
            case PdfTextElement textEl:
                ComposeText(container, textEl);
                break;

            case PdfShapeElement shapeEl:
                ComposeShape(container, shapeEl);
                break;

            case PdfDividerElement divEl:
                ComposeDivider(container, divEl);
                break;

            case PdfTableElement tableEl:
                ComposeTable(container, tableEl);
                break;

            case PdfChartElement chartEl:
                ComposeChart(container, chartEl);
                break;

            case PdfImageElement imgEl:
                ComposeImage(container, imgEl);
                break;

            case PdfQrCodeElement qrEl:
                ComposeQrCode(container, qrEl);
                break;

            case PdfBarcodeElement barEl:
                ComposeBarcode(container, barEl);
                break;

            case PdfFormFieldElement formEl:
                ComposeFormField(container, formEl);
                break;

            case PdfRedactionElement redEl:
                container.Background(redEl.FillColorHex).Border((float)redEl.BorderThickness).BorderColor(redEl.BorderColorHex).Padding(4).AlignCenter().AlignMiddle()
                    .Text(redEl.ShowOverlayText ? redEl.ExemptionCode : "").FontSize(8.5f).Bold().FontColor(redEl.TextColorHex);
                break;

            case PdfInkElement inkEl:
                ComposeInk(container, inkEl);
                break;

            case PdfStickyNoteElement noteEl:
                ComposeStickyNote(container, noteEl);
                break;

            case PdfMeasurementElement measEl:
                container.PaddingVertical(2).Row(mRow =>
                {
                    mRow.AutoItem().Text("|").FontSize(9).Bold().FontColor(measEl.StrokeColorHex);
                    mRow.RelativeItem().BorderBottom((float)measEl.StrokeThickness).BorderColor(measEl.StrokeColorHex).PaddingBottom(2).AlignCenter().Text(measEl.GetFormattedDistance()).FontSize((float)measEl.FontSize).Bold().FontColor(measEl.StrokeColorHex);
                    mRow.AutoItem().Text("|").FontSize(9).Bold().FontColor(measEl.StrokeColorHex);
                });
                break;

            case PdfSvgElement svgEl:
                ComposeSvg(container, svgEl);
                break;

            case PdfMathElement mathEl:
                ComposeMath(container, mathEl);
                break;
        }
    }

    private void ComposeText(IContainer container, PdfTextElement textEl)
    {
        // For curved/circular typography, outline strokes, drop shadows, double underlines, custom scaling/flipping or baseline shifts,
        // render with high-precision vector SVG so all transformations and glyph geometries remain 100% scalable in the PDF.
        bool requiresVectorSvg = textEl.ShapeMode != TextShapeMode.Normal ||
                                 textEl.HasStroke ||
                                 textEl.HasShadow ||
                                 textEl.IsDoubleUnderline ||
                                 Math.Abs(textEl.ScaleX - 1.0) > 0.01 ||
                                 Math.Abs(textEl.ScaleY - 1.0) > 0.01 ||
                                 textEl.FlipX ||
                                 textEl.FlipY ||
                                 Math.Abs(textEl.BaselineShift) > 0.01 ||
                                 Math.Abs(textEl.CharacterRotation) > 0.01 ||
                                 Math.Abs(textEl.WordSpacing) > 0.01 ||
                                 Math.Abs(textEl.ParagraphSpacing) > 0.01 ||
                                 textEl.VerticalAlignment != TextVerticalAlignment.Top;

        if (requiresVectorSvg)
        {
            try
            {
                string svgMarkup = TextLayoutEngine.GenerateSvgMarkup(textEl);
                container.Svg(svgMarkup).FitArea();
                return;
            }
            catch
            {
                // Fallback to standard text composition on any unexpected error
            }
        }

        var target = container;

        if (textEl.CornerRadius > 0)
        {
            target = target.CornerRadius((float)textEl.CornerRadius);
        }

        if (textEl.BorderThickness > 0 && textEl.BorderColorHex != "#00000000" && !textEl.BorderColorHex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
        {
            target = target.Border((float)textEl.BorderThickness).BorderColor(textEl.BorderColorHex);
        }

        if (textEl.BackgroundColorHex != "#00000000" && !textEl.BackgroundColorHex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
        {
            target = target.Background(textEl.BackgroundColorHex);
        }

        target.Padding((float)textEl.Padding).Text(text =>
        {
            if (textEl.Alignment == TextAlignmentMode.Center) text.AlignCenter();
            else if (textEl.Alignment == TextAlignmentMode.Right) text.AlignRight();
            else if (textEl.Alignment == TextAlignmentMode.Justify) text.Justify();

            if (textEl.Spans != null && textEl.Spans.Count > 0)
            {
                foreach (var s in textEl.Spans)
                {
                    if (string.IsNullOrEmpty(s.Text)) continue;

                    var span = !string.IsNullOrEmpty(s.LinkUrl)
                        ? text.Hyperlink(s.Text, s.LinkUrl)
                        : text.Span(s.Text);

                    span.FontFamily(s.FontFamily ?? textEl.FontFamily ?? "Arial")
                        .FontSize((float)(s.FontSize ?? textEl.FontSize))
                        .FontColor(s.TextColorHex ?? textEl.TextColorHex);

                    if (textEl.LineHeight > 0.1) span.LineHeight((float)textEl.LineHeight);
                    if (s.IsBold ?? textEl.IsBold) span.Bold();
                    if (s.IsItalic ?? textEl.IsItalic) span.Italic();
                    if (s.IsUnderline ?? textEl.IsUnderline) span.Underline();
                    if (s.IsStrikethrough ?? textEl.IsStrikethrough) span.Strikethrough();
                    if (s.Script == TextScriptMode.Superscript) span.Superscript();
                    if (s.Script == TextScriptMode.Subscript) span.Subscript();
                }
            }
            else
            {
                var span = text.Span(textEl.Text ?? "")
                    .FontFamily(textEl.FontFamily ?? "Arial")
                    .FontSize((float)textEl.FontSize)
                    .FontColor(textEl.TextColorHex);

                if (textEl.LineHeight > 0.1) span.LineHeight((float)textEl.LineHeight);
                if (textEl.IsBold) span.Bold();
                if (textEl.IsItalic) span.Italic();
                if (textEl.IsUnderline) span.Underline();
                if (textEl.IsStrikethrough) span.Strikethrough();
            }
        });
    }

    private void ComposeShape(IContainer container, PdfShapeElement shapeEl)
    {
        if (shapeEl.ShapeType == ShapeType.Rectangle && shapeEl.CornerRadius <= 0 && string.IsNullOrEmpty(shapeEl.CustomPathData))
        {
            var target = container;

            if (!string.IsNullOrEmpty(shapeEl.FillColorHex) && shapeEl.FillColorHex != "#00000000" && !shapeEl.FillColorHex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
            {
                target = target.Background(shapeEl.FillColorHex);
            }

            if (shapeEl.StrokeThickness > 0 && !string.IsNullOrEmpty(shapeEl.StrokeColorHex) && shapeEl.StrokeColorHex != "#00000000" && !shapeEl.StrokeColorHex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
            {
                target = target.Border((float)shapeEl.StrokeThickness).BorderColor(shapeEl.StrokeColorHex);
            }

            if (!string.IsNullOrEmpty(shapeEl.Label))
            {
                target.Padding(4).AlignCenter().AlignMiddle().Text(shapeEl.Label)
                    .FontSize((float)shapeEl.LabelFontSize)
                    .FontColor(shapeEl.LabelColorHex ?? "#201F1E")
                    .Bold();
            }
            return;
        }

        try
        {
            string svgMarkup = SvgShapeHelper.GenerateSvgMarkup(shapeEl);
            container.Svg(svgMarkup);
        }
        catch
        {
            var target = container;

            if (shapeEl.ShapeType == ShapeType.Circle)
            {
                target = target.CornerRadius((float)Math.Max(shapeEl.Width, shapeEl.Height) / 2);
            }
            else if (shapeEl.CornerRadius > 0)
            {
                target = target.CornerRadius((float)shapeEl.CornerRadius);
            }

            if (!string.IsNullOrEmpty(shapeEl.FillColorHex) && shapeEl.FillColorHex != "#00000000" && !shapeEl.FillColorHex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
            {
                target = target.Background(shapeEl.FillColorHex);
            }

            if (shapeEl.StrokeThickness > 0 && !string.IsNullOrEmpty(shapeEl.StrokeColorHex) && shapeEl.StrokeColorHex != "#00000000" && !shapeEl.StrokeColorHex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
            {
                target = target.Border((float)shapeEl.StrokeThickness).BorderColor(shapeEl.StrokeColorHex);
            }

            if (!string.IsNullOrEmpty(shapeEl.Label))
            {
                target.Padding(4).AlignCenter().AlignMiddle().Text(shapeEl.Label)
                    .FontSize((float)shapeEl.LabelFontSize)
                    .FontColor(shapeEl.LabelColorHex ?? "#201F1E")
                    .Bold();
            }
        }
    }

    private void ComposeDivider(IContainer container, PdfDividerElement divEl)
    {
        if (divEl.Style == DividerStyle.Straight && divEl.DashStyle == LineDashStyle.Solid)
        {
            if (divEl.IsVertical)
            {
                container.AlignCenter().LineVertical((float)divEl.Thickness).LineColor(divEl.ColorHex);
            }
            else
            {
                container.AlignMiddle().LineHorizontal((float)divEl.Thickness).LineColor(divEl.ColorHex);
            }
            return;
        }

        try
        {
            string svgMarkup = SvgShapeHelper.GenerateDividerSvgMarkup(divEl);
            container.Svg(svgMarkup).FitArea();
        }
        catch
        {
            if (divEl.IsVertical)
                container.AlignCenter().LineVertical((float)divEl.Thickness).LineColor(divEl.ColorHex);
            else
                container.AlignMiddle().LineHorizontal((float)divEl.Thickness).LineColor(divEl.ColorHex);
        }
    }

    private void ComposeInk(IContainer container, PdfInkElement inkEl)
    {
        try
        {
            string svgMarkup = SvgShapeHelper.GenerateInkSvgMarkup(inkEl);
            container.Svg(svgMarkup).FitArea();
        }
        catch
        {
            container.Background(inkEl.StrokeColorHex);
        }
    }

    private void ComposeTable(IContainer container, PdfTableElement tableEl)
    {
        container.Border(1).BorderColor(tableEl.BorderColorHex).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                for (int i = 0; i < Math.Max(1, tableEl.Headers.Count); i++)
                {
                    columns.RelativeColumn();
                }
            });

            // Headers
            if (tableEl.Headers.Count > 0)
            {
                table.Header(header =>
                {
                    foreach (var h in tableEl.Headers)
                    {
                        header.Cell().Background(tableEl.HeaderBackgroundHex).Padding(5).Text(h)
                            .FontColor(tableEl.HeaderTextHex)
                            .Bold()
                            .FontSize(8.5f);
                    }
                });
            }

            // Rows
            int rowIndex = 0;
            foreach (var row in tableEl.Rows)
            {
                var bg = (rowIndex % 2 == 1) ? tableEl.AlternateRowBackgroundHex : "#FFFFFF";
                foreach (var cellText in row)
                {
                    table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(tableEl.BorderColorHex).Padding(4).Text(cellText)
                        .FontSize(8);
                }
                rowIndex++;
            }
        });
    }

    private void ComposeChart(IContainer container, PdfChartElement chartEl)
    {
        try
        {
            byte[] chartPng = LiveChartsRenderer.RenderChartToPngBytes(
                chartEl, 
                Math.Max(180, (int)chartEl.Width), 
                Math.Max(120, (int)chartEl.Height), 
                2.5f);

            if (chartPng.Length > 0)
            {
                container.Border(1).BorderColor(chartEl.BorderColorHex).Background(chartEl.BackgroundColorHex).Padding(4).Image(chartPng).FitArea();
                return;
            }
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogWarning("PdfExport",
                $"Chart '{chartEl.Title}' could not be rendered; exporting a title block instead", ex);
        }

        container.Border(1).BorderColor(chartEl.BorderColorHex).Background(chartEl.BackgroundColorHex).Padding(8).Column(chartCol =>
        {
            chartCol.Item().AlignCenter().Text($"{chartEl.Title}").FontSize(9f).Bold().FontColor(Colors.Grey.Darken3);
        });
    }

    /// <summary>
    /// Draws an image element, falling back to a labelled placeholder box.
    /// </summary>
    /// <remarks>
    /// Each source attempt used to swallow its exception outright, so a logo that failed to
    /// decode silently became a grey box in every exported PDF with no error anywhere. The
    /// fallback behaviour is unchanged — the failures are now recorded.
    /// </remarks>
    private void ComposeImage(IContainer container, PdfImageElement imgEl)
    {
        string label = imgEl.AltText ?? imgEl.ImagePath ?? "image";

        if (imgEl.ImageData != null && imgEl.ImageData.Length > 0)
        {
            try
            {
                container.Image(imgEl.ImageData).FitArea();
                return;
            }
            catch (Exception ex)
            {
                AppLogService.Instance.LogWarning("PdfExport",
                    $"Embedded image bytes for '{label}' could not be drawn", ex);
            }
        }

        if (!string.IsNullOrEmpty(imgEl.Base64Data))
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(imgEl.Base64Data);
                container.Image(bytes).FitArea();
                return;
            }
            catch (Exception ex)
            {
                AppLogService.Instance.LogWarning("PdfExport",
                    $"Base64 image data for '{label}' could not be decoded", ex);
            }
        }

        if (!string.IsNullOrEmpty(imgEl.ImagePath) && File.Exists(imgEl.ImagePath))
        {
            try
            {
                if (imgEl.ImagePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    string svg = File.ReadAllText(imgEl.ImagePath);
                    container.Svg(svg).FitArea();
                    return;
                }

                container.Image(imgEl.ImagePath).FitArea();
                return;
            }
            catch (Exception ex)
            {
                AppLogService.Instance.LogWarning("PdfExport",
                    $"Image file '{imgEl.ImagePath}' could not be drawn", ex);
            }
        }

        AppLogService.Instance.Log(AppLogLevel.Warning, "PdfExport",
            $"No usable image source for '{label}'; exporting a placeholder box instead.");

        container.Border(1).BorderColor(imgEl.BorderColorHex).Background("#F3F2F1").AlignCenter().AlignMiddle()
            .Text(imgEl.AltText ?? "Image").FontSize(10).FontColor(Colors.Grey.Medium);
    }

    private void ComposeSvg(IContainer container, PdfSvgElement svgEl)
    {
        try
        {
            string svgData = !string.IsNullOrWhiteSpace(svgEl.SvgSource)
                ? svgEl.SvgSource
                : (!string.IsNullOrWhiteSpace(svgEl.FilePath) && File.Exists(svgEl.FilePath)
                    ? File.ReadAllText(svgEl.FilePath)
                    : SvgOrnamentLibrary.GetGaneshaCrestSvg());

            if (!string.IsNullOrWhiteSpace(svgEl.TintColorHex))
            {
                svgData = svgData.Replace("currentColor", svgEl.TintColorHex);
            }

            container.Svg(svgData).FitArea();
        }
        catch
        {
            container.Border(1).BorderColor(svgEl.BorderColorHex ?? "#CBD5E1").Background("#F8FAFC").AlignCenter().AlignMiddle()
                .Text("SVG Vector Art").FontSize(9).FontColor(Colors.Grey.Medium);
        }
    }

    private void ComposeMath(IContainer container, PdfMathElement mathEl)
    {
        try
        {
            var target = container;

            if (mathEl.CornerRadius > 0)
            {
                target = target.CornerRadius((float)mathEl.CornerRadius);
            }

            if (mathEl.ShowBorder && mathEl.BorderThickness > 0 && !string.IsNullOrEmpty(mathEl.BorderColorHex) && !mathEl.BorderColorHex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
            {
                target = target.Border((float)mathEl.BorderThickness).BorderColor(mathEl.BorderColorHex);
            }

            if (mathEl.ShowBackground && !string.IsNullOrEmpty(mathEl.BackgroundColorHex) && !mathEl.BackgroundColorHex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
            {
                target = target.Background(mathEl.BackgroundColorHex);
            }

            if (mathEl.Padding > 0)
            {
                target = target.Padding((float)mathEl.Padding);
            }

            var options = new MathRenderOptions
            {
                FontSize = mathEl.FontSize > 0 ? mathEl.FontSize : 16.0,
                TextColorHex = !string.IsNullOrEmpty(mathEl.TextColorHex) ? mathEl.TextColorHex : "#000000",
                BackgroundColorHex = "#00000000",
                DisplayStyle = mathEl.DisplayStyle,
                Alignment = mathEl.Alignment,
                ShowEquationNumber = mathEl.ShowEquationNumber,
                EquationNumber = mathEl.EquationNumber,
                Padding = 2.0
            };

            var renderResult = MathLayoutEngine.RenderToSvg(mathEl.Formula, options);
            target.Svg(renderResult.SvgMarkup).FitArea();
        }
        catch
        {
            container.Border(1).BorderColor("#CBD5E1").Background("#F8FAFC").AlignCenter().AlignMiddle()
                .Text(mathEl.Formula ?? "f(x)").FontSize((float)mathEl.FontSize).FontColor(mathEl.TextColorHex ?? "#000000");
        }
    }

    private void ComposeQrCode(IContainer container, PdfQrCodeElement qrEl)
    {
        try
        {
            byte[] qrBytes = QrCodeHelper.GeneratePngBytes(
                qrEl.Content,
                qrEl.DarkColorHex,
                qrEl.LightColorHex,
                qrEl.EccLevel,
                pixelsPerModule: 20,
                drawQuietZones: qrEl.DrawQuietZones);

            string bgHex = !string.IsNullOrWhiteSpace(qrEl.LightColorHex) ? qrEl.LightColorHex : "#FFFFFF";

            container.Border(1).BorderColor(Colors.Grey.Lighten2).Background(bgHex).Padding(4).Column(qCol =>
            {
                qCol.Item().AlignCenter().Image(qrBytes).FitArea();
                if (!string.IsNullOrEmpty(qrEl.Label))
                {
                    qCol.Item().PaddingTop(2).AlignCenter().Text(qrEl.Label).FontSize(7f).Bold().FontColor(Colors.Grey.Darken2);
                }
            });
        }
        catch
        {
            container.Border(1).BorderColor(Colors.Grey.Lighten2).Background(Colors.White).AlignCenter().AlignMiddle()
                .Text("QR CODE").FontSize(8).Bold();
        }
    }

    private void ComposeBarcode(IContainer container, PdfBarcodeElement barEl)
    {
        container.Border(1).BorderColor(Colors.Grey.Lighten2).Background(Colors.White).Padding(4).Column(bCol =>
        {
            // Algorithmic barcode stripes
            bCol.Item().AlignCenter().Height(22).Row(r =>
            {
                string val = barEl.CodeValue ?? "12345678";
                foreach (char c in val)
                {
                    int pattern = (int)c % 5;
                    r.AutoItem().Width(pattern == 0 ? 1 : (pattern == 1 ? 2 : (pattern == 2 ? 1.5f : 3))).Background(barEl.BarColorHex);
                    r.AutoItem().Width(pattern % 2 == 0 ? 1 : 2).Background(Colors.White);
                }
            });

            if (barEl.ShowText && !string.IsNullOrEmpty(barEl.CodeValue))
            {
                bCol.Item().PaddingTop(2).AlignCenter().Text(barEl.CodeValue).FontSize(7.5f).FontColor(Colors.Grey.Darken3);
            }
        });
    }

    private void ComposeFormField(IContainer container, PdfFormFieldElement formEl)
    {
        var target = container;

        // Apply background fill if enabled
        if (formEl.ShowBackground && !string.IsNullOrEmpty(formEl.BackgroundColorHex) && !formEl.BackgroundColorHex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
        {
            target = target.Background(formEl.BackgroundColorHex);
        }

        // Apply border if enabled
        if (formEl.ShowBorder && !string.IsNullOrEmpty(formEl.BorderColorHex) && !formEl.BorderColorHex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
        {
            target = target.Border(1).BorderColor(formEl.BorderColorHex);
        }

        string displayVal = !string.IsNullOrEmpty(formEl.Value) ? formEl.Value : string.Empty;
        string textColor = !string.IsNullOrEmpty(formEl.TextColorHex) ? formEl.TextColorHex : "#1E293B";
        string fontFamily = !string.IsNullOrEmpty(formEl.FontFamily) ? formEl.FontFamily : "Arial";

        // 1. Checkbox Field
        if (formEl.FieldType == FormFieldType.Checkbox)
        {
            target.Padding(2).Row(r =>
            {
                r.AutoItem().Width(12).Height(12).Border(1).BorderColor(Colors.Grey.Darken2)
                    .Background(formEl.IsChecked ? Colors.Blue.Lighten4 : Colors.White)
                    .AlignCenter().AlignMiddle()
                    .Text(formEl.IsChecked ? "✓" : "").FontSize(8).Bold().FontColor(Colors.Blue.Darken3);

                if (formEl.ShowLabel && !string.IsNullOrEmpty(formEl.Label))
                {
                    r.RelativeItem().PaddingLeft(4).AlignMiddle().Text(formEl.Label).FontSize(8).FontColor(Colors.Grey.Darken3);
                }
            });
            return;
        }

        // 2. Radio Button Field
        if (formEl.FieldType == FormFieldType.Radio)
        {
            target.Padding(2).Row(r =>
            {
                r.AutoItem().Width(12).Height(12).CornerRadius(6).Border(1).BorderColor(Colors.Grey.Darken2)
                    .Background(Colors.White)
                    .AlignCenter().AlignMiddle()
                    .Text(formEl.IsChecked ? "●" : "").FontSize(7).Bold().FontColor(Colors.Blue.Darken3);

                if (formEl.ShowLabel && !string.IsNullOrEmpty(formEl.Label))
                {
                    r.RelativeItem().PaddingLeft(4).AlignMiddle().Text(formEl.Label).FontSize(8).FontColor(Colors.Grey.Darken3);
                }
            });
            return;
        }

        // 3. Signature & Signature Line Field
        if (formEl.FieldType == FormFieldType.Signature || formEl.FieldType == FormFieldType.SignatureLine)
        {
            target.PaddingHorizontal(4).PaddingBottom(2).Column(sCol =>
            {
                if (formEl.ShowLabel && !string.IsNullOrEmpty(formEl.Label) && formEl.Height >= 32)
                {
                    sCol.Item().Text(formEl.Label).FontSize(7.5f).Bold().FontColor(Colors.Grey.Darken2);
                }

                sCol.Item().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingBottom(2).AlignMiddle().Text(text =>
                {
                    if (!string.IsNullOrEmpty(formEl.Value))
                    {
                        text.Span(formEl.Value).FontFamily("Georgia").FontSize(9.5f).Italic().FontColor(Colors.Blue.Darken3);
                    }
                    else
                    {
                        text.Span("[Authorized Signature Line]").FontSize(8).Italic().FontColor(Colors.Grey.Medium);
                    }
                });
            });
            return;
        }

        // 4. Push Button Field
        if (formEl.FieldType == FormFieldType.Button)
        {
            target.Background(formEl.BorderColorHex ?? "#0F6CBD").Padding(4).AlignCenter().AlignMiddle()
                .Text(formEl.Label ?? "Button").FontSize(8.5f).Bold().FontColor(Colors.White);
            return;
        }

        // 5. Multiline Text Area
        if (formEl.FieldType == FormFieldType.MultilineText)
        {
            target.Padding(4).Column(mCol =>
            {
                if (formEl.ShowLabel && !string.IsNullOrEmpty(formEl.Label) && formEl.Height >= 45)
                {
                    mCol.Item().Text(formEl.Label).FontSize(7.5f).Bold().FontColor(Colors.Grey.Darken3);
                }

                mCol.Item().Text(displayVal).FontFamily(fontFamily)
                    .FontSize((float)Math.Clamp(formEl.FontSize, 7.5, 11))
                    .FontColor(textColor);
            });
            return;
        }

        // 6. Standard Single-Line Inputs (Text, Number, Dropdown, DatePicker)
        // If container height is spacious (>= 36pt) and label is explicitly requested, render stacked label + input
        if (formEl.ShowLabel && formEl.Height >= 36 && !string.IsNullOrWhiteSpace(formEl.Label))
        {
            target.Padding(3).Column(fCol =>
            {
                fCol.Item().Row(r =>
                {
                    r.RelativeItem().Text(formEl.Label).FontSize(7.5f).Bold().FontColor(Colors.Grey.Darken3);
                    if (formEl.IsRequired)
                    {
                        r.AutoItem().Text("*").FontSize(7.5f).Bold().FontColor(Colors.Red.Medium);
                    }
                });

                float inputFontSize = (float)Math.Clamp(formEl.FontSize > 0 ? formEl.FontSize : 9.5, 7.0, Math.Max(8, formEl.Height - 16));
                var inputContainer = fCol.Item().PaddingTop(1).AlignMiddle();

                inputContainer.Text(text =>
                {
                    var span = text.Span(displayVal)
                        .FontFamily(fontFamily)
                        .FontSize(inputFontSize)
                        .FontColor(textColor);

                    if (formEl.TextAlignment == TextAlignmentMode.Center) text.AlignCenter();
                    else if (formEl.TextAlignment == TextAlignmentMode.Right) text.AlignRight();
                });
            });
            return;
        }

        // Compact Single-Line Input (Height < 36pt or ShowLabel == false):
        // Fills the allocated box directly without stacked label headers, eliminating layout overflow completely!
        float compactFontSize = (float)Math.Clamp(formEl.FontSize > 0 ? formEl.FontSize : (formEl.Height * 0.65), 7.0, Math.Max(8, formEl.Height - 4));
        target.PaddingHorizontal(4).AlignMiddle().Text(text =>
        {
            text.Span(displayVal)
                .FontFamily(fontFamily)
                .FontSize(compactFontSize)
                .FontColor(textColor);

            if (formEl.TextAlignment == TextAlignmentMode.Center) text.AlignCenter();
            else if (formEl.TextAlignment == TextAlignmentMode.Right) text.AlignRight();
        });
    }

    private void ComposeStickyNote(IContainer container, PdfStickyNoteElement noteEl)
    {
        container.Border(1).BorderColor(noteEl.BorderColorHex).Background(noteEl.ColorHex).Padding(6).Column(nCol =>
        {
            nCol.Item().Row(r =>
            {
                r.RelativeItem().Text($"📌 {noteEl.Author}").FontSize(8f).Bold().FontColor("#78350F");
                r.AutoItem().Text(noteEl.Timestamp).FontSize(7f).FontColor("#92400E");
            });
            nCol.Item().PaddingTop(2).Text(noteEl.NoteText).FontSize(7.5f).FontColor("#78350F");
            nCol.Item().PaddingTop(2).Text($"Status: {noteEl.Status}").FontSize(7f).Bold().FontColor("#B45309");
        });
    }
}
