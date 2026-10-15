# FryPdf.PluginSdk

The official, cross-platform Plugin SDK and core document processing engine for **FryPDF** — a high-performance, privacy-first PDF creation and editing studio built with .NET 10, SkiaSharp, UglyToad.PdfPig, and QuestPDF.

[![NuGet](https://img.shields.io/nuget/v/FryPdf.PluginSdk.svg)](https://www.nuget.org/packages/FryPdf.PluginSdk)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/codefrydev/frypdf/blob/main/LICENSE)

---

## What is FryPdf.PluginSdk?

`FryPdf.PluginSdk` provides all contracts, extension descriptors, typed dispatch pipelines, and domain models required to develop third-party plugins for FryPDF. 

Plugins run in isolated, collectible `AssemblyLoadContext` sandboxes and can extend every major application pillar without modifying host code:
- **PDF Tools**: Custom document operations (watermarking, redaction, converters, merge/split)
- **Ribbon Actions & Tabs**: Dynamic ribbon contributions and button capsules
- **Contextual Sidebars & Overlays**: Document telemetry, metadata inspectors, interactive panels
- **Custom Canvas Elements**: Custom visual and interactive elements
- **AI & OCR Engines**: Custom model connectors and text extractors
- **Document Importers & Exporters**: Custom format ingestion and rendering

---

## Building new plugins? Use FryWorld.Sdk

New plugins should be built on [FryWorld.Sdk](https://www.nuget.org/packages/FryWorld.Sdk), the plugin SDK shared by every Fry app. FryPDF hosts FryWorld.Sdk plugins next to its own, so one plugin runs in FryWorld, in FryPDF and as a standalone app. FryPDF shows its pages, overlays, commands, sidebars, inspectors, dialogs, shortcuts and ribbon tabs. Install it by dropping its `.fryplugin` into FryPDF's `fryworld-plugins` folder (beside `plugins`).

To also use FryPDF's PDF features, reference this package as well and use the scoped extensions in `PdfEditorApp.Core.Plugins.FryWorldInterop`:

```csharp
using PdfEditorApp.Core.Plugins.FryWorldInterop;

public override Task ApplyAsync(IFryPluginContext ctx, CancellationToken ct = default)  // FryWorld.Sdk context
{
    if (ctx.IsFryPdfHost())
    {
        ctx.RegisterDocumentImporter(new MarkdownImporter());   // removed again when the plugin unmounts
        ctx.RegisterTemplate(new InvoiceTemplate());
    }
    return Task.CompletedTask;
}
```

The extensions cover importers, exporters, templates, OCR engines, data connectors and canvas elements. This package's build targets keep `PdfEditorApp.Core.dll` out of the plugin's `.fryplugin`, because FryPDF provides it.

Plugins built on this SDK's own `IFryPlugin` keep working unchanged.

---

## Installation

Add the SDK package to your plugin project via NuGet:

```bash
dotnet add package FryPdf.PluginSdk
```

Or reference it in your `.csproj`:

```xml
<ItemGroup>
  <!-- Host contracts are marked compile-only so they are not bundled into the plugin archive -->
  <PackageReference Include="FryPdf.PluginSdk" Version="1.0.0" PrivateAssets="all" />
</ItemGroup>
```

> **Important**: Host assemblies are provided by the running FryPDF application at runtime. Always set `PrivateAssets="all"` on `FryPdf.PluginSdk` in your plugin project to prevent shipping duplicate assemblies inside your `.fryplugin` bundle.

---

## Quickstart: Creating a Plugin

### 1. Implement `IFryPlugin`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using PdfEditorApp.Core.Plugins;
using PdfEditorApp.Core.Plugins.Descriptors;

namespace MyAwesomePlugin;

public class MyWatermarkPlugin : IFryPlugin
{
    public string Id => "com.example.watermark";
    public string Name => "Quick Watermark";
    public Version Version => new(1, 0, 0);

    public Task ApplyAsync(IFryPluginContext ctx, CancellationToken ct = default)
    {
        // Register a tool descriptor
        ctx.RegisterTool(new PdfToolDescriptor
        {
            Id = Id,
            Name = Name,
            Description = "Applies a custom watermark to every page",
            Category = "OptimizeAndSecurity",
            IconKind = "Watermark",
            IconColorHex = "#0284C7"
        });

        // Register reversible teardown effects
        ctx.RegisterEffect(() =>
        {
            // Cleanup on plugin unload
        });

        return Task.CompletedTask;
    }
}
```

### 2. Define `plugin.json` Manifest

Add `plugin.json` in your plugin project root:

```json
{
  "id": "com.example.watermark",
  "name": "Quick Watermark",
  "version": "1.0.0",
  "author": "Acme Tools",
  "description": "Applies custom watermarks across all document pages.",
  "category": "Tools",
  "entryPoint": "MyAwesomePlugin.dll"
}
```

### 3. Package as `.fryplugin`

Plugins are packaged as `.fryplugin` zip archives containing your compiled plugin DLL, dependencies, and `plugin.json`. Users can install them directly into FryPDF via drag-and-drop or the built-in Plugin Manager.

---

## Architecture Principles

1. **Inverted Capability Context (`IFryPluginContext`)**: Plugins register and consume capabilities via a thread-safe context with zero static coupling.
2. **Reversible Effects (`PluginScope`)**: All side effects registered via `ctx.RegisterEffect` are unwound in reverse order on plugin unload to eliminate memory leaks.
3. **Directed Acyclic Graph (DAG) Solver**: Dependencies declared via `RequiredServices` are topologically sorted and mounted deterministically.
4. **Typed Dispatch Pipelines**: Composable middleware pipelines (`Waterfall`, `Bail`, `Parallel`, `Serial`, `Emit`) for document processing.

---

## Resources & Documentation

- **Project Repository**: [GitHub (codefrydev/frypdf)](https://github.com/codefrydev/frypdf)
- **Plugin Development Guide**: [docs/EXTERNAL_PLUGIN_DEVELOPMENT_GUIDE.md](https://github.com/codefrydev/frypdf/blob/main/docs/EXTERNAL_PLUGIN_DEVELOPMENT_GUIDE.md)
- **Official Website**: [https://codefrydev.in/frypdf/](https://codefrydev.in/frypdf/)
- **License**: MIT
