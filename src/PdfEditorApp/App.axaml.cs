using PdfEditorApp.Services.Tools.Core;
using PdfEditorApp.Services.Tools.Organize;
using PdfEditorApp.Services.Tools.Security;
using PdfEditorApp.Services.Tools.Conversion;
using PdfEditorApp.Services.Tools.Intelligence;
using PdfEditorApp.ViewModels.Tools.Core;
using PdfEditorApp.ViewModels.Tools.Organize;
using PdfEditorApp.ViewModels.Tools.Security;
using PdfEditorApp.ViewModels.Tools.Conversion;
using PdfEditorApp.ViewModels.Tools.Intelligence;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PdfEditorApp.Core.Plugins;
using PdfEditorApp.Core.Plugins.Descriptors;
using PdfEditorApp.Services;
using PdfEditorApp.Services.Canvas;
using PdfEditorApp.Services.Ribbon;
using PdfEditorApp.Services.AI;
using PdfEditorApp.Services.Data;
using PdfEditorApp.Services.Export;
using PdfEditorApp.Services.Import;
using PdfEditorApp.Services.Inspector;
using PdfEditorApp.Services.Ocr;
using PdfEditorApp.Services.StatusBar;
using PdfEditorApp.ViewModels;
using PdfEditorApp.Views;

namespace PdfEditorApp;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        Name = "FryPDF";
        // Touch the singleton to install the FryPdfTraceListener at process start so that
        // all Debug.WriteLine calls from plugin loading, path resolution, and CDN fetching
        // are captured in the in-process diagnostic log buffer from the very first frame.
        _ = PdfEditorApp.Services.AppLogService.Instance;

        // Raw input (keyboard/mouse) is dispatched synchronously via Dispatcher.Send, which
        // wraps the call in try/catch and offers this event as the only way to intercept it.
        // Without a handler here, any exception thrown while handling a key/pointer event
        // anywhere in the app - including inside third-party controls we don't own, such as
        // AvaloniaEdit's CompletionWindow - is rethrown unhandled and aborts the whole process.
        // Marking it Handled logs and recovers instead of losing the user's in-progress work.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Debug.WriteLine($"[Dispatcher.UnhandledException] {e.Exception}");
            e.Handled = true;
        };

        LiveChartsCore.LiveCharts.Configure(config => LiveChartsCore.SkiaSharpView.LiveChartsSkiaSharp.UseDefaults(config));
        AvaloniaXamlLoader.Load(this);
    }

    private void AboutMenuItem_OnClick(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.DataContext is MainViewModel mainVm)
        {
            mainVm.OpenAboutDialogCommand.Execute(null);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        Services = serviceCollection.BuildServiceProvider();

        var themeService = Services.GetRequiredService<IThemeService>();
        themeService.Initialize();

        InitializePluginSystem();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = Services.GetRequiredService<MainViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVm,
            };
            desktop.ShutdownRequested += (_, _) => ShutdownServices();

            // Reports any stall on the UI thread, whatever its cause. The per-operation
            // stopwatches elsewhere all stop before Avalonia's layout/render pass, so this is
            // the only thing that can see a freeze that happens during rendering.
            _uiThreadWatchdog = new UiThreadWatchdog();

            // Professional startup setup orchestration (Google Material 3 Expressive)
            _ = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var loadingService = Services.GetService<PdfEditorApp.Core.Plugins.Loading.ILoadingProgressService>();
                PdfEditorApp.Core.Plugins.Loading.ILoadingProgressHandle? handle = null;

                if (loadingService != null)
                {
                    handle = loadingService.Show(new PdfEditorApp.Core.Plugins.Loading.LoadingProgressOptions
                    {
                        Title = "Starting FryPDF Studio",
                        Category = "STARTUP & INITIALIZATION",
                        StatusMessage = "Mounting 17 microkernel plugin bundles...",
                        PipelinePhases = new[] { "Theme", "Plugins", "Engine", "Workspace" },
                        ActivePhaseIndex = 1,
                        IsCancellable = false,
                        ProgressPercent = 30.0
                    });
                }

                try
                {
                    await Task.Delay(120);
                    handle?.UpdateStatus("Initializing Skia hardware accelerated canvas...", progressPercent: 65.0, activePhaseIndex: 2);

                    var marketplace = Services.GetService<PdfEditorApp.Core.Plugins.Marketplace.IPluginMarketplaceService>();
                    if (marketplace != null)
                    {
                        await marketplace.InitializeAsync();
                    }

                    handle?.UpdateStatus("Setting up workspace & dashboard...", progressPercent: 90.0, activePhaseIndex: 3);
                    await Task.Delay(120);
                    handle?.UpdateStatus("Ready!", progressPercent: 100.0, activePhaseIndex: 3);
                    await Task.Delay(80);
                }
                catch (Exception ex)
                {
                    AppLogService.Instance.LogWarning("App", "Startup initialization warning", ex);
                }
                finally
                {
                    handle?.Dispose();
                }
            }, DispatcherPriority.Loaded);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Tears down application services on shutdown.
    /// </summary>
    private static UiThreadWatchdog? _uiThreadWatchdog;

    private static void ShutdownServices()
    {
        _uiThreadWatchdog?.Dispose();
        _uiThreadWatchdog = null;

        try
        {
            if (Services is IAsyncDisposable asyncDisposable)
            {
                // Run disposal off the UI thread with a bounded timeout (2 seconds)
                // so the application exits cleanly and never deadlocks or hangs.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var teardownTask = Task.Run(async () =>
                {
                    try
                    {
                        await asyncDisposable.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        AppLogService.Instance.LogWarning("App", "Background teardown error", ex);
                    }
                }, cts.Token);

                teardownTask.Wait(TimeSpan.FromSeconds(2));
            }
            else if (Services is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogWarning("App", "Error disposing application services on shutdown", ex);
        }
        finally
        {
            AppLogService.Instance.Dispose();
        }
    }

    private static void InitializePluginSystem()
    {
        try
        {
            PageViewModel.DefaultElementService = Services.GetRequiredService<ICanvasElementService>();
            var host = Services.GetRequiredService<PluginHost>();

            var profilePath = System.IO.Path.Combine(AppContext.BaseDirectory, "profiles", "desktop.profile.json");
            if (!System.IO.File.Exists(profilePath))
            {
                profilePath = "profiles/desktop.profile.json";
            }

            PdfEditorApp.Core.Plugins.Profiles.PluginProfile profile;
            if (System.IO.File.Exists(profilePath))
            {
                profile = PdfEditorApp.Core.Plugins.Profiles.ProfileLoader.LoadFromFile(profilePath);
            }
            else
            {
                profile = new PdfEditorApp.Core.Plugins.Profiles.PluginProfile { ProfileName = "desktop" };
            }

            var availableBundles = new PdfEditorApp.Core.Plugins.Profiles.IFryPluginBundle[]
            {
                new PdfEditorApp.Plugins.Bundles.ToolsOrganizeBundle(),
                new PdfEditorApp.Plugins.Bundles.ToolsSecurityBundle(),
                new PdfEditorApp.Plugins.Bundles.ToolsConversionBundle(),
                new PdfEditorApp.Plugins.Bundles.ToolsIntelligenceBundle(),
                new PdfEditorApp.Plugins.Bundles.DataStudioBundle(),
                new PdfEditorApp.Plugins.Bundles.CanvasElementsBundle(),
                new PdfEditorApp.Plugins.Bundles.DocumentIoBundle(),
                new PdfEditorApp.Plugins.Bundles.AiProvidersBundle(),
                new PdfEditorApp.Plugins.Bundles.OcrEnginesBundle(),
                new PdfEditorApp.Plugins.Bundles.StandardTemplatesBundle(),
                new PdfEditorApp.Plugins.Bundles.StatusBarBundle(),
                new PdfEditorApp.Plugins.Bundles.InspectorBundle(),
                new PdfEditorApp.Plugins.Bundles.CommandPaletteBundle(),
                new PdfEditorApp.Plugins.Bundles.WorkspacePagesBundle(),
                new PdfEditorApp.Plugins.Bundles.DialogsBundle(),
                new PdfEditorApp.Plugins.Bundles.EditorSidebarsBundle(),
                new PdfEditorApp.Plugins.Bundles.ShellOverlaysBundle()
            };

            PdfEditorApp.Core.Plugins.Profiles.ProfileLoader.ApplyProfile(profile, host, availableBundles);

            // FryPdfPaths.PluginsDirectory is MSIX-safe: on a Microsoft Store install it
            // resolves to %LocalAppData%\FryPDF\plugins\ instead of the read-only WindowsApps dir.
            var userLocalPlugins = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FryPDF", "plugins");

            var directoriesToScan = new System.Collections.Generic.HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                FryPdfPaths.PluginsDirectory,
                System.IO.Path.Combine(AppContext.BaseDirectory, "plugins"),
                userLocalPlugins
            };

            // Each directory and each plugin is isolated: a throw while scanning the first
            // directory used to skip every remaining directory *and* host.StartAsync(),
            // leaving the app with no plugins at all and no visible error.
            foreach (var dir in directoriesToScan)
            {
                if (!System.IO.Directory.Exists(dir)) continue;

                try
                {
                    var externalPackages = PdfEditorApp.Plugins.Loader.PluginAssemblyLoader.DiscoverAndLoadDirectory(dir);
                    foreach (var pkg in externalPackages)
                    {
                        foreach (var plugin in pkg.Plugins)
                        {
                            try
                            {
                                host.RegisterPlugin(plugin);
                            }
                            catch (Exception ex)
                            {
                                AppLogService.Instance.LogWarning("App",
                                    $"Could not register plugin '{plugin.Id}' from '{dir}'", ex);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogService.Instance.LogWarning("App", $"Could not scan plugin directory '{dir}'", ex);
                }
            }

            try
            {
                // Plugin bundles must be mounted before MainWindow is constructed, so this
                // has to complete synchronously. Dispatching through Task.Run first means no
                // continuation is captured back to the dispatcher — which has not started
                // its loop yet, so a captured continuation would deadlock.
                Task.Run(() => host.StartAsync()).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AppLogService.Instance.LogError("App", "Plugin host failed to start", ex);
            }
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogError("App", "Plugin system initialization failed", ex);
        }
    }

    public static void ConfigureServices(IServiceCollection services)
    {
        // Core Services
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IUiSettingsService, UiSettingsService>();
        services.AddSingleton<IPdfExportService>(sp => new PdfExportService(sp.GetService<IFryPluginContext>()));
        services.AddSingleton<IPdfImportService>(sp => new PdfImportService(sp.GetRequiredService<IDocumentImporterRegistry>(), sp.GetService<IFryPluginContext>()));
        services.AddSingleton<IProjectPersistenceService>(sp => new ProjectPersistenceService(sp.GetRequiredService<IPdfImportService>(), sp.GetService<ICanvasElementRegistry>()));
        services.AddSingleton<ITemplateService, TemplateService>();
        services.AddSingleton<IDocumentAuditService, DocumentAuditService>();
        services.AddSingleton<IDocumentCompareService, DocumentCompareService>();
        services.AddSingleton<ISignatureService, SignatureService>();
        services.AddSingleton<ISmartPlacementService, SmartPlacementService>();
        services.AddSingleton<IRecentDocumentsService, RecentDocumentsService>();
        services.AddSingleton<IPageOrganizerService, PageOrganizerService>();
        services.AddSingleton<IHelpGuideService, HelpGuideService>();
        services.AddTransient<IUndoRedoService, UndoRedoService>();

        // Plugin Infrastructure (Everything is a Plugin)
        services.AddSingleton<CanvasElementRegistry>(sp => new CanvasElementRegistry(sp, seedBuiltIns: false));
        services.AddSingleton<ICanvasElementService>(sp => sp.GetRequiredService<CanvasElementRegistry>());
        services.AddSingleton<ICanvasElementRegistry>(sp => sp.GetRequiredService<CanvasElementRegistry>());
        services.AddSingleton<RibbonRegistry>();
        services.AddSingleton<IRibbonRegistry>(sp => sp.GetRequiredService<RibbonRegistry>());

        services.AddSingleton<DocumentImporterRegistry>();
        services.AddSingleton<IDocumentImporterRegistry>(sp => sp.GetRequiredService<DocumentImporterRegistry>());
        services.AddSingleton<DocumentExporterRegistry>();
        services.AddSingleton<IDocumentExporterRegistry>(sp => sp.GetRequiredService<DocumentExporterRegistry>());
        services.AddSingleton<InspectorRegistry>(sp => new InspectorRegistry(seedDefaults: false));
        services.AddSingleton<IInspectorRegistry>(sp => sp.GetRequiredService<InspectorRegistry>());
        services.AddSingleton<AiProviderRegistry>();
        services.AddSingleton<IAiProviderRegistry>(sp => sp.GetRequiredService<AiProviderRegistry>());
        services.AddSingleton<ITemplateRegistry>(sp => (ITemplateRegistry)sp.GetRequiredService<ITemplateService>());
        services.AddSingleton<OcrEngineRegistry>();
        services.AddSingleton<IOcrEngineRegistry>(sp => sp.GetRequiredService<OcrEngineRegistry>());
        services.AddSingleton<DataConnectorRegistry>();
        services.AddSingleton<IDataConnectorRegistry>(sp => sp.GetRequiredService<DataConnectorRegistry>());
        services.AddSingleton<StatusBarRegistry>(sp => new StatusBarRegistry(seedDefaults: false));
        services.AddSingleton<IStatusBarRegistry>(sp => sp.GetRequiredService<StatusBarRegistry>());
        services.AddSingleton<PdfEditorApp.Services.Palette.CommandPaletteRegistry>();
        services.AddSingleton<ICommandPaletteRegistry>(sp => sp.GetRequiredService<PdfEditorApp.Services.Palette.CommandPaletteRegistry>());
        services.AddSingleton<PdfEditorApp.Services.Navigation.NavigationRegistry>();
        services.AddSingleton<INavigationRegistry>(sp => sp.GetRequiredService<PdfEditorApp.Services.Navigation.NavigationRegistry>());
        services.AddSingleton<PdfEditorApp.Services.Dialogs.DialogRegistry>();
        services.AddSingleton<IDialogRegistry>(sp => sp.GetRequiredService<PdfEditorApp.Services.Dialogs.DialogRegistry>());
        services.AddSingleton<PdfEditorApp.Services.Sidebar.SidebarRegistry>();
        services.AddSingleton<ISidebarRegistry>(sp => sp.GetRequiredService<PdfEditorApp.Services.Sidebar.SidebarRegistry>());
        services.AddSingleton<PdfEditorApp.Services.Overlays.OverlayRegistry>();
        services.AddSingleton<PdfEditorApp.Core.Plugins.Descriptors.IOverlayRegistry>(sp => sp.GetRequiredService<PdfEditorApp.Services.Overlays.OverlayRegistry>());
        services.AddSingleton<PdfEditorApp.Services.Shortcuts.ShortcutRegistry>();
        services.AddSingleton<PdfEditorApp.Core.Plugins.Descriptors.IShortcutRegistry>(sp => sp.GetRequiredService<PdfEditorApp.Services.Shortcuts.ShortcutRegistry>());
        services.AddSingleton<PdfEditorApp.Core.Plugins.Settings.IPluginSettingsStore, PdfEditorApp.Core.Plugins.Settings.FilePluginSettingsStore>();
        services.AddSingleton<PdfEditorApp.Core.Plugins.Loading.ILoadingProgressService, PdfEditorApp.Core.Plugins.Loading.LoadingProgressService>();
        services.AddSingleton<CommunityToolkit.Mvvm.Messaging.IMessenger>(CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default);

        // Lets a plugin declare work with a hard deadline (e.g. an audio callback) so the host
        // can tune GC latency while it runs. Resolved by plugins through the IServiceProvider
        // handed to their view factories.
        services.AddSingleton<PdfEditorApp.Core.Plugins.Realtime.IRealtimeWorkCoordinator, RealtimeWorkCoordinator>();
        services.AddSingleton<PdfEditorApp.Core.Plugins.Marketplace.IInstalledPluginStore, PdfEditorApp.Core.Plugins.Marketplace.FileInstalledPluginStore>();


        services.AddSingleton<FryPluginContext>(sp => new FryPluginContext(sp));
        services.AddSingleton<IFryPluginContext>(sp => sp.GetRequiredService<FryPluginContext>());
        services.AddSingleton<PluginHost>(sp => new PluginHost(sp.GetRequiredService<FryPluginContext>()));
        services.AddSingleton<PdfEditorApp.Core.Plugins.Marketplace.IPluginMarketplaceService, PdfEditorApp.Services.Plugins.PluginMarketplaceService>();

        // PDF Tools Platform Services
        services.AddSingleton<IQuestPdfOperationsEngine, QuestPdfOperationsEngine>();
        services.AddSingleton<IPdfToolRegistry>(sp => new PdfToolRegistry(sp.GetRequiredService<IFryPluginContext>(), seedDefaults: false));
        services.AddSingleton<IPdfPageService, PdfPageService>();
        services.AddSingleton<IPdfOptimizationService, PdfOptimizationService>();
        services.AddSingleton<IPdfSecurityService, PdfSecurityService>();
        services.AddSingleton<IPdfConversionService, PdfConversionService>();
        services.AddSingleton<IPdfOcrService, PdfOcrService>();
        services.AddSingleton<IPdfFormService, PdfFormService>();
        services.AddSingleton<IAiDocumentService, AiDocumentService>();
        services.AddSingleton<IDocumentTranslationService, DocumentTranslationService>();
        services.AddSingleton<IPdfWorkflowEngine, PdfWorkflowEngine>();
        services.AddSingleton<IPdfDocumentOperationsService>(sp =>
            new PdfDocumentOperationsService(
                sp.GetRequiredService<IPdfToolRegistry>(),
                sp.GetRequiredService<IPdfPageService>(),
                sp.GetRequiredService<IPdfOptimizationService>(),
                sp.GetRequiredService<IPdfSecurityService>(),
                sp.GetRequiredService<IPdfConversionService>(),
                sp.GetRequiredService<IPdfOcrService>(),
                sp.GetRequiredService<IPdfFormService>(),
                sp.GetRequiredService<IAiDocumentService>(),
                sp.GetRequiredService<IDocumentTranslationService>(),
                sp.GetRequiredService<IPdfWorkflowEngine>(),
                sp.GetService<IFryPluginContext>()));
        services.AddSingleton<IPdfToolViewModelFactory>(sp =>
            new PdfToolViewModelFactory(
                sp.GetRequiredService<IPdfDocumentOperationsService>(),
                sp.GetRequiredService<IPdfToolRegistry>(),
                sp));

        services.AddSingleton<PdfEditorApp.Core.Data.IDataSourceService, PdfEditorApp.Core.Data.DataSourceService>();
        services.AddSingleton<PdfEditorApp.Core.Data.IDataBindingService, PdfEditorApp.Core.Data.DataBindingService>();
        services.AddSingleton<PdfEditorApp.Core.Data.IDataMergeEngine, PdfEditorApp.Core.Data.DataMergeEngine>();
        services.AddSingleton<PdfEditorApp.Core.Data.IBatchPdfGenerator, PdfEditorApp.Services.BatchPdfGeneratorService>();

        // ViewModels
        services.AddTransient<PdfEditorApp.ViewModels.DataStudio.DataStudioViewModel>();
        services.AddTransient<PdfEditorApp.ViewModels.BatchGeneration.BatchGenerationViewModel>();
        services.AddTransient<InspectorViewModel>();
        services.AddTransient<PdfViewerViewModel>();
        services.AddTransient<PdfToolRunnerViewModel>();
        services.AddTransient<WorkflowBuilderViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<PluginsManagerViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddSingleton<PdfEditorApp.ViewModels.LoadingProgressViewModel>();
        services.AddTransient<MainViewModel>();

        // Individual Tool ViewModels (resolvable directly or via IPdfToolViewModelFactory)
        services.AddTransient<MergePdfToolViewModel>();
        services.AddTransient<SplitPdfToolViewModel>();
        services.AddTransient<RotatePdfToolViewModel>();
        services.AddTransient<OrganizePdfToolViewModel>();
        services.AddTransient<CropPdfToolViewModel>();
        services.AddTransient<PageNumbersToolViewModel>();

        services.AddTransient<CompressPdfToolViewModel>();
        services.AddTransient<RepairPdfToolViewModel>();
        services.AddTransient<ProtectPdfToolViewModel>();
        services.AddTransient<UnlockPdfToolViewModel>();
        services.AddTransient<SignPdfToolViewModel>();
        services.AddTransient<RedactPdfToolViewModel>();
        services.AddTransient<WatermarkToolViewModel>();

        services.AddTransient<PdfToWordToolViewModel>();
        services.AddTransient<WordToPdfToolViewModel>();
        services.AddTransient<PdfToExcelToolViewModel>();
        services.AddTransient<ExcelToPdfToolViewModel>();
        services.AddTransient<PdfToPowerPointToolViewModel>();
        services.AddTransient<PowerPointToPdfToolViewModel>();
        services.AddTransient<PdfToJpgToolViewModel>();
        services.AddTransient<JpgToPdfToolViewModel>();
        services.AddTransient<HtmlToPdfToolViewModel>();
        services.AddTransient<ScanToPdfToolViewModel>();
        services.AddTransient<PdfToMarkdownToolViewModel>();
        services.AddTransient<PdfToPdfAToolViewModel>();

        services.AddTransient<OcrPdfToolViewModel>();
        services.AddTransient<ComparePdfToolViewModel>();
        services.AddTransient<EditPdfToolViewModel>();
        services.AddTransient<PdfFormsToolViewModel>();
        services.AddTransient<AiSummarizerToolViewModel>();
        services.AddTransient<TranslatePdfToolViewModel>();
    }
}