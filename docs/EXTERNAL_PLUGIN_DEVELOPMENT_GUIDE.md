# Developing External Plugins for FryPDF

> **Authoritative Guide & Developer Manual**  
> Learn how to build, test, package, and distribute third-party plugins for **FryPDF** using **.NET 10, C# 13, and Avalonia UI (Material Design 3 Expressive)**.

> **Building a new external plugin?** Build it on [FryWorld.Sdk](https://www.nuget.org/packages/FryWorld.Sdk) instead. FryPDF hosts FryWorld.Sdk plugins (install them in its `fryworld-plugins` folder), and the same plugin also runs in FryWorld and on its own. FryPDF's former external plugins (Snake, Music Player, Chess, Scratchpad, Image Editor, C# Code Studio, Telemetry HUD, Tic-Tac-Toe) have moved there. This guide still describes FryPDF's own plugin runtime, which FryPDF's built-in features use and which keeps running existing plugins.

---

## 1. Overview & Architectural Foundation

FryPDF is built upon an **"Everything is a Plugin"** modular microkernel architecture inspired by the **Cordis** framework. The core desktop shell privileges no single subsystem: tools, dynamic ribbon actions, editor sidebars, contextual property panels, floating overlays, AI engines, and file converters are all isolated plugins mounted into an active runtime context (`IFryPluginContext`).

### Why Build a FryPDF External Plugin?
- **Zero Monolithic Wiring**: You never need to fork or recompile FryPDF to introduce new features.
- **Isolated Collectible Loading**: Plugins execute inside dedicated `CollectiblePluginLoadContext` (`AssemblyLoadContext`) sandboxes. Plugins can be loaded, enabled, disabled, updated, and completely unloaded from memory without restarting the application.
- **Dynamic M3 Expressive Integration**: Your UI automatically integrates with Google Material Design 3 (M3) Expressive themes, color tokens, and elevation physics.
- **Auto-Generated Settings UI**: Declare typed configuration options in your code or manifest; FryPDF automatically generates an M3 Expressive settings dialog.
- **Single-File Distribution (`.fryplugin`)**: Distribute your plugin as a single `.fryplugin` package (ZIP archive containing `plugin.json`, DLLs, and resources) that users can install via drag-and-drop.

---

## 2. Prerequisites & Environment Setup

Before starting, ensure you have:
- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** (v10.0 or later)
- An IDE with modern C# 13+ support:
  - Visual Studio Code (with C# Dev Kit extension)
  - JetBrains Rider (2025.1+)
  - Visual Studio 2026 (v18+)

### Zero Source Code Requirement
> [!TIP]
> **You do NOT need to clone FryPDF's repository or download its source code to create plugins!**  
> External developers develop plugins independently against either:
> 1. **Their installed FryPDF application** (referencing the compiled `.dll` files from the install directory).
> 2. **The `FryPdf.PluginSdk` NuGet package** (`dotnet add package FryPdf.PluginSdk`).

### Referenced Host Contracts
External plugins compile against the following host contracts (provided by the installed application at runtime):
- **`PdfEditorApp.Core.dll`**: Core domain models, plugin contracts (`IFryPlugin`, `IFryPluginContext`), and extension descriptors.
- **`PdfEditorApp.dll`** *(optional, for UI plugins)*: Avalonia UI views, viewmodels, base classes (`ToolPluginBase`), and services.
- **`CommunityToolkit.Mvvm`**: Standard MVVM source generators (`[ObservableProperty]`, `[RelayCommand]`).
- **`Avalonia`** (12.x): Cross-platform UI controls and styling.

### Contract Versioning (ABI)
Keep every host reference at `<Private>false</Private>` — **never bundle a host assembly into your
package**. Host assemblies are resolved from the running application, not from your plugin folder.
Shipping your own `PdfEditorApp.Core.dll` gives `IFryPlugin` two distinct type identities, and the
host will discover zero plugins in your package without reporting an error.

`PdfEditorApp.Core` carries a **stable `AssemblyVersion` (`1.0.0.0`) that is deliberately
independent of the application's release version**. It is bumped only on a breaking contract
change, so a plugin compiled today keeps loading after FryPDF ships new releases. The DLL's
*file* version still tracks the release it shipped in — use that to identify a build, and the
assembly version to reason about the contract. Nothing in your project should pin the contract
version.

Version drift is not fatal: the host resolves **its own** assemblies by simple name, ignoring the
version your plugin recorded at compile time. That matters most for `PdfEditorApp.dll`, whose
version legitimately moves with every release — a plugin compiled against `PdfEditorApp 1.0.0.0`
still binds to a host shipping `0.0.6.0`.

Released builds are published `--self-contained`, and in that configuration the runtime's own
binder refuses a request for a **higher** version than the app ships, reporting it as

```
Could not load file or assembly 'PdfEditorApp.Core, Version=X'. The system cannot find the file specified.
```

even though the DLL is present next to the executable. FryPDF no longer routes host assemblies
through that binder, so this should not occur. If it does, check **Diagnostic Logs**: an
`Unresolved dependency` line names what the host actually has loaded under that name, where it
was loaded from, and whether the DLL is on disk.

---

## 3. The Anatomy of an External Plugin

Every external plugin consists of:
1. **A .NET 10 Class Library** targeting `net10.0`.
2. **An Implementation of `IFryPlugin`** (or subclass of `ToolPluginBase`).
3. **Reversible Effects & Dynamic Registrations** mounted via `ApplyAsync`.
4. **A `plugin.json` Manifest File** describing metadata, dependencies, and settings.
5. **A `.fryplugin` Archive** bundle for distribution.

### 3.1 The `IFryPlugin` Interface
Every plugin must implement the `IFryPlugin` contract:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PdfEditorApp.Core.Plugins;
using PdfEditorApp.Core.Plugins.Manifests;

namespace MyCompany.FryPdf.Plugins;

public class MyCustomPlugin : IFryPlugin
{
    // Unique identifier (reverse-domain style: <vendor>.<category>.<name>)
    public string Id => "com.mycompany.frypdf.customtool";

    // User-friendly display name
    public string Name => "Custom PDF Tool";

    // Semantic version of the plugin
    public Version Version => new(1, 0, 0);

    // Declares required service contracts that must be registered before mounting
    public IReadOnlyList<Type> RequiredServices => Array.Empty<Type>();

    // Declares service contracts this plugin provides to other plugins
    public IReadOnlyList<Type> ProvidedServices => Array.Empty<Type>();

    // Declarative configuration schema for user-editable settings
    public IReadOnlyDictionary<string, PluginSettingDefinition>? SettingsSchema => null;

    // Called when the plugin is mounted into FryPDF
    public Task ApplyAsync(IFryPluginContext ctx, CancellationToken ct = default)
    {
        // Register tools, ribbon buttons, sidebars, pipelines, or services here
        return Task.CompletedTask;
    }
}
```

---

## 4. The 12 Dynamic Capability Registries

FryPDF provides 12 dynamic extension pillars accessible through `IFryPluginContext`. Never write hardcoded UI wiring—always register through these pillars:

| Pillar | Registry / Context Method | Typical Use Case |
| :--- | :--- | :--- |
| **1. PDF Tools** | `ctx.RegisterTool(PdfToolDescriptor)` | Standalone tools (Merge, Split, Rotate, Watermark, Compress, etc.). |
| **2. Dynamic Ribbon** | `ctx.RegisterRibbonAction(RibbonActionDescriptor)`<br/>`ctx.RegisterRibbonTab(RibbonTabDescriptor)`<br/>`ctx.RegisterRibbonGroup(RibbonGroupDescriptor)` | Action pill buttons, custom ribbon groups, or dedicated ribbon tabs. |
| **3. Extensible Sidebars** | `ctx.RegisterSidebarTab(SidebarTabDescriptor)` | Custom sidebar panels (e.g. Audit Logs, Bookmarks, AI Chat, Metadata). |
| **4. Contextual Inspector** | `ctx.RegisterInspectorSection(InspectorSectionDescriptor)` | Custom property editor panels targeting specific canvas elements. |
| **5. Workspace Pages & Sidebar Navigation** | `ctx.RegisterNavigationItem(NavigationItemDescriptor)` | Full workspace pages, analytics studios, integrated editors, or diagnostics appearing directly in the left sidebar navigation. |
| **6. Canvas Elements** | `ctx.RegisterCanvasElement(CanvasElementDescriptor)` | Custom vector elements (Barcode, LaTeX Math, Stamps, Ink, Form Fields). |
| **7. Shell Overlays** | `ctx.RegisterOverlay(OverlayDescriptor)` | Floating, draggable, minimizable M3 utility cards (e.g. Scratchpad, Notes). |
| **8. Command Palette** | `ctx.RegisterCommand(CommandPaletteDescriptor)` | Searchable commands triggered via ⌘K / Ctrl+K with keyboard shortcuts. |
| **9. Status Bar Widgets** | `ctx.RegisterStatusBarWidget(StatusBarWidgetDescriptor)` | Footer status pills, counters, toggles, or telemetry badges. |
| **10. Document Importers** | `ctx.RegisterImporter(IDocumentImporter)` | Custom file format decoders (Markdown, DOCX, XLSX, HTML, EPUB). |
| **11. Document Exporters** | `ctx.RegisterExporter(IDocumentExporter)` | Custom file generators (PDF/A, multi-image sequences, CSV, HTML). |
| **12. OCR & AI Engines** | `ctx.RegisterOcrEngine(IOcrEngine)` | Platform-specific OCR engines (Apple Vision, Tesseract) or local AI models. |
| **13. Data Connectors** | `ctx.RegisterDataConnector(IDataConnector)` | Tabular data loaders (REST APIs, SQLite databases, JSON feeds). |

### Reversible Effects Rule (`ctx.RegisterEffect`)
Whenever your plugin registers event listeners, creates file hooks, or starts background workers, **you MUST track them using `ctx.RegisterEffect`**. When the plugin unloads, FryPDF unwinds these actions in reverse order (LIFO), guaranteeing zero memory leaks and zero dangling event listeners:

```csharp
public Task ApplyAsync(IFryPluginContext ctx, CancellationToken ct = default)
{
    // Subscribe to a message or event
    var token = WeakReferenceMessenger.Default.Register<DocumentChangedMessage>(this, HandleDocumentChanged);

    // Register rollback action for clean teardown
    ctx.RegisterEffect(() =>
    {
        WeakReferenceMessenger.Default.Unregister<DocumentChangedMessage>(this);
    });

    return Task.CompletedTask;
}
```

---

## 5. Step-by-Step Tutorial: Building a "Watermark Plus" Plugin

Let's build a real, fully functional external plugin: **`FryPdf.Plugin.WatermarkPlus`**.  
This plugin:
1. Adds a dedicated full-page PDF tool to the "Optimize & Security" suite.
2. Contributes a quick-action pill to the Ribbon's **View** tab.
3. Contributes a keyboard shortcut to the **Command Palette** (⌘K / Ctrl+K).
4. Provides configurable settings (Default Watermark Text, Default Opacity) with auto-generated M3 UI.

### Step 5.1: Create the Project File (`.csproj`)
Create a new directory named `FryPdf.Plugin.WatermarkPlus` and add `FryPdf.Plugin.WatermarkPlus.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>13</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AssemblyName>FryPdf.Plugin.WatermarkPlus</AssemblyName>
    <RootNamespace>FryPdf.Plugin.WatermarkPlus</RootNamespace>
    <Version>1.0.0</Version>
    <Company>Acme Document Solutions</Company>
    <Description>Enterprise Watermarking Plugin for FryPDF</Description>
  </PropertyGroup>

  <!-- Option A: Direct reference to your installed FryPDF application (Zero source code needed) -->
  <PropertyGroup>
    <!-- Auto-detect FryPDF install path or use FRYPDF_HOME environment variable -->
    <FryPdfInstallDir Condition="'$(FryPdfInstallDir)' == '' and Exists('/Applications/FryPDF.app/Contents/MacOS')">/Applications/FryPDF.app/Contents/MacOS</FryPdfInstallDir>
    <FryPdfInstallDir Condition="'$(FryPdfInstallDir)' == '' and Exists('C:\Program Files\FryPDF')">C:\Program Files\FryPDF</FryPdfInstallDir>
    <FryPdfInstallDir Condition="'$(FryPdfInstallDir)' == '' and Exists('/opt/FryPDF')">/opt/FryPDF</FryPdfInstallDir>
    <FryPdfInstallDir Condition="'$(FryPdfInstallDir)' == ''">$(FRYPDF_HOME)</FryPdfInstallDir>
  </PropertyGroup>

  <ItemGroup>
    <!-- Compile against installed application binaries -->
    <Reference Include="PdfEditorApp.Core">
      <HintPath>$(FryPdfInstallDir)\PdfEditorApp.Core.dll</HintPath>
      <Private>false</Private> <!-- Do not bundle host assemblies into your plugin -->
    </Reference>
    <Reference Include="PdfEditorApp">
      <HintPath>$(FryPdfInstallDir)\PdfEditorApp.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <!-- Option B: NuGet SDK package (Recommended for CI/CD and external plugin repos) -->
  <!--
  <ItemGroup>
    <PackageReference Include="FryPdf.PluginSdk" Version="0.1.2" PrivateAssets="all" />
  </ItemGroup>
  -->

  <ItemGroup>
    <!-- UI & MVVM dependencies (marked Private=false if provided by host) -->
    <PackageReference Include="Avalonia" Version="12.1.1" PrivateAssets="all" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <!-- Include manifest in build output -->
    <None Include="plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

> [!IMPORTANT]
> **Host Dependency Exclusion (`<Private>false</Private>`)**:  
> Always mark `PdfEditorApp.Core`, `PdfEditorApp`, `Avalonia`, and `CommunityToolkit.Mvvm` as compile-only (`Private=false` or `PrivateAssets="all"`). The host application already loads these assemblies. Packaging duplicate copies of Avalonia or Core DLLs inside your plugin will cause type casting errors across `AssemblyLoadContext` boundaries.

---

### Step 5.2: Create the `plugin.json` Manifest
Create `plugin.json` in the project root:

```json
{
  "id": "com.acme.frypdf.watermarkplus",
  "name": "Watermark Plus",
  "version": "1.0.0",
  "author": "Acme Document Solutions",
  "description": "Apply enterprise security watermarks, confidential stamps, and company emblems to multi-page PDFs.",
  "entryPoint": "FryPdf.Plugin.WatermarkPlus.dll",
  "icon": "Watermark",
  "dependencies": [],
  "settingsSchema": {
    "DefaultText": {
      "type": "string",
      "label": "Default Stamp Text",
      "description": "Default text prefilled in the watermark input",
      "default": "CONFIDENTIAL"
    },
    "DefaultOpacity": {
      "type": "number",
      "label": "Default Opacity (%)",
      "description": "Default stamp opacity percentage (1-100)",
      "default": 35
    },
    "IncludeDateStamp": {
      "type": "boolean",
      "label": "Append Current Date",
      "description": "Automatically append today's date below the watermark text",
      "default": true
    },
    "StampColor": {
      "type": "select",
      "label": "Stamp Color",
      "description": "Default watermark tone",
      "default": "Red",
      "options": ["Red", "DarkSlate", "NavyBlue", "EmeraldGreen"]
    }
  }
}
```

#### Supported `settingsSchema` Types:
- **`"string"`**: Renders a Material Design 3 `TextBox.m3-outlined`.
- **`"secret"`**: Renders a secure password field with `PasswordChar="●"`.
- **`"number"`**: Renders an M3 `NumericUpDown.m3-outlined`.
- **`"boolean"`**: Renders a tactile M3 `ToggleSwitch`.
- **`"select"`**: Renders an M3 `ComboBox.m3-outlined` populated with the `options` array.

---

### Step 5.3: Implement the Tool ViewModel
Create `WatermarkPlusViewModel.cs`. Use `CommunityToolkit.Mvvm` for reactive properties and commands:

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Services.Tools.Core;

namespace FryPdf.Plugin.WatermarkPlus;

public partial class WatermarkPlusViewModel : PdfToolViewModelBase
{
    public override string ToolId => "com.acme.frypdf.watermarkplus";
    public override string DisplayName => "Watermark Plus";
    public override string Category => "Optimize & Security";

    [ObservableProperty]
    private string _stampText = "CONFIDENTIAL";

    [ObservableProperty]
    private double _opacityPercent = 35.0;

    [ObservableProperty]
    private bool _includeDate = true;

    [ObservableProperty]
    private string _statusMessage = "Ready to stamp documents.";

    public WatermarkPlusViewModel(IServiceProvider serviceProvider) : base(serviceProvider)
    {
    }

    [RelayCommand]
    private async Task ApplyWatermarkAsync()
    {
        if (SelectedFiles.Count == 0)
        {
            StatusMessage = "Please select at least one PDF document.";
            return;
        }

        IsProcessing = true;
        ProgressValue = 0;
        StatusMessage = "Applying watermark stamps...";

        try
        {
            // Execute heavy PDF stamping off the UI thread via Task.Run
            await Task.Run(async () =>
            {
                int count = SelectedFiles.Count;
                for (int i = 0; i < count; i++)
                {
                    var file = SelectedFiles[i];
                    // Example: Apply stamp using Core deconstruction or QuestPDF pipelines
                    await Task.Delay(150); // Simulate processing

                    ProgressValue = (double)(i + 1) / count * 100.0;
                }
            });

            StatusMessage = $"Successfully watermarked {SelectedFiles.Count} documents!";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }
}
```

---

### Step 5.4: Implement the Avalonia M3 Tool View
Create `WatermarkPlusView.axaml` adhering strictly to **Material Design 3 Expressive** guidelines:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:FryPdf.Plugin.WatermarkPlus"
             xmlns:materialIcons="clr-namespace:Material.Icons.Avalonia;assembly=Material.Icons.Avalonia"
             x:Class="FryPdf.Plugin.WatermarkPlus.WatermarkPlusView"
             x:DataType="vm:WatermarkPlusViewModel">

    <Grid RowDefinitions="Auto,*,Auto" Margin="24">
        <!-- Header Banner (M3 ShapeCornerLarge) -->
        <Border Grid.Row="0"
                Classes="m3-card-elevated"
                Background="{DynamicResource M3SurfaceContainerBrush}"
                CornerRadius="{StaticResource M3ShapeCornerLarge}"
                Padding="20" Margin="0,0,0,20">
            <StackPanel Spacing="6">
                <StackPanel Orientation="Horizontal" Spacing="12" VerticalAlignment="Center">
                    <Border Width="40" Height="40"
                            CornerRadius="{StaticResource M3ShapeCornerFull}"
                            Background="{DynamicResource M3PrimaryContainerBrush}">
                        <materialIcons:MaterialIcon Kind="Watermark" Width="22" Height="22"
                                                    Foreground="{DynamicResource M3OnPrimaryContainerBrush}" />
                    </Border>
                    <TextBlock Text="Watermark Plus Studio"
                               FontSize="22" FontWeight="SemiBold"
                               Foreground="{DynamicResource M3OnSurfaceBrush}"
                               VerticalAlignment="Center" />
                </StackPanel>
                <TextBlock Text="Apply high-security diagonal and marginalia stamps across multi-page PDFs."
                           FontSize="13" Foreground="{DynamicResource M3OnSurfaceVariantBrush}" />
            </StackPanel>
        </Border>

        <!-- Configuration Body -->
        <ScrollViewer Grid.Row="1">
            <StackPanel Spacing="16" MaxWidth="600" HorizontalAlignment="Left">
                <!-- Stamp Text Input -->
                <TextBlock Text="Stamp Text" FontWeight="Medium"
                           Foreground="{DynamicResource M3OnSurfaceBrush}" />
                <TextBox Classes="m3-outlined"
                         Text="{Binding StampText}"
                         Watermark="e.g. STRICTLY CONFIDENTIAL" />

                <!-- Tactile Chubby Opacity Slider -->
                <TextBlock Text="Stamp Opacity" FontWeight="Medium"
                           Foreground="{DynamicResource M3OnSurfaceBrush}" />
                <Grid ColumnDefinitions="*,Auto" Margin="0,4,0,0">
                    <Slider Grid.Column="0"
                            Minimum="5" Maximum="100"
                            Value="{Binding OpacityPercent}"
                            Margin="0,0,16,0" />
                    <TextBlock Grid.Column="1"
                               Text="{Binding OpacityPercent, StringFormat='{}{0:F0}%'}"
                               FontWeight="Bold" VerticalAlignment="Center"
                               Foreground="{DynamicResource M3PrimaryBrush}" />
                </Grid>

                <!-- Append Date Toggle -->
                <CheckBox Content="Append Current Date &amp; Timestamp"
                          IsChecked="{Binding IncludeDate}"
                          Foreground="{DynamicResource M3OnSurfaceBrush}" />

                <!-- Status Feedback -->
                <TextBlock Text="{Binding StatusMessage}"
                           FontSize="12" Foreground="{DynamicResource M3OutlineBrush}" />
            </StackPanel>
        </ScrollViewer>

        <!-- Action Footer with Tactile Pill Button -->
        <Border Grid.Row="2" Padding="0,16,0,0" BorderBrush="{DynamicResource M3OutlineVariantBrush}" BorderThickness="0,1,0,0">
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Spacing="12">
                <Button Classes="m3-filled-btn"
                        CornerRadius="{StaticResource M3ShapeCornerFull}"
                        Command="{Binding ApplyWatermarkCommand}"
                        Padding="24,10">
                    <StackPanel Orientation="Horizontal" Spacing="8">
                        <materialIcons:MaterialIcon Kind="CheckCircleOutline" Width="18" Height="18" />
                        <TextBlock Text="Apply Watermark" FontWeight="SemiBold" />
                    </StackPanel>
                </Button>
            </StackPanel>
        </Border>
    </Grid>
</UserControl>
```

And the code-behind in `WatermarkPlusView.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace FryPdf.Plugin.WatermarkPlus;

public partial class WatermarkPlusView : UserControl
{
    public WatermarkPlusView()
    {
        InitializeComponent();
    }
}
```

---

### Step 5.5: Implement the Main Plugin Class
Now connect everything together by implementing `IFryPlugin` in `WatermarkPlusPlugin.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PdfEditorApp.Core.Plugins;
using PdfEditorApp.Core.Plugins.Descriptors;
using PdfEditorApp.Core.Plugins.Manifests;
using PdfEditorApp.Services.Tools.Core;

namespace FryPdf.Plugin.WatermarkPlus;

public class WatermarkPlusPlugin : IFryPlugin
{
    public string Id => "com.acme.frypdf.watermarkplus";
    public string Name => "Watermark Plus";
    public Version Version => new(1, 0, 0);

    public IReadOnlyList<Type> RequiredServices => Array.Empty<Type>();
    public IReadOnlyList<Type> ProvidedServices => Array.Empty<Type>();

    public IReadOnlyDictionary<string, PluginSettingDefinition>? SettingsSchema => new Dictionary<string, PluginSettingDefinition>
    {
        ["DefaultText"] = new()
        {
            Type = "string",
            Label = "Default Stamp Text",
            Description = "Default text prefilled in the watermark input",
            DefaultValue = "CONFIDENTIAL"
        },
        ["DefaultOpacity"] = new()
        {
            Type = "number",
            Label = "Default Opacity (%)",
            Description = "Default stamp opacity percentage (1-100)",
            DefaultValue = 35
        }
    };

    public Task ApplyAsync(IFryPluginContext ctx, CancellationToken ct = default)
    {
        // 1. Register Dedicated Full-Page Tool
        var toolRegistration = ctx.RegisterTool(new PdfToolDescriptor
        {
            Id = Id,
            Name = Name,
            Description = "Apply custom security watermarks with dynamic opacity and timestamp stamps.",
            Category = "OptimizeAndSecurity",
            IconKind = "Watermark",
            IconColorHex = "#0284C7",
            BackgroundAccentHex = "#F0F9FF",
            SupportsMultiFile = true,
            AcceptedFileExtensions = ".pdf",
            CreateViewModel = sp => ActivatorUtilities.CreateInstance<WatermarkPlusViewModel>(sp)
        });

        // 2. Register Ribbon Action in the 'Tools' Tab
        var ribbonRegistration = ctx.RegisterRibbonAction(new RibbonActionDescriptor
        {
            Id = "action.ribbon.watermarkplus",
            TabId = "tools",
            GroupId = "security",
            Label = "Watermark Plus",
            Tooltip = "Open Watermark Plus Studio",
            IconKind = "Watermark",
            Order = 105,
            Action = sp =>
            {
                if (sp.GetService(typeof(IPdfToolRegistry)) is IPdfToolRegistry toolRegistry)
                {
                    toolRegistry.OpenTool(Id);
                }
            }
        });

        // 3. Register Command in Command Palette (Ctrl+K / ⌘K)
        var cmdRegistration = ctx.RegisterCommand(new CommandPaletteDescriptor
        {
            Id = "cmd.watermarkplus.open",
            Title = "Open Watermark Plus Studio",
            Subtitle = "Security stamp and watermark tool",
            Category = "Security",
            IconKind = "Watermark",
            Shortcut = "Ctrl+Shift+W",
            Order = 80,
            Action = sp =>
            {
                if (sp.GetService(typeof(IPdfToolRegistry)) is IPdfToolRegistry toolRegistry)
                {
                    toolRegistry.OpenTool(Id);
                }
            }
        });

        // 4. Register Reversible Effects for 100% Clean Unload
        ctx.RegisterEffect(() =>
        {
            toolRegistration.Dispose();
            ribbonRegistration.Dispose();
            cmdRegistration.Dispose();
        });

        return Task.CompletedTask;
    }
}
```

---

---

## 6. Step-by-Step Tutorial: Building an Integrated Workspace Page & Sidebar Plugin (Full Studio View)

While Section 5 demonstrated how to build a tool inside the "All Tools" grid, FryPDF also allows external plugins to integrate **full-page workspace studios and diagnostic dashboards directly into the main application shell and sidebar**—exactly like FryPDF's built-in **Diagnostic Logs**, **Font Manager**, or **PDF Reader** views!

### 6.1 Workspace Page vs. Tool Plugin
| Capability | Tool Plugin (`ToolPluginBase`) | Workspace Page Plugin (`NavigationItemDescriptor`) |
| :--- | :--- | :--- |
| **Shell Placement** | Card inside "All Tools" / Category grids | Dedicated clickable item in the left sidebar navigation |
| **Sidebar Group** | Filtered under tool categories | Placed into `Overview`, `Categories`, `Library`, `Preferences`, or custom groups |
| **Viewport Hosting** | Standard `PdfToolPageView` frame | Independent full-bleed view (`ScrollableDocument`, `FullViewport`, or `ImmersiveStudio`) |
| **Header Bar** | Tool header with input file cards | Contextual top bar, host search bar, or custom full-width toolbar |
| **Best Used For** | File operations (Merge, Split, Watermark, Compress) | Full studio environments (Markdown editors, CAD viewers, Analytics dashboards, Log viewers) |

---

### 6.2 Understanding `NavigationItemDescriptor`
Workspace pages are registered using `ctx.RegisterNavigationItem(NavigationItemDescriptor)`:

```csharp
public sealed class NavigationItemDescriptor
{
    // Unique identifier for the page (e.g. "com.acme.analytics")
    public required string Id { get; init; }

    // Label displayed in the sidebar item and tooltip
    public required string Title { get; init; }

    // Sidebar group header: "Overview", "Categories", "Library", "Preferences",
    // or any custom string (e.g. "Extensions", "Studios") which creates a new header!
    public string Group { get; init; } = "General";

    // Material Design icon name (e.g. "ChartTimelineVariantShimmer", "FormatListBulletedSquare")
    public string IconKind { get; init; } = "ApplicationOutline";

    // Optional status pill badge (e.g. "PRO", "New", "Logs", "Beta")
    public string? BadgeText { get; init; }

    // Hex color for the badge background (e.g. "#DC2626" for red, "#7C3AED" for purple)
    public string? BadgeColorHex { get; init; }

    // Numeric sorting position within the sidebar group (ascending order)
    public int Order { get; init; } = 100;

    // Viewport layout mode:
    // - ScrollableDocument: Hosted inside a standard ScrollViewer beneath the top search bar.
    // - FullViewport: Edge-to-edge container without outer scroll (ideal for split views & tables).
    // - ImmersiveStudio: Fills the entire window, hides search bar, and collapses sidebar to 68px rail.
    public NavigationDisplayMode DisplayMode { get; init; } = NavigationDisplayMode.ScrollableDocument;

    // When true, hides the top global search bar so your page occupies the full vertical height
    public bool HideTopSearchBar { get; init; } = false;

    // Factory method that instantiates and returns the Avalonia UserControl view
    public Func<IServiceProvider, object>? ViewFactory { get; init; }
}
```

---

### 6.3 Building the "Analytics Studio" Workspace Plugin

Let's build **`FryPdf.Plugin.AnalyticsStudio`**, an external plugin that adds an interactive document analytics and diagnostics studio to FryPDF's sidebar.

#### Step 6.3.1: Create Project File (`FryPdf.Plugin.AnalyticsStudio.csproj`)
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>13</LangVersion>
    <AssemblyName>FryPdf.Plugin.AnalyticsStudio</AssemblyName>
    <RootNamespace>FryPdf.Plugin.AnalyticsStudio</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.2.0" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
    <PackageReference Include="Material.Icons.Avalonia" Version="2.2.0" />
  </ItemGroup>

  <ItemGroup>
    <!-- Reference host core contracts (provided by installed FryPDF at runtime) -->
    <Reference Include="PdfEditorApp.Core">
      <HintPath>$(MSBuildProgramFiles32)\FryPDF\PdfEditorApp.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="PdfEditorApp">
      <HintPath>$(MSBuildProgramFiles32)\FryPDF\PdfEditorApp.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <ItemGroup>
    <AvaloniaResource Include="**\*.axaml" />
  </ItemGroup>
</Project>
```

#### Step 6.3.2: Create the Plugin Class (`AnalyticsStudioPlugin.cs`)
```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PdfEditorApp.Core.Plugins;
using PdfEditorApp.Core.Plugins.Descriptors;

namespace FryPdf.Plugin.AnalyticsStudio;

public class AnalyticsStudioPlugin : IFryPlugin
{
    public string Id => "com.acme.frypdf.analyticsstudio";
    public string Name => "Document Analytics Studio";
    public Version Version => new(1, 0, 0);

    public IReadOnlyList<Type> RequiredServices => Array.Empty<Type>();
    public IReadOnlyList<Type> ProvidedServices => Array.Empty<Type>();

    public Task ApplyAsync(IFryPluginContext ctx, CancellationToken ct = default)
    {
        // 1. Register the workspace page into the host's navigation registry
        ctx.RegisterNavigationItem(new NavigationItemDescriptor
        {
            Id = "AnalyticsStudio",
            Title = "Analytics Studio",
            Group = "Library",                         // Placed in Library section beside Help & Logs
            IconKind = "ChartTimelineVariantShimmer",    // M3 icon
            BadgeText = "PRO",                         // Pill badge
            BadgeColorHex = "#7C3AED",                 // Purple accent
            Order = 165,                               // Positioned after Licensing (160)
            DisplayMode = NavigationDisplayMode.FullViewport, // Edge-to-edge layout
            HideTopSearchBar = false,
            ViewFactory = sp =>
            {
                var view = new Views.AnalyticsStudioView();
                view.DataContext = new ViewModels.AnalyticsStudioViewModel(ctx, sp);
                return view;
            }
        });

        return Task.CompletedTask;
    }
}
```

#### Step 6.3.3: Create the ViewModel (`AnalyticsStudioViewModel.cs`)
```csharp
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Core.Plugins;

namespace FryPdf.Plugin.AnalyticsStudio.ViewModels;

public partial class AnalyticsStudioViewModel : ObservableObject
{
    private readonly IFryPluginContext _ctx;
    private readonly IServiceProvider _serviceProvider;

    [ObservableProperty]
    private string _statusMessage = "Ready to analyze documents.";

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private int _analyzedPageCount;

    [ObservableProperty]
    private int _embeddedFontCount;

    [ObservableProperty]
    private int _imageCount;

    public ObservableCollection<string> Findings { get; } = new();

    public AnalyticsStudioViewModel(IFryPluginContext ctx, IServiceProvider serviceProvider)
    {
        _ctx = ctx;
        _serviceProvider = serviceProvider;
        Findings.Add("Audit engine initialized. Load a PDF to begin verification.");
    }

    [RelayCommand]
    private async Task RunAuditAsync()
    {
        IsAnalyzing = true;
        StatusMessage = "Analyzing document structure and layout hierarchy...";

        await Task.Delay(1000); // Offload CPU operations with Task.Run in real plugins

        AnalyzedPageCount = 14;
        EmbeddedFontCount = 6;
        ImageCount = 22;

        Findings.Clear();
        Findings.Add("✓ PDF/A-2b compliance validated.");
        Findings.Add("✓ All 6 embedded fonts contain complete Unicode ToUnicode CMap tables.");
        Findings.Add("✓ Zero uncompressed image streams detected.");
        Findings.Add("ℹ 2 vector paths exceed 4000 nodes (recommend path simplification).");

        StatusMessage = "Analysis complete. 4 metrics audited.";
        IsAnalyzing = false;
    }
}
```

#### Step 6.3.4: Create the Avalonia View (`AnalyticsStudioView.axaml`)
All host Google Material Design 3 Expressive tokens, corner shapes, and dynamic brushes are automatically inherited:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:materialIcons="clr-namespace:Material.Icons.Avalonia;assembly=Material.Icons.Avalonia"
             xmlns:vm="clr-namespace:FryPdf.Plugin.AnalyticsStudio.ViewModels"
             x:Class="FryPdf.Plugin.AnalyticsStudio.Views.AnalyticsStudioView"
             x:DataType="vm:AnalyticsStudioViewModel"
             Background="{DynamicResource M3SurfaceBrush}">

    <Grid RowDefinitions="Auto,Auto,*" Margin="24,20,24,20">

        <!-- 1. Header Card with M3 Elevation and Action Pill -->
        <Border Grid.Row="0"
                Classes="m3-card-elevated"
                Padding="24,20"
                Margin="0,0,0,16">
            <Grid ColumnDefinitions="Auto,*,Auto">
                <!-- M3 Container Icon Badge -->
                <Border Grid.Column="0"
                        Width="48" Height="48"
                        CornerRadius="{StaticResource M3ShapeCornerMedium}"
                        Background="{DynamicResource M3PrimaryContainerBrush}">
                    <materialIcons:MaterialIcon Kind="ChartTimelineVariantShimmer"
                                                Width="26" Height="26"
                                                Foreground="{DynamicResource M3PrimaryBrush}" />
                </Border>

                <!-- Titles -->
                <StackPanel Grid.Column="1" Margin="16,0,0,0" VerticalAlignment="Center">
                    <StackPanel Orientation="Horizontal" Spacing="10">
                        <TextBlock Text="Document Analytics Studio"
                                   FontSize="20" FontWeight="Bold"
                                   Foreground="{DynamicResource WinTextBrush}" />
                        <Border Background="#7C3AED"
                                CornerRadius="{StaticResource M3ShapeCornerExtraSmall}"
                                Padding="6,2" VerticalAlignment="Center">
                            <TextBlock Text="PRO" FontSize="10" FontWeight="Bold" Foreground="White" />
                        </Border>
                    </StackPanel>
                    <TextBlock Text="{Binding StatusMessage}"
                               FontSize="13" Margin="0,4,0,0"
                               Foreground="{DynamicResource WinMutedBrush}" />
                </StackPanel>

                <!-- Primary Action Pill Button -->
                <Button Grid.Column="2"
                        Classes="primary-btn"
                        Command="{Binding RunAuditCommand}"
                        IsEnabled="{Binding !IsAnalyzing}">
                    <StackPanel Orientation="Horizontal" Spacing="8">
                        <materialIcons:MaterialIcon Kind="PlayCircleOutline" Width="18" Height="18" />
                        <TextBlock Text="Run Full Audit" FontWeight="SemiBold" />
                    </StackPanel>
                </Button>
            </Grid>
        </Border>

        <!-- 2. Metric Counters Grid -->
        <Grid Grid.Row="1" ColumnDefinitions="*,*,*" Margin="0,0,0,16">
            <!-- Metric 1 -->
            <Border Grid.Column="0" Classes="m3-card-elevated" Margin="0,0,8,0" Padding="18,14">
                <StackPanel>
                    <TextBlock Text="PAGES AUDITED" FontSize="11" FontWeight="Bold" Foreground="{DynamicResource WinSubtleBrush}" />
                    <TextBlock Text="{Binding AnalyzedPageCount}" FontSize="28" FontWeight="ExtraBold" Foreground="{DynamicResource WinTextBrush}" Margin="0,4,0,0" />
                </StackPanel>
            </Border>

            <!-- Metric 2 -->
            <Border Grid.Column="1" Classes="m3-card-elevated" Margin="4,0,4,0" Padding="18,14">
                <StackPanel>
                    <TextBlock Text="EMBEDDED FONTS" FontSize="11" FontWeight="Bold" Foreground="{DynamicResource WinSubtleBrush}" />
                    <TextBlock Text="{Binding EmbeddedFontCount}" FontSize="28" FontWeight="ExtraBold" Foreground="{DynamicResource M3PrimaryBrush}" Margin="0,4,0,0" />
                </StackPanel>
            </Border>

            <!-- Metric 3 -->
            <Border Grid.Column="2" Classes="m3-card-elevated" Margin="8,0,0,0" Padding="18,14">
                <StackPanel>
                    <TextBlock Text="EXTRACTED IMAGES" FontSize="11" FontWeight="Bold" Foreground="{DynamicResource WinSubtleBrush}" />
                    <TextBlock Text="{Binding ImageCount}" FontSize="28" FontWeight="ExtraBold" Foreground="{DynamicResource WinTextBrush}" Margin="0,4,0,0" />
                </StackPanel>
            </Border>
        </Grid>

        <!-- 3. Audit Findings List Container -->
        <Border Grid.Row="2" Classes="m3-card-elevated" Padding="20">
            <Grid RowDefinitions="Auto,*">
                <TextBlock Grid.Row="0" Text="Audit Findings &amp; Observations"
                           FontSize="15" FontWeight="SemiBold"
                           Foreground="{DynamicResource WinTextBrush}" Margin="0,0,0,12" />

                <ScrollViewer Grid.Row="1">
                    <ItemsControl ItemsSource="{Binding Findings}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <Border Background="{DynamicResource WinHoverBrush}"
                                        CornerRadius="{StaticResource M3ShapeCornerMedium}"
                                        Margin="0,0,0,8" Padding="14,10">
                                    <TextBlock Text="{Binding}" FontSize="13" Foreground="{DynamicResource WinTextBrush}" />
                                </Border>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </ScrollViewer>
            </Grid>
        </Border>
    </Grid>
</UserControl>
```

#### Step 6.3.5: Code-Behind (`AnalyticsStudioView.axaml.cs`)
```csharp
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FryPdf.Plugin.AnalyticsStudio.Views;

public partial class AnalyticsStudioView : UserControl
{
    public AnalyticsStudioView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
```

#### Step 6.3.6: Plugin Manifest (`plugin.json`)
```json
{
  "id": "com.acme.frypdf.analyticsstudio",
  "name": "Document Analytics Studio",
  "version": "1.0.0",
  "author": "Acme Software",
  "description": "Full-page workspace studio for deep PDF layout, font, and compliance auditing.",
  "entryPoint": "FryPdf.Plugin.AnalyticsStudio.dll",
  "icon": "ChartTimelineVariantShimmer"
}
```

---

### 6.4 What Happens When the User Installs It
1. The user drags and drops `FryPdf.Plugin.AnalyticsStudio.fryplugin` into FryPDF's **Plugins & Extensions** page.
2. FryPDF unzips it into `%LocalAppData%\FryPDF\plugins\com.acme.frypdf.analyticsstudio\`.
3. `CollectiblePluginLoadContext` loads the assembly and calls `AnalyticsStudioPlugin.ApplyAsync(ctx)`.
4. `ctx.RegisterNavigationItem` registers the descriptor.
5. The sidebar **immediately updates in real-time** without restarting FryPDF:
   - Under the `LIBRARY` section, **"Analytics Studio"** appears with the `ChartTimelineVariantShimmer` icon and the purple `PRO` badge!
6. When the user clicks the item, `ViewFactory` instantiates `AnalyticsStudioView`, caches it for 0ms future tab-switches, and presents it in the full viewport!
7. When the plugin is uninstalled, `INavigationRegistry.UnregisterNavigationItem` unhooks the sidebar item and cleans up automatically.

---

## 7. Packaging Your Plugin (`.fryplugin`)

FryPDF uses `.fryplugin` distribution packages. A `.fryplugin` file is simply a standard ZIP archive with the following structure:

```
WatermarkPlus.fryplugin (ZIP file)
├── plugin.json                    # Root manifest
├── FryPdf.Plugin.WatermarkPlus.dll# Compiled entry assembly
└── [OtherDependency.dll]          # Any external 3rd-party dependencies (excluding host DLLs)
```

### 7.1 Automated Packaging with MSBuild (Recommended)
Add the following snippet to the bottom of your `.csproj` file. Every time you run `dotnet build -c Release`, MSBuild will automatically package your plugin into a `.fryplugin` file ready for installation:

```xml
  <!-- Automated .fryplugin packager -->
  <Target Name="PackageFryPlugin" AfterTargets="Build" Condition="'$(Configuration)' == 'Release'">
    <PropertyGroup>
      <PluginStagingDir>$(TargetDir)staging\</PluginStagingDir>
      <OutputFryPlugin>$(TargetDir)$(AssemblyName).fryplugin</OutputFryPlugin>
    </PropertyGroup>

    <RemoveDir Directories="$(PluginStagingDir)" />
    <MakeDir Directories="$(PluginStagingDir)" />

    <!-- Copy compiled DLLs and plugin.json -->
    <ItemGroup>
      <PluginFiles Include="$(TargetDir)*.dll" Exclude="$(TargetDir)PdfEditorApp*.dll;$(TargetDir)Avalonia*.dll" />
      <PluginFiles Include="$(ProjectDir)plugin.json" />
    </ItemGroup>

    <Copy SourceFiles="@(PluginFiles)" DestinationFolder="$(PluginStagingDir)" />
    <Delete Files="$(OutputFryPlugin)" Condition="Exists('$(OutputFryPlugin)')" />
    <ZipDirectory SourceDirectory="$(PluginStagingDir)" DestinationFile="$(OutputFryPlugin)" />
    <RemoveDir Directories="$(PluginStagingDir)" />

    <Message Importance="High" Text="✨ Successfully packaged FryPDF plugin: $(OutputFryPlugin)" />
  </Target>
```

### 7.2 Manual Packaging via CLI
You can also package your plugin manually using standard command-line tools:

```bash
# 1. Build project in Release mode
dotnet build -c Release

# 2. Navigate to output folder
cd bin/Release/net10.0/

# 3. Create ZIP archive renamed to .fryplugin
zip -r WatermarkPlus.fryplugin plugin.json FryPdf.Plugin.WatermarkPlus.dll
```

---

## 8. Installing and Testing Your Plugin in FryPDF

FryPDF supports 3 seamless ways to load and test your external plugin:

### Method 1: Drag-and-Drop Installation (Easiest)
1. Launch **FryPDF**.
2. Open the **Plugins Manager** (Click the Settings gear in the header $\to$ **Plugins & Extensions**, or press `Ctrl+Shift+P` / `⌘Shift+P`).
3. Drag and drop your `.fryplugin` (or `.dll`) directly onto the dialog window.
4. FryPDF immediately unpacks the package, inspects the manifest, loads the assembly into an isolated `CollectiblePluginLoadContext`, and mounts your plugin with zero restart required!

### Method 2: Command Palette or File Picker
1. In FryPDF, press `Ctrl+K` or `⌘K` to open the **Command Palette**.
2. Type `Install Plugin Package...` and press Enter.
3. Select your `.fryplugin` or compiled `.dll` file from the open file dialog.

### Method 3: Direct File Placement (Auto-Discovery)
On startup, FryPDF scans designated plugin directories and automatically loads all unpacked folders, `.fryplugin` archives, and standalone `.dll` files. You can drop your plugin files into either of the following locations:

#### Platform User Directories:
- **macOS**: `~/Library/Application Support/FryPdf/plugins/<PluginId>/`
- **Windows**: `%APPDATA%\FryPdf\plugins\<PluginId>\` (e.g. `C:\Users\<User>\AppData\Roaming\FryPdf\plugins\<PluginId>\`)
- **Linux**: `~/.config/FryPdf/plugins/<PluginId>/`

#### Application Local Directory:
- `<ApplicationDirectory>/plugins/<PluginId>/`

---

## 9. Material Design 3 (M3) Expressive Styling Mandate

To maintain visual harmony with FryPDF, all external plugins must adhere to **Google Material Design 3 Expressive**:

### 9.1 Shape Scale Tokens
Always reference centralized M3 shape scale tokens rather than hardcoding arbitrary numbers:

| Token Key | Radius | Usage |
| :--- | :--- | :--- |
| `{StaticResource M3ShapeCornerFull}` | `9999px` | Buttons (`m3-filled-btn`, `m3-tonal-btn`), segmented pills, search boxes (`TextBox.m3-search`), slider thumbs. |
| `{StaticResource M3ShapeCornerExtraLarge}` | `28px` | Modal dialog cards (`Border.m3-dialog-card`), hero banners. |
| `{StaticResource M3ShapeCornerLarge}` | `16px` | Content cards (`m3-card-elevated`), inputs (`TextBox.m3-outlined`), ComboBoxes. |
| `{StaticResource M3ShapeCornerMedium}` | `12px` | Context menus, flyouts, tooltips, list items. |
| `{StaticResource M3ShapeCornerSmall}` | `8px` | Badges, chips, thumbnail cards. |

### 9.2 Dynamic Tonal Brushes (Dark & Light Mode)
**NEVER** hardcode hex colors (like `#FFFFFF` or `#1E293B`) in your views. Always reference semantic `{DynamicResource ...}` keys so your plugin seamlessly responds to daylight, warm sepia, dark night, and high contrast themes:

- **Primary Colors**: `M3PrimaryBrush`, `M3OnPrimaryBrush`, `M3PrimaryContainerBrush`, `M3OnPrimaryContainerBrush`
- **Surfaces**: `M3SurfaceBrush`, `M3SurfaceContainerBrush`, `M3SurfaceContainerHighestBrush`, `M3SurfaceDimBrush`
- **Text & Outlines**: `M3OnSurfaceBrush`, `M3OnSurfaceVariantBrush`, `M3OutlineBrush`, `M3OutlineVariantBrush`

---

## 10. Performance & Memory Safety Mandates

FryPDF is an interactive 60+ FPS studio. External plugins must comply with the following performance rules:

1. **Zero UI Thread Blocking**:
   - **Never** perform PDF parsing, document rendering, OCR, or file I/O synchronously on the Avalonia UI thread.
   - Offload heavy tasks using `await Task.Run(...)` and support cooperative cancellation (`CancellationToken`).
2. **Unmanaged Graphics Disposal**:
   - If using `SkiaSharp` (`SKBitmap`, `SKImage`, `SKSurface`), always dispose old instances when replacing preview images to prevent native memory leaks.
3. **No Large Object Heap (LOH) Allocations**:
   - Do not allocate large byte arrays ($\ge 85\text{ KB}$) in tight loops. Use `ArrayPool<byte>.Shared` or streams.
4. **Collectible ALC Hygiene**:
   - Avoid creating static references, global event hooks, or long-lived thread-pool timers that reference your plugin types without an unsubscription mechanism. Static references will anchor your `AssemblyLoadContext` and prevent the plugin from being garbage collected when unloaded.

---

## 11. Automated Testing of External Plugins

You can verify your plugin's mounting and lifecycle in an xUnit test project using the same loader FryPDF uses:

```csharp
using System.IO;
using PdfEditorApp.Core.Plugins;
using PdfEditorApp.Plugins.Loader;
using Xunit;

namespace FryPdf.Plugin.WatermarkPlus.Tests;

public class PluginLifecycleTests
{
    [Fact]
    public void Plugin_Loads_And_Instantiates_Successfully()
    {
        var dllPath = typeof(WatermarkPlusPlugin).Assembly.Location;
        using var package = PluginAssemblyLoader.LoadPluginAssembly(dllPath);

        Assert.NotEmpty(package.Plugins);
        var plugin = package.Plugins[0];
        Assert.Equal("com.acme.frypdf.watermarkplus", plugin.Id);
        Assert.Equal("Watermark Plus", plugin.Name);
    }

    [Fact]
    public void PluginPackage_Unpacks_And_Validates_Manifest()
    {
        var tempPkg = Path.Combine(Path.GetTempPath(), "test.fryplugin");
        var stagingDir = Path.GetDirectoryName(typeof(WatermarkPlusPlugin).Assembly.Location)!;

        // Create package
        FryPluginPackageLoader.CreatePackage(stagingDir, tempPkg);
        Assert.True(File.Exists(tempPkg));

        // Unpack and verify
        using var result = FryPluginPackageLoader.UnpackAndLoad(tempPkg);
        Assert.Equal("com.acme.frypdf.watermarkplus", result.Manifest.Id);
        Assert.NotNull(result.AssemblyPackage);

        File.Delete(tempPkg);
    }
}
```

---

## 12. Summary & Next Steps

With FryPDF's modular microkernel architecture, creating professional external plugins is clean, fast, and robust:
- Implement `IFryPlugin` (or inherit `ToolPluginBase`).
- Register capabilities into `IFryPluginContext` with reversible rollback.
- Add `plugin.json` and package as `.fryplugin`.
- Drag and drop onto FryPDF to run!

For in-depth details on internal pipeline mechanics, DAG solvers, and core architecture, see:
- [Plugin-Based Architecture Manual (`PLUGIN_BASED_ARCHITECTURE.md`)](PLUGIN_BASED_ARCHITECTURE.md)
- [Material Design 3 Expressive Guidelines (`MATERIAL_DESIGN_3_EXPRESSIVE_GUIDELINES.md`)](MATERIAL_DESIGN_3_EXPRESSIVE_GUIDELINES.md)
- [PDF Deconstruction and Vector Editing (`PDF_DECONSTRUCTION_AND_EDITING.md`)](PDF_DECONSTRUCTION_AND_EDITING.md)
