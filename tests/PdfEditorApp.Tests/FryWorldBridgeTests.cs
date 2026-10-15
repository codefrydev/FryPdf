using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PdfEditorApp.Core.Plugins.FryWorldInterop;
using PdfEditorApp.Core.Plugins.Settings;
using PdfEditorApp.Plugins.FryWorldBridge;
using Xunit;
using Fp = PdfEditorApp.Core.Plugins.Descriptors;
using Fw = FryWorld.Sdk.Descriptors;
using FwPlugins = FryWorld.Sdk.Plugins;
using FwServices = FryWorld.Sdk.Services;

namespace PdfEditorApp.Tests;

/// <summary>
/// FryWorld.Sdk plugins in FryPDF: they mount in FryPDF's FryWorld plugin host, appear in FryPDF's own registries
/// (and disappear when unmounted), get FryPDF's services in FryWorld's shape, and can use FryPDF's PDF features.
/// Uses FryPDF's real service registrations.
/// </summary>
public sealed class FryWorldBridgeTests : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly FwPlugins.PluginHost _fryWorld;

    public FryWorldBridgeTests()
    {
        var collection = new ServiceCollection();
        App.ConfigureServices(collection);
        _services = collection.BuildServiceProvider();

        _fryWorld = _services.GetRequiredService<FwPlugins.PluginHost>();
        // No UI thread in these tests: raise registry events inline instead of posting them to the dispatcher.
        ((FwPlugins.FryPluginContext)_fryWorld.Context).NotificationContext = null;
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    /// <summary>A FryWorld plugin that contributes one of everything, plus a PDF importer.</summary>
    private sealed class StudioPlugin : FwPlugins.FryPluginBase
    {
        public override string Id => "test.fryworld.studio";
        public override string Name => "FryWorld Studio";

        public override Task ApplyAsync(FwPlugins.IFryPluginContext ctx, CancellationToken ct = default)
        {
            ctx.RegisterNavigationItem(new Fw.NavigationItemDescriptor { Id = "fw.page", Title = "FW Page", Category = "Extensions", ViewFactory = () => new TextBlock() });
            ctx.RegisterOverlay(new Fw.OverlayDescriptor { Id = "fw.overlay", Title = "FW Overlay", ChromeMode = Fw.OverlayChromeMode.FloatingPill, ViewFactory = () => new TextBlock() });
            ctx.RegisterCommand(new Fw.CommandPaletteDescriptor { Id = "fw.command", Title = "FW Command", Command = new RelayCommand(() => CommandRuns++) });
            ctx.RegisterSidebarTab(new Fw.SidebarTabDescriptor { Id = "fw.sidebar", Title = "FW Sidebar", ViewFactory = () => new TextBlock() });
            ctx.RegisterDialog(new Fw.DialogDescriptor { Id = "fw.dialog", Title = "FW Dialog", ViewFactory = () => new TextBlock() });
            ctx.RegisterInspectorSection(new Fw.InspectorSectionDescriptor { Id = "fw.inspector", Title = "FW Inspector", ViewFactory = () => new TextBlock() });
            ctx.RegisterShortcut(new Fw.ShortcutDescriptor { Id = "fw.shortcut", Description = "FW Shortcut", KeyGesture = "Ctrl+Alt+F" });
            ctx.RegisterRibbonTab(new Fw.RibbonTabDescriptor
            {
                Id = "fw.ribbon",
                Header = "FW Tab",
                Actions = { new Fw.RibbonActionDescriptor { Id = "fw.ribbon.action", Label = "Go", GroupId = "tools" } }
            });

            IsFryPdf = ctx.IsFryPdfHost();
            ctx.RegisterDocumentImporter(new FakeImporter());
            Settings = FwPlugins.FryPluginContextExtensions.GetPluginSettings(ctx);
            return Task.CompletedTask;
        }

        public int CommandRuns { get; private set; }
        public bool IsFryPdf { get; private set; }
        public FwServices.IPluginSettings? Settings { get; private set; }
    }

    private sealed class FakeImporter : Fp.IDocumentImporter
    {
        public string ImporterId => "test.fryworld.importer";
        public string DisplayName => "FryWorld test importer";
        public IReadOnlyList<string> SupportedExtensions => new[] { ".fwtest" };
        public Task<PdfEditorApp.Core.Models.PdfDocumentModel> ImportAsync(System.IO.Stream stream, string fileName, string? password = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task FryWorldPlugin_AppearsInFryPdfsRegistries_AndLeavesThemOnUnmount()
    {
        var bridge = _services.GetRequiredService<FryWorldPluginBridge>();
        bridge.Attach();

        var plugin = new StudioPlugin();
        await _fryWorld.MountPluginAsync(plugin);
        Assert.Equal(FwPlugins.PluginLifecycleState.Active, _fryWorld.GetPluginState(plugin.Id));

        var navigation = _services.GetRequiredService<Fp.INavigationRegistry>();
        var overlays = _services.GetRequiredService<Fp.IOverlayRegistry>();
        var palette = _services.GetRequiredService<Fp.ICommandPaletteRegistry>();
        var sidebars = _services.GetRequiredService<Fp.ISidebarRegistry>();
        var dialogs = _services.GetRequiredService<Fp.IDialogRegistry>();
        var inspectors = _services.GetRequiredService<Fp.IInspectorRegistry>();
        var shortcuts = _services.GetRequiredService<Fp.IShortcutRegistry>();
        var ribbon = _services.GetRequiredService<Fp.IRibbonRegistry>();
        var importers = _services.GetRequiredService<Fp.IDocumentImporterRegistry>();

        var page = navigation.GetItem("fw.page");
        Assert.NotNull(page);
        Assert.Equal(Fp.NavigationDisplayMode.FullViewport, page!.DisplayMode);
        Assert.Equal(Fp.OverlayChromeMode.FloatingPill, overlays.GetOverlay("fw.overlay")!.ChromeMode);
        Assert.NotNull(sidebars.GetTab("fw.sidebar"));
        Assert.NotNull(dialogs.GetDialog("fw.dialog"));
        Assert.Contains(inspectors.GetAllSections(), s => s.SectionId == "fw.inspector");
        Assert.NotNull(shortcuts.GetShortcut("fw.shortcut"));
        Assert.Contains(ribbon.GetAllTabs(), t => t.Id == "fw.ribbon");
        Assert.Contains(ribbon.GetAllActions(), a => a.Id == "fw.ribbon.action");
        Assert.True(plugin.IsFryPdf);
        Assert.NotNull(importers.GetImporter("test.fryworld.importer"));

        // FryPDF's palette runs the FryWorld command.
        var command = palette.GetAllCommands().Single(c => c.Id == "fw.command");
        command.Action!(_services);
        Assert.Equal(1, plugin.CommandRuns);

        await _fryWorld.UnmountPluginAsync(plugin.Id);

        Assert.Null(navigation.GetItem("fw.page"));
        Assert.Null(overlays.GetOverlay("fw.overlay"));
        Assert.DoesNotContain(palette.GetAllCommands(), c => c.Id == "fw.command");
        Assert.Null(sidebars.GetTab("fw.sidebar"));
        Assert.Null(dialogs.GetDialog("fw.dialog"));
        Assert.DoesNotContain(inspectors.GetAllSections(), s => s.SectionId == "fw.inspector");
        Assert.Null(shortcuts.GetShortcut("fw.shortcut"));
        Assert.DoesNotContain(ribbon.GetAllActions(), a => a.Id == "fw.ribbon.action");
        Assert.Null(importers.GetImporter("test.fryworld.importer"));
    }

    [Fact]
    public async Task FryWorldPlugin_GetsFryPdfsServicesInFryWorldsShape()
    {
        _services.GetRequiredService<FryWorldPluginBridge>().Attach();
        var plugin = new StudioPlugin();
        await _fryWorld.MountPluginAsync(plugin);

        // Settings land in FryPDF's plugin settings store.
        plugin.Settings!.Set("Volume", 7);
        Assert.Equal(7, _services.GetRequiredService<IPluginSettingsStore>().GetSetting(plugin.Id, "Volume", 0));

        // The shell drives FryPDF's overlay registry.
        var overlays = _services.GetRequiredService<Fp.IOverlayRegistry>();
        var shell = new FryPdfShellService(
            _services.GetRequiredService<Fp.INavigationRegistry>(), overlays, _services.GetRequiredService<Fp.IDialogRegistry>(),
            onUiThread: action => action());

        Assert.True(shell.ShowOverlay("fw.overlay"));
        Assert.True(shell.IsOverlayVisible("fw.overlay"));
        Assert.True(shell.HideOverlay("fw.overlay"));
        Assert.False(shell.IsOverlayVisible("fw.overlay"));
        Assert.False(shell.ShowOverlay("no.such.overlay"));
        Assert.False(shell.NavigateTo("no.such.page"));

        Assert.IsType<FryPdfShellService>(_fryWorld.Context.GetService<FwServices.IShellService>());
        Assert.IsType<FryPdfThemeAdapter>(_fryWorld.Context.GetService<FwServices.IThemeService>());
    }
}
