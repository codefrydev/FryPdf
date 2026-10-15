using System;
using PdfEditorApp.Core.Models;
using PdfEditorApp.Core.Plugins.Descriptors;
using FwContext = FryWorld.Sdk.Plugins.IFryPluginContext;

namespace PdfEditorApp.Core.Plugins.FryWorldInterop;

/// <summary>
/// FryPDF's PDF features for plugins built on FryWorld.Sdk. When FryPDF hosts a FryWorld plugin, these register
/// importers, exporters, templates, OCR engines, data connectors and canvas elements with FryPDF, tied to the
/// plugin's scope so they are removed when it unmounts.
/// </summary>
/// <remarks>
/// They need FryPDF: in another host (FryWorld itself, the standalone runner) the registries do not exist, so
/// these throw <see cref="InvalidOperationException"/>. Check <see cref="IsFryPdfHost"/> first when a plugin also
/// runs elsewhere.
/// </remarks>
/// <example>
/// <code>
/// public override Task ApplyAsync(IFryPluginContext ctx, CancellationToken ct = default)
/// {
///     if (ctx.IsFryPdfHost())
///     {
///         ctx.RegisterDocumentImporter(new MarkdownImporter());
///         ctx.RegisterTemplate(new InvoiceTemplate());
///     }
///     return Task.CompletedTask;
/// }
/// </code>
/// </example>
public static class FryWorldPdfExtensions
{
    /// <summary>Whether the plugin is running inside FryPDF, so the PDF registries are available.</summary>
    public static bool IsFryPdfHost(this FwContext ctx) => ctx.HasService(typeof(IDocumentImporterRegistry));

    /// <summary>Adds a document importer (e.g. a new file format FryPDF can open).</summary>
    public static IDisposable RegisterDocumentImporter(this FwContext ctx, IDocumentImporter importer) =>
        ctx.RegisterDisposable(Registry<IDocumentImporterRegistry>(ctx).RegisterImporter(importer));

    /// <summary>Adds a document exporter (e.g. a new format FryPDF can save to).</summary>
    public static IDisposable RegisterDocumentExporter(this FwContext ctx, IDocumentExporter exporter) =>
        ctx.RegisterDisposable(Registry<IDocumentExporterRegistry>(ctx).RegisterExporter(exporter));

    /// <summary>Adds a document template to FryPDF's template gallery.</summary>
    public static IDisposable RegisterTemplate(this FwContext ctx, ITemplateDescriptor template) =>
        ctx.RegisterDisposable(Registry<ITemplateRegistry>(ctx).RegisterTemplate(template));

    /// <summary>Adds an OCR engine.</summary>
    public static IDisposable RegisterOcrEngine(this FwContext ctx, IOcrEngine engine) =>
        ctx.RegisterDisposable(Registry<IOcrEngineRegistry>(ctx).RegisterEngine(engine));

    /// <summary>Adds a data connector for data-bound documents.</summary>
    public static IDisposable RegisterDataConnector(this FwContext ctx, IDataConnector connector) =>
        ctx.RegisterDisposable(Registry<IDataConnectorRegistry>(ctx).RegisterConnector(connector));

    /// <summary>Adds a canvas element type to FryPDF's editor.</summary>
    public static IDisposable RegisterCanvasElement(this FwContext ctx, CanvasElementDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var registry = Registry<ICanvasElementRegistry>(ctx);
        registry.RegisterElement(descriptor);
        return ctx.RegisterEffect(() => registry.UnregisterElement(descriptor.ElementTypeId));
    }

    private static TRegistry Registry<TRegistry>(FwContext ctx) where TRegistry : class
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.TryGetService<TRegistry>(out var registry)
            ? registry
            : throw new InvalidOperationException(
                $"{typeof(TRegistry).Name} is only available when the plugin runs inside FryPDF. Check ctx.IsFryPdfHost() first.");
    }
}
