using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PdfEditorApp.Core.Plugins;
using PdfEditorApp.Core.Plugins.Descriptors;
using PdfEditorApp.Core.Plugins.Marketplace;
using PdfEditorApp.Core.Plugins.Profiles;
using PdfEditorApp.Plugins.Bundles;
using PdfEditorApp.Services.Plugins;
using PdfEditorApp.Services.Tools.Core;
using PdfEditorApp.ViewModels;
using Xunit;

namespace PdfEditorApp.Tests;

public class PluginsManagerPageTests
{
    private ServiceProvider CreateTestServices(string? testDir = null, HttpClient? httpClient = null)
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        if (testDir != null || httpClient != null)
        {
            testDir ??= Path.Combine(AppContext.BaseDirectory, $"frypdf_pm_{Guid.NewGuid():N}");
            var storePath = Path.Combine(testDir, "installed_plugins.json");
            services.AddSingleton<IInstalledPluginStore>(new FileInstalledPluginStore(storePath));
            services.AddSingleton<IPluginMarketplaceService>(sp =>
            {
                var host = sp.GetRequiredService<PluginHost>();
                var overlay = sp.GetService<IOverlayRegistry>();
                var store = sp.GetRequiredService<IInstalledPluginStore>();
                var pluginsDir = Path.Combine(testDir, "plugins");
                return httpClient == null
                    ? new PluginMarketplaceService(host, overlay, store, null, null, pluginsDir)
                    : new PluginMarketplaceService(host, overlay, store, httpClient, PluginMarketplaceService.DefaultRegistryBaseUrl, pluginsDir);
            });
        }
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task PluginsManager_LoadsAllInstalledPluginsAndCategorizesCorrectly()
    {
        var sp = CreateTestServices();
        var host = sp.GetRequiredService<PluginHost>();

        // Mount domain bundles
        var bundles = new IFryPluginBundle[]
        {
            new ToolsOrganizeBundle(),
            new ToolsSecurityBundle(),
            new ToolsConversionBundle(),
            new ToolsIntelligenceBundle(),
            new DataStudioBundle()
        };

        var profile = new PluginProfile
        {
            ProfileName = "desktop",
            Bundles = bundles.Select(b => b.Id).ToList()
        };
        ProfileLoader.ApplyProfile(profile, host, bundles);
        await host.StartAsync();

        var toolRegistry = sp.GetRequiredService<IPdfToolRegistry>();
        var marketplace = sp.GetRequiredService<IPluginMarketplaceService>();

        var vm = new PluginsManagerViewModel(host, marketplace, toolRegistry);
        await vm.LoadAllDataAsync();

        Assert.Equal(33, vm.InstalledCount);
        Assert.Equal(33, vm.ActiveInstalledCount);
        Assert.NotEmpty(vm.FilteredInstalledPlugins);

        // Verify Categories
        var categories = vm.FilteredInstalledPlugins.Select(p => p.Category).Distinct().ToList();
        Assert.Contains("Tools & Productivity", categories);

        // Verify specific plugins are present
        Assert.Contains(vm.FilteredInstalledPlugins, p => p.Id == "frypdf.tool.merge");
        Assert.Contains(vm.FilteredInstalledPlugins, p => p.Id == "frypdf.tool.compress");

        await host.StopAsync();
    }

    [Fact]
    public async Task PluginsManager_SearchAndFilter_WorksAcrossBothInstalledAndMarketplace()
    {
        var sp = CreateTestServices();
        var host = sp.GetRequiredService<PluginHost>();
        var bundles = new IFryPluginBundle[] { new ToolsOrganizeBundle(), new ToolsSecurityBundle() };
        var profile = new PluginProfile { ProfileName = "desktop", Bundles = bundles.Select(b => b.Id).ToList() };
        ProfileLoader.ApplyProfile(profile, host, bundles);
        await host.StartAsync();

        var marketplace = sp.GetRequiredService<IPluginMarketplaceService>();
        var vm = new PluginsManagerViewModel(host, marketplace);
        await vm.LoadAllDataAsync();

        // 1. Search Installed
        vm.SearchQuery = "merge";
        Assert.Single(vm.FilteredInstalledPlugins);
        Assert.Equal("frypdf.tool.merge", vm.FilteredInstalledPlugins[0].Id);

        // 2. Clear Search
        vm.SearchQuery = "";
        Assert.Equal(13, vm.FilteredInstalledPlugins.Count);

        // 3. Switch to Marketplace Tab: the external plugins FryPDF used to offer are FryWorld plugins now, so neither
        // FryPDF's retired builds nor the store's FryWorld builds are offered here.
        vm.SelectedTab = PluginsManagerTab.Marketplace;
        foreach (var query in new[] { "Snake", "Chess", "Music", "Scratchpad", "Image", "C#", "Telemetry", "Tic" })
        {
            vm.SearchQuery = query;
            Assert.DoesNotContain(vm.FilteredMarketplacePlugins, m =>
                m.Id.StartsWith("com.fryworld.", StringComparison.OrdinalIgnoreCase) ||
                m.Id.StartsWith("frypdf.overlay.", StringComparison.OrdinalIgnoreCase) ||
                m.Id is "com.frypdf.plugin.chess" or "com.frypdf.plugin.csharpeditor" or "com.frypdf.plugin.tictactoe");
        }
        vm.SearchQuery = "";

        await host.StopAsync();
    }

    [Fact]
    public async Task PluginsManager_Selection_PopulatesDetailViewModelAndContributions()
    {
        var sp = CreateTestServices();
        var host = sp.GetRequiredService<PluginHost>();
        var bundles = new IFryPluginBundle[] { new ToolsConversionBundle() };
        var profile = new PluginProfile { ProfileName = "desktop", Bundles = bundles.Select(b => b.Id).ToList() };
        ProfileLoader.ApplyProfile(profile, host, bundles);
        await host.StartAsync();

        var marketplace = sp.GetRequiredService<IPluginMarketplaceService>();
        var vm = new PluginsManagerViewModel(host, marketplace);
        await vm.LoadAllDataAsync();

        Assert.NotEmpty(vm.FilteredInstalledPlugins);
        var target = vm.FilteredInstalledPlugins.First(p => p.Id == "frypdf.tool.pdftoword");

        // Switch to Installed tab to select an installed plugin
        vm.SelectedTab = PluginsManagerTab.Installed;
        vm.SelectedInstalledPlugin = target;
        Assert.NotNull(vm.SelectedDetail);
        Assert.Equal("frypdf.tool.pdftoword", vm.SelectedDetail.Id);
        Assert.Equal(target.Name, vm.SelectedDetail.Name);
        Assert.True(vm.SelectedDetail.IsInstalled);
        Assert.True(vm.SelectedDetail.IsActive);
        Assert.NotEmpty(vm.SelectedDetail.ContributedFeatures);
        Assert.NotEmpty(vm.SelectedDetail.Highlights);
        Assert.NotEmpty(vm.SelectedDetail.Dependencies);

        // Test Sub-Tab navigation
        vm.SelectedDetail.SelectTab("Contributions");
        Assert.Equal(ExtensionDetailTab.Contributions, vm.SelectedDetail.SelectedTab);

        vm.SelectedDetail.SelectTab("Runtime");
        Assert.Equal(ExtensionDetailTab.Runtime, vm.SelectedDetail.SelectedTab);
        Assert.Contains("Active", vm.SelectedDetail.RuntimeStatus);

        await host.StopAsync();
    }

    [Fact]
    public async Task PluginsManager_DefaultTabIsStore_AndButtonsReflectStateAccurately()
    {
        using var httpClient = new HttpClient(new StubCatalogHandler()) { Timeout = TimeSpan.FromSeconds(5) };
        var sp = CreateTestServices(httpClient: httpClient);
        var host = sp.GetRequiredService<PluginHost>();
        var bundles = new IFryPluginBundle[] { new ToolsOrganizeBundle() };
        var profile = new PluginProfile { ProfileName = "desktop", Bundles = bundles.Select(b => b.Id).ToList() };
        ProfileLoader.ApplyProfile(profile, host, bundles);
        await host.StartAsync();

        var marketplace = sp.GetRequiredService<IPluginMarketplaceService>();
        var vm = new PluginsManagerViewModel(host, marketplace);
        await vm.LoadAllDataAsync();

        // 1. First tab is Store (Marketplace)
        Assert.Equal(PluginsManagerTab.Marketplace, vm.SelectedTab);
        Assert.NotNull(vm.SelectedDetail);

        // 2. Uninstalled Store Item button state verification
        var uninstalledItem = vm.FilteredMarketplacePlugins.FirstOrDefault(m => !m.IsInstalled);
        Assert.NotNull(uninstalledItem);
        vm.SelectedMarketplacePlugin = uninstalledItem;

        var detail = vm.SelectedDetail;
        Assert.NotNull(detail);
        Assert.False(detail.IsInstalled);
        Assert.True(detail.CanInstall);
        Assert.False(detail.CanUninstall); // Critical fix: NO delete button for uninstalled store items!
        Assert.False(detail.CanToggleActive);
        Assert.Equal("Available in Store", detail.RuntimeStatus);

        // 3. Installed System Built-in plugin button state verification
        vm.SelectedTab = PluginsManagerTab.Installed;
        var mergePlugin = vm.FilteredInstalledPlugins.First(p => p.Id == "frypdf.tool.merge");
        vm.SelectedInstalledPlugin = mergePlugin;

        detail = vm.SelectedDetail;
        Assert.NotNull(detail);
        Assert.True(detail.IsInstalled);
        Assert.True(detail.IsSystemBuiltIn);
        Assert.False(detail.CanUninstall); // System built-in cannot be uninstalled
        Assert.True(detail.CanToggleActive); // Can be enabled/disabled
        Assert.True(detail.IsActiveAndInstalled);

        await host.StopAsync();
    }

    [Fact]
    public async Task PluginsManager_InstallProgressAndStatus_UpdatesDuringInstallation()
    {
        var tempDir = Path.Combine(AppContext.BaseDirectory, $"frypdf_progress_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var httpClient = new HttpClient(new StubCatalogHandler()) { Timeout = TimeSpan.FromSeconds(5) };
            var sp = CreateTestServices(tempDir, httpClient);
            var host = sp.GetRequiredService<PluginHost>();
            var marketplace = sp.GetRequiredService<IPluginMarketplaceService>();

            var vm = new PluginsManagerViewModel(host, marketplace);
            await vm.LoadAllDataAsync();

            var sample = vm.FilteredMarketplacePlugins.ToList().First(m => m.Id == StubCatalogHandler.SampleId);
            vm.SelectedMarketplacePlugin = sample;

            // Before install
            Assert.False(vm.IsInstalling);
            Assert.Equal(0, vm.InstallProgress);
            Assert.False(vm.SelectedDetail!.IsInstalling);

            // Trigger install command
            var installTask = vm.InstallMarketplacePluginCommand.ExecuteAsync(StubCatalogHandler.SampleId);
            await installTask;

            // After install finishes
            Assert.False(vm.IsInstalling);
            Assert.True(marketplace.IsPluginInstalled(StubCatalogHandler.SampleId));

            // Re-select the installed sample in the marketplace
            vm.SelectedMarketplacePlugin = vm.FilteredMarketplacePlugins.ToList().First(m => m.Id == StubCatalogHandler.SampleId);
            var detail = vm.SelectedDetail!;
            Assert.True(detail.IsInstalled);
            Assert.True(detail.IsExternal);
            Assert.True(detail.CanUninstall); // External installed items CAN be uninstalled

            await marketplace.UninstallPluginAsync(StubCatalogHandler.SampleId);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }


    [Fact]
    public async Task WorkspacePagesBundle_RegistersPluginsPageDescriptorCorrectly()
    {
        var sp = CreateTestServices();
        var navRegistry = sp.GetRequiredService<PdfEditorApp.Core.Plugins.Descriptors.INavigationRegistry>();

        var bundle = new WorkspacePagesBundle();
        Assert.Contains(bundle.Plugins, p => p.Id == "frypdf.page.plugins");

        var ctx = sp.GetRequiredService<IFryPluginContext>();
        var plugin = bundle.Plugins.First(p => p.Id == "frypdf.page.plugins");
        await plugin.ApplyAsync(ctx);

        var item = navRegistry.GetItem("Plugins");
        Assert.NotNull(item);
        Assert.Equal("Plugins & Extensions", item.Title);
        Assert.Equal("Preferences", item.Group);
        Assert.Equal("PuzzleOutline", item.IconKind);
        Assert.NotNull(item.ViewFactory);
    }

    [Fact]
    public void HomeViewModel_NavigatesToPluginsSectionCorrectly()
    {
        var sp = CreateTestServices();
        var home = sp.GetRequiredService<HomeViewModel>();

        Assert.NotNull(home.PluginsManager);

        home.SelectNavSectionCommand.Execute("Plugins");

        Assert.True(home.IsPluginsSection);
        Assert.Equal(PdfEditorApp.Core.Models.HomeNavSection.Plugins, home.SelectedNavSection);
    }

    [Fact]
    public void PluginsManagerDetail_OverlayTypeDetection_And_LaunchCommand_WorkAsExpected()
    {
        // 1. Overlay detection by ID, tags, and known plugins
        Assert.True(PluginsManagerDetailViewModel.DetermineIsOverlayType("com.example.overlay.widget"));
        Assert.True(PluginsManagerDetailViewModel.DetermineIsOverlayType("custom.plugin", tags: new[] { "overlay", "utility" }));
        Assert.True(PluginsManagerDetailViewModel.DetermineIsOverlayType("custom.plugin", features: new[] { "Shell Overlay: Floating Card" }));

        // Non-overlay modules should return false
        Assert.False(PluginsManagerDetailViewModel.DetermineIsOverlayType("frypdf.tool.merge", tags: new[] { "pdf", "tool" }));
        Assert.False(PluginsManagerDetailViewModel.DetermineIsOverlayType("frypdf.element.text"));

        // 2. CanLaunch flag logic
        var installedOverlay = new MarketplacePluginItem
        {
            Id = "com.example.sample",
            Name = "Sample Overlay",
            Publisher = "Code Fry Dev",
            Version = "1.0.0",
            Description = "Sample shell overlay",
            Category = "UI & Extensions",
            Tags = new[] { "game", "sample", "overlay" },
            Status = MarketplacePluginStatus.Installed
        };

        var detailVm = PluginsManagerDetailViewModel.FromMarketplaceItem(installedOverlay);
        Assert.True(detailVm.IsOverlayType);
        Assert.True(detailVm.IsInstalled);
        Assert.True(detailVm.CanLaunch);

        // Non-installed overlay cannot launch yet
        installedOverlay.Status = MarketplacePluginStatus.Available;
        var uninstalledDetail = PluginsManagerDetailViewModel.FromMarketplaceItem(installedOverlay);
        Assert.True(uninstalledDetail.IsOverlayType);
        Assert.False(uninstalledDetail.IsInstalled);
        Assert.False(uninstalledDetail.CanLaunch);

        // 3. LaunchCommand invokes callback
        string launchedId = string.Empty;
        detailVm.LaunchCallback = id =>
        {
            launchedId = id;
            return Task.CompletedTask;
        };

        detailVm.LaunchCommand.Execute(null);
        Assert.Equal("com.example.sample", launchedId);
    }

    /// <summary>
    /// Serves a one-entry catalog with an external FryPDF plugin that has no downloadable package, so install goes
    /// through the local install path without reaching the network.
    /// </summary>
    private sealed class StubCatalogHandler : HttpMessageHandler
    {
        public const string SampleId = "com.frypdf.test.sample";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.ToString().EndsWith("catalog.json", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""
                        [ { "id": "{{SampleId}}", "name": "Sample Extension", "publisher": "Code Fry Dev", "version": "1.0.0",
                            "category": "UI & Extensions", "description": "Stub catalog entry for tests.", "formattedSize": "10 KB" } ]
                        """, System.Text.Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
