using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PdfEditorApp.Core.Models;
using PdfEditorApp.Core.Plugins;
using PdfEditorApp.Core.Plugins.Descriptors;
using PdfEditorApp.Models;
using PdfEditorApp.Services;
using PdfEditorApp.Services.Tools;
using PdfEditorApp.Services.Tools.Conversion;
using PdfEditorApp.Services.Tools.Core;
using PdfEditorApp.Services.Tools.Intelligence;
using PdfEditorApp.Services.Tools.Organize;
using PdfEditorApp.Services.Tools.Security;
using PdfEditorApp.ViewModels;
using PdfEditorApp.ViewModels.ElementViewModels;
using PdfEditorApp.ViewModels.Tools;
using PdfEditorApp.ViewModels.Tools.Core;
using Xunit;

namespace PdfEditorApp.Tests;

public class QualityAndBugFixTests
{
    [Fact]
    public void MainViewModel_ImplementsIServiceProvider_AndDoesNotThrowInvalidCastException()
    {
        var mainVm = new MainViewModel();
        Assert.IsAssignableFrom<IServiceProvider>(mainVm);

        var sp = (IServiceProvider)mainVm;
        Assert.NotNull(sp);

        // Resolving self
        var self = sp.GetService(typeof(MainViewModel));
        Assert.Same(mainVm, self);

        // Resolving unknown type before plugin host initialization returns null gracefully without exception
        var unknown = sp.GetService(typeof(QualityAndBugFixTests));
        Assert.Null(unknown);

        mainVm.Dispose();
    }

    [Fact]
    public void UndoRedoService_RecordAction_DiscardsRedoStackSafely()
    {
        var undoRedo = new UndoRedoService();
        bool action1Discarded = false;
        bool action2Discarded = false;

        // Record first action
        undoRedo.RecordAction(
            "Action 1",
            () => { },
            () => { },
            () => { action1Discarded = true; });

        // Record second action
        undoRedo.RecordAction(
            "Action 2",
            () => { },
            () => { },
            () => { action2Discarded = true; });

        // Undo action 2 -> now action 2 is on the redo stack
        var undone = undoRedo.Undo();
        Assert.Equal("Action 2", undone);
        Assert.False(action2Discarded, "Action 2 should not be discarded when undone");

        // Record a new action 3 -> this clears the redo stack; Action 2's OnDiscarded MUST be called
        undoRedo.RecordAction("Action 3", () => { }, () => { });

        Assert.True(action2Discarded, "Action 2 on redo stack should have been discarded when a new action was recorded");
        Assert.False(action1Discarded, "Action 1 is still on the undo list and should not be discarded");
    }

    [Fact]
    public void PdfViewerViewModel_Dispose_CleansUpResourcesSafely()
    {
        var viewerVm = new PdfViewerViewModel();

        // Simulate loaded dummy page
        viewerVm.Pages.Add(new PdfViewerPageItem { PageNumber = 1, WidthPoints = 595, HeightPoints = 842 });
        Assert.NotEmpty(viewerVm.Pages);

        // Dispose should empty pages and not throw
        viewerVm.Dispose();
        Assert.Empty(viewerVm.Pages);

        // Second call should be a safe no-op
        viewerVm.Dispose();
    }

    [Fact]
    public async Task PdfDocumentOperationsService_ExecutesCustomToolViaExecutionHandler()
    {
        var context = new FryPluginContext();
        var registry = new PdfToolRegistry(context);

        bool customHandlerCalled = false;
        var customToolId = (PdfToolId)9999;
        var customToolDef = new PdfToolDefinition
        {
            Id = customToolId,
            Name = "Unit Test Dynamic Tool",
            Description = "Custom tool with execution handler",
            Category = PdfToolCategory.EditAndForms,
            ExecutionHandler = (options, progress, ct) =>
            {
                customHandlerCalled = true;
                progress?.Report(100.0);
                return Task.FromResult(new ToolExecutionResult
                {
                    Success = true,
                    Message = "Custom plugin execution succeeded."
                });
            }
        };

        registry.RegisterTool(customToolDef);

        var opsService = new PdfDocumentOperationsService(
            registry,
            new PdfPageService(),
            new PdfOptimizationService(),
            new PdfSecurityService(),
            new PdfConversionService(),
            new PdfOcrService(),
            new PdfFormService(),
            new AiDocumentService(),
            new DocumentTranslationService(),
            new PdfWorkflowEngine(),
            context);

        var result = await opsService.ExecuteToolAsync(customToolId, new { DummyParam = 42 });

        Assert.True(customHandlerCalled, "Custom ExecutionHandler on PdfToolDefinition should have been executed");
        Assert.True(result.Success);
        Assert.Equal("Custom plugin execution succeeded.", result.Message);
    }

    [Fact]
    public void ImageElementViewModel_DefaultCornerRadius_IsM3Small8()
    {
        var imageVm = new ImageElementViewModel();
        Assert.Equal(8.0, imageVm.CornerRadius);
    }

    private static string GetProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FryPDF.sln")) || Directory.Exists(Path.Combine(dir.FullName, "src", "PdfEditorApp")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Project root could not be located.");
    }

    [Fact]
    public void Views_AdhereToMaterial3ExpressiveTokensAndNoLegacyArbitraryRadii()
    {
        var root = GetProjectRoot();
        var viewsDir = Path.Combine(root, "src", "PdfEditorApp", "Views");

        // 1. PdfToolsStudioView.axaml
        var toolsStudio = File.ReadAllText(Path.Combine(viewsDir, "PdfToolsStudioView.axaml"));
        Assert.DoesNotContain("CornerRadius=\"20\"", toolsStudio);
        Assert.Contains("CornerRadius=\"{StaticResource M3ShapeCornerFull}\"", toolsStudio);

        // 2. PdfToolPageView.axaml
        var toolPage = File.ReadAllText(Path.Combine(viewsDir, "PdfToolPageView.axaml"));
        Assert.DoesNotContain("Setter Property=\"CornerRadius\" Value=\"8\"", toolPage);
        Assert.Contains("CornerRadius=\"{StaticResource M3ShapeCornerFull}\"", toolPage);
        Assert.DoesNotContain("Background\" Value=\"#EFF6FF\"", toolPage);

        // 3. HelpGuidePageView.axaml
        var helpGuide = File.ReadAllText(Path.Combine(viewsDir, "HelpGuidePageView.axaml"));
        Assert.DoesNotContain("Background=\"#E0F2FE\"", helpGuide);
        Assert.DoesNotContain("CornerRadius=\"20\"", helpGuide);
        Assert.Contains("M3SecondaryContainerBrush", helpGuide);
        Assert.Contains("M3SuccessContainerBrush", helpGuide);

        // 4. InspectorSidebarView.axaml
        var inspector = File.ReadAllText(Path.Combine(viewsDir, "InspectorSidebarView.axaml"));
        Assert.DoesNotContain("CornerRadius=\"9\"", inspector);

        // 5. TrashCachePageView.axaml
        var trashPage = File.ReadAllText(Path.Combine(viewsDir, "TrashCachePageView.axaml"));
        Assert.DoesNotContain("Background=\"#FEF2F2\"", trashPage);
        Assert.Contains("M3ErrorContainerBrush", trashPage);
        Assert.Contains("CornerRadius=\"{StaticResource M3ShapeCornerFull}\"", trashPage);
    }

    private static MainViewModel CreateMainViewModelWithWorkspacePages()
    {
        var navReg = new PdfEditorApp.Services.Navigation.NavigationRegistry();
        var context = new FryPluginContext();
        context.RegisterService<INavigationRegistry>(navReg);

        var host = new PluginHost(context);
        var bundle = new PdfEditorApp.Plugins.Bundles.WorkspacePagesBundle();
        host.RegisterPlugins(bundle.Plugins);
        host.StartAsync().GetAwaiter().GetResult();

        return new MainViewModel(
            new PdfExportService(),
            new TemplateService(),
            new ProjectPersistenceService(),
            navigationRegistry: navReg,
            pluginHost: host);
    }

    [Fact]
    public void PdfReader_OpenPdfAndGoBack_MaintainsSidebarAndContentInSync()
    {
        using var mainVm = CreateMainViewModelWithWorkspacePages();

        // 1. Navigate to PDF Reader section
        mainVm.Home.SelectNavSectionCommand.Execute("PdfReader");
        Assert.Equal(HomeNavSection.PdfReader, mainVm.Home.SelectedNavSection);
        Assert.True(mainVm.Home.IsPdfReaderSection);
        Assert.False(mainVm.Home.IsHomeSection);

        var pdfReaderItem = mainVm.Home.DynamicNavigationItems.FirstOrDefault(i => i.Id == "PdfReader");
        Assert.NotNull(pdfReaderItem);
        Assert.True(pdfReaderItem.IsActive);

        var homeItem = mainVm.Home.DynamicNavigationItems.FirstOrDefault(i => i.Id == "Home");
        Assert.NotNull(homeItem);
        Assert.False(homeItem.IsActive);

        // 2. Open a PDF document in PDF Viewer Mode
        mainVm.PdfViewer.DocumentTitle = "SampleReport.pdf";
        mainVm.OpenInViewer("dummy/SampleReport.pdf");
        Assert.True(mainVm.IsPdfViewerVisible);
        Assert.False(mainVm.IsHomePageVisible);

        // 3. User clicks Back / Close (CloseViewerCommand in PdfViewer sends CloseViewerMessage)
        mainVm.PdfViewer.CloseViewerCommand.Execute(null);

        // 4. Assert: Should return to PDF Reader, with BOTH sidebar and content synchronized
        Assert.True(mainVm.IsHomePageVisible);
        Assert.False(mainVm.IsPdfViewerVisible);
        Assert.Equal(HomeNavSection.PdfReader, mainVm.Home.SelectedNavSection);
        Assert.True(mainVm.Home.IsPdfReaderSection);
        Assert.False(mainVm.Home.IsHomeSection);
        Assert.True(pdfReaderItem.IsActive, "Sidebar PDF Reader tab must remain active after returning from viewer");
        Assert.False(homeItem.IsActive, "Sidebar Home tab must not be active when returning to PDF Reader");
    }

    [Fact]
    public void HomeViewModel_DirectSelectedNavSectionAssignment_SynchronizesDynamicNavigationItems()
    {
        using var mainVm = CreateMainViewModelWithWorkspacePages();
        var home = mainVm.Home;

        // Directly setting SelectedNavSection to Help
        home.SelectedNavSection = HomeNavSection.Help;
        var helpItem = home.DynamicNavigationItems.FirstOrDefault(i => i.Id == "Help");
        var homeItem = home.DynamicNavigationItems.FirstOrDefault(i => i.Id == "Home");

        Assert.NotNull(helpItem);
        Assert.True(helpItem.IsActive);
        Assert.NotNull(homeItem);
        Assert.False(homeItem.IsActive);
        Assert.Equal("Help", home.ActiveNavDescriptor?.Id);

        // Directly setting SelectedNavSection to Settings
        home.SelectedNavSection = HomeNavSection.Settings;
        var settingsItem = home.DynamicNavigationItems.FirstOrDefault(i => i.Id == "Settings");

        Assert.NotNull(settingsItem);
        Assert.True(settingsItem.IsActive);
        Assert.False(helpItem.IsActive);
        Assert.Equal("Settings", home.ActiveNavDescriptor?.Id);
    }

    [Fact]
    public void MainViewModel_NavigateToHome_Explicit_SynchronizesBothContentAndSidebar()
    {
        using var mainVm = CreateMainViewModelWithWorkspacePages();
        mainVm.Home.SelectNavSectionCommand.Execute("PdfReader");
        Assert.Equal(HomeNavSection.PdfReader, mainVm.Home.SelectedNavSection);

        // Explicitly invoke NavigateToHome (e.g. from editor title bar home icon)
        mainVm.NavigateToHomeCommand.Execute(null);

        Assert.Equal(HomeNavSection.Home, mainVm.Home.SelectedNavSection);
        Assert.True(mainVm.Home.IsHomeSection);
        Assert.False(mainVm.Home.IsPdfReaderSection);

        var homeItem = mainVm.Home.DynamicNavigationItems.FirstOrDefault(i => i.Id == "Home");
        var pdfReaderItem = mainVm.Home.DynamicNavigationItems.FirstOrDefault(i => i.Id == "PdfReader");

        Assert.NotNull(homeItem);
        Assert.True(homeItem.IsActive);
        Assert.NotNull(pdfReaderItem);
        Assert.False(pdfReaderItem.IsActive);
    }

    [Fact]
    public void HomeViewModel_OpenHelpGuide_SynchronizesSidebarHighlight()
    {
        using var mainVm = CreateMainViewModelWithWorkspacePages();
        mainVm.Home.SelectNavSectionCommand.Execute("Home");

        mainVm.Home.OpenHelpGuideCommand.Execute(null);

        Assert.Equal(HomeNavSection.Help, mainVm.Home.SelectedNavSection);
        Assert.True(mainVm.Home.IsHelpSection);
        var helpItem = mainVm.Home.DynamicNavigationItems.FirstOrDefault(i => i.Id == "Help");
        Assert.NotNull(helpItem);
        Assert.True(helpItem.IsActive);
    }

    [Fact]
    public void DocumentEditor_StatusBar_AllowsOnlyDocumentEditorScopedCoreTelemetry()
    {
        // 1. External plugin widget -> must be rejected
        var externalWidget = new PdfEditorApp.Core.Plugins.Descriptors.StatusBarWidgetDescriptor
        {
            WidgetId = "com.frypdf.plugin.snake",
            IsExternal = true,
            Scope = PdfEditorApp.Core.Plugins.Descriptors.StatusBarScope.DocumentEditor
        };
        Assert.False(MainViewModel.IsAllowedInDocumentEditorStatusBar(externalWidget));

        // 2. Global scope widget (e.g. general app extension or unneeded baseline stub) -> must be rejected
        var globalWidget = new PdfEditorApp.Core.Plugins.Descriptors.StatusBarWidgetDescriptor
        {
            WidgetId = "frypdf.status.ready",
            IsExternal = false,
            Scope = PdfEditorApp.Core.Plugins.Descriptors.StatusBarScope.Global
        };
        Assert.False(MainViewModel.IsAllowedInDocumentEditorStatusBar(globalWidget));

        // 3. Core DocumentEditor scoped telemetry (e.g. Skia engine monitor) -> must be allowed
        var coreEditorWidget = new PdfEditorApp.Core.Plugins.Descriptors.StatusBarWidgetDescriptor
        {
            WidgetId = "frypdf.status.memory",
            IsExternal = false,
            Scope = PdfEditorApp.Core.Plugins.Descriptors.StatusBarScope.DocumentEditor
        };
        Assert.True(MainViewModel.IsAllowedInDocumentEditorStatusBar(coreEditorWidget));

        // 4. Null or empty widget -> must be rejected
        Assert.False(MainViewModel.IsAllowedInDocumentEditorStatusBar(null));
        Assert.False(MainViewModel.IsAllowedInDocumentEditorStatusBar(new PdfEditorApp.Core.Plugins.Descriptors.StatusBarWidgetDescriptor()));
    }

    [Fact]
    public void DocumentEditor_StatusBar_ExcludesExternalPluginsAndGlobalStubs()
    {
        var registry = new PdfEditorApp.Services.StatusBar.StatusBarRegistry(seedDefaults: false);

        // Register core document editor widget
        registry.RegisterWidget(new PdfEditorApp.Core.Plugins.Descriptors.StatusBarWidgetDescriptor
        {
            WidgetId = "frypdf.status.memory",
            Alignment = PdfEditorApp.Core.Plugins.Descriptors.StatusBarAlignment.Right,
            Scope = PdfEditorApp.Core.Plugins.Descriptors.StatusBarScope.DocumentEditor,
            IsExternal = false,
            Factory = _ => new StatusBarWidgetViewModel
            {
                WidgetId = "frypdf.status.memory",
                Label = "Skia 64-bit"
            }
        });

        // Register global stub widget (should be filtered out)
        registry.RegisterWidget(new PdfEditorApp.Core.Plugins.Descriptors.StatusBarWidgetDescriptor
        {
            WidgetId = "frypdf.status.pagestats",
            Alignment = PdfEditorApp.Core.Plugins.Descriptors.StatusBarAlignment.Left,
            Scope = PdfEditorApp.Core.Plugins.Descriptors.StatusBarScope.Global,
            IsExternal = false,
            Factory = _ => new StatusBarWidgetViewModel
            {
                WidgetId = "frypdf.status.pagestats",
                Label = "Pages"
            }
        });

        // Register an external plugin widget (should be filtered out)
        registry.RegisterWidget(new PdfEditorApp.Core.Plugins.Descriptors.StatusBarWidgetDescriptor
        {
            WidgetId = "frypdf.overlay.imageeditor",
            Alignment = PdfEditorApp.Core.Plugins.Descriptors.StatusBarAlignment.Left,
            Scope = PdfEditorApp.Core.Plugins.Descriptors.StatusBarScope.Global,
            IsExternal = true,
            Factory = _ => new StatusBarWidgetViewModel
            {
                WidgetId = "frypdf.overlay.imageeditor",
                Label = "Image Editor"
            }
        });

        var allowedLeft = registry.GetWidgets(PdfEditorApp.Core.Plugins.Descriptors.StatusBarAlignment.Left)
            .Where(MainViewModel.IsAllowedInDocumentEditorStatusBar)
            .ToList();

        var allowedRight = registry.GetWidgets(PdfEditorApp.Core.Plugins.Descriptors.StatusBarAlignment.Right)
            .Where(MainViewModel.IsAllowedInDocumentEditorStatusBar)
            .ToList();

        // Left has no editor-scoped widgets (page count and status message are native editor chips)
        Assert.Empty(allowedLeft);

        // Right only contains the core Skia memory monitor
        Assert.Single(allowedRight);
        Assert.Equal("frypdf.status.memory", allowedRight[0].WidgetId);
    }

    [Fact]
    public void ScopedPluginContext_AutomaticallyDetectsAndMarksExternalPlugins()
    {
        var parentContext = new PdfEditorApp.Core.Plugins.FryPluginContext();
        var externalPlugin = new SampleExternalPlugin();
        var scope = new PdfEditorApp.Core.Plugins.PluginScope(externalPlugin);
        var scopedContext = parentContext.CreateScopedContext(scope);

        var widget = new PdfEditorApp.Core.Plugins.Descriptors.StatusBarWidgetDescriptor
        {
            WidgetId = "com.sample.external.widget",
            // Even if the external plugin author attempted to declare DocumentEditor scope:
            Scope = PdfEditorApp.Core.Plugins.Descriptors.StatusBarScope.DocumentEditor
        };

        // Act: Plugin registers widget through its scoped context
        scopedContext.RegisterStatusBarWidget(widget);

        // Assert 1: The infrastructure automatically and tamper-proofly marks IsExternal = true
        Assert.True(widget.IsExternal, "ScopedPluginContext must automatically mark widgets from external assemblies as IsExternal=true");

        // Assert 2: MainViewModel document editor status bar rejects it immediately
        Assert.False(MainViewModel.IsAllowedInDocumentEditorStatusBar(widget), "Document editor status bar must reject all external plugin widgets");
    }

    private sealed class SampleExternalPlugin : PdfEditorApp.Core.Plugins.IFryPlugin
    {
        public string Id => "com.sample.external";
        public string Name => "Sample External Plugin";
        public Version Version => new(1, 0, 0);
        public IReadOnlyList<Type> RequiredServices => Array.Empty<Type>();
        public Task ApplyAsync(PdfEditorApp.Core.Plugins.IFryPluginContext ctx, CancellationToken ct = default) => Task.CompletedTask;
    }
}
