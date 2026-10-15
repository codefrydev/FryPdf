using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PdfEditorApp.Core.Plugins;
using PdfEditorApp.Core.Plugins.Descriptors;
using PdfEditorApp.Core.Plugins.Manifests;
using PdfEditorApp.Core.Plugins.Marketplace;
using PdfEditorApp.Core.Plugins.Profiles;
using PdfEditorApp.Core.Plugins.Settings;
using PdfEditorApp.Plugins.Bundles;
using PdfEditorApp.Services;
using PdfEditorApp.Services.Overlays;
using PdfEditorApp.Services.Plugins;
using Xunit;

namespace PdfEditorApp.Tests;

public class TestMarketplacePlugin : IFryPlugin
{
    public string Id => "com.frypdf.test.marketplace";
    public string Name => "Test Marketplace Plugin";
    public Version Version => new(1, 0, 0);
    public IReadOnlyList<Type> RequiredServices => Array.Empty<Type>();
    public IReadOnlyList<Type> ProvidedServices => Array.Empty<Type>();
    public IReadOnlyDictionary<string, PluginSettingDefinition>? SettingsSchema => null;
    public Task ApplyAsync(IFryPluginContext ctx, CancellationToken ct = default) => Task.CompletedTask;
}

// Shares AppLogService.Instance's buffer with AppLogServiceTests — same collection to avoid races.
[Collection("AppLogService")]
public class RemotePluginMarketplaceTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    /// <summary>
    /// Allocates a plugins directory that belongs to a single test.
    /// </summary>
    /// <remarks>
    /// Passing no directory used to leave <see cref="PluginMarketplaceService"/> to fall back
    /// to <see cref="FryPdfPaths.PluginsDirectory"/>, which every such test then shared. That
    /// directory holds <c>catalog_cache.json</c>, which the service writes after a successful
    /// fetch and re-reads from its own constructor — so a test backed by a mock handler would
    /// persist its fixture there and a later test would load it as if it were the real
    /// registry. The result was an order-dependent failure that also survived between runs,
    /// because the file outlives the process. Every test now gets its own directory.
    /// </remarks>
    private string NewTempDir(string prefix)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private IServiceProvider CreateTestServices(string? testDir = null, HttpClient? httpClient = null, string? registryBaseUrl = null)
    {
        testDir ??= NewTempDir("frypdf_mkt");

        var services = new ServiceCollection();
        services.AddSingleton<FryPluginContext>();
        services.AddSingleton<IFryPluginContext>(sp => sp.GetRequiredService<FryPluginContext>());
        services.AddSingleton<PluginHost>();
        services.AddSingleton<OverlayRegistry>();
        services.AddSingleton<IOverlayRegistry>(sp => sp.GetRequiredService<OverlayRegistry>());
        services.AddSingleton<IInstalledPluginStore>(sp =>
            new FileInstalledPluginStore(Path.Combine(testDir, "installed_plugins.json")));

        services.AddSingleton<IPluginMarketplaceService>(sp =>
        {
            var host = sp.GetRequiredService<PluginHost>();
            var overlay = sp.GetRequiredService<OverlayRegistry>();
            var store = sp.GetRequiredService<IInstalledPluginStore>();
            return new PluginMarketplaceService(
                host, overlay, store, httpClient, registryBaseUrl, Path.Combine(testDir, "plugins"));
        });

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task FetchRemoteCatalogAsync_ParsesOfficialCatalogSchema()
    {
        // Served from a stub rather than raw.githubusercontent.com. This test is named for
        // the parsing of the official catalog schema, and that is all it should depend on -
        // reaching the real host made it fail offline, behind a proxy, and any time the
        // published catalog changed.
        var handler = new MockHttpMessageHandler(Array.Empty<byte>(), OfficialCatalogFixture);
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        var sp = CreateTestServices(
            httpClient: httpClient,
            registryBaseUrl: PluginMarketplaceService.DefaultRegistryBaseUrl);
        var marketplace = sp.GetRequiredService<IPluginMarketplaceService>();

        var remoteItems = await marketplace.FetchRemoteCatalogAsync(forceRefresh: true);

        var sample = remoteItems.SingleOrDefault(i => i.Id == "com.frypdf.plugin.sample");
        Assert.NotNull(sample);
        Assert.Equal("Sample Overlay", sample!.Name);
        Assert.Equal("Code Fry Dev", sample.Publisher);
        Assert.Equal("UI & Extensions", sample.Category);
        Assert.StartsWith("https://raw.githubusercontent.com/codefrydev/PDFCreator-resources/", sample.DownloadUrl);
        Assert.Contains("sample", sample.Tags);

        // Fields the schema leaves optional must fall back to the model's defaults.
        var minimal = remoteItems.SingleOrDefault(i => i.Id == "com.frypdf.plugin.minimal");
        Assert.NotNull(minimal);
        Assert.Equal("General", minimal!.Category);
        Assert.Empty(minimal.Tags);

        var fullCatalog = await marketplace.GetCatalogAsync();
        Assert.NotEmpty(fullCatalog);
    }

    /// <summary>
    /// Mirrors the shape of catalog.json in codefrydev/PDFCreator-resources: camelCase keys,
    /// absolute raw.githubusercontent.com download URLs, and one entry that omits the
    /// optional fields so the defaults stay covered.
    /// </summary>
    private const string OfficialCatalogFixture = """
[
  {
    "id": "com.frypdf.plugin.sample",
    "name": "Sample Overlay",
    "publisher": "Code Fry Dev",
    "version": "1.0.0",
    "category": "UI & Extensions",
    "description": "A sample floating overlay.",
    "tags": [ "sample", "overlay" ],
    "downloadUrl": "https://raw.githubusercontent.com/codefrydev/PDFCreator-resources/refs/heads/main/plugins/com.frypdf.plugin.sample/Sample.fryplugin",
    "formattedSize": "51 KB"
  },
  {
    "id": "com.frypdf.plugin.minimal",
    "name": "Minimal Plugin",
    "publisher": "Code Fry Dev",
    "version": "1.0.0",
    "description": "Omits every optional field.",
    "downloadUrl": "https://raw.githubusercontent.com/codefrydev/PDFCreator-resources/refs/heads/main/plugins/com.frypdf.plugin.minimal/Minimal.fryplugin"
  }
]
""";

    [Fact]
    public async Task FetchRemoteCatalogAsync_DoesNotOfferRetiredOrFryWorldPlugins()
    {
        // FryPDF's external plugins (Snake, Music Player, Chess, Scratchpad, Image Editor, C# Code Studio, Telemetry HUD,
        // Tic-Tac-Toe) moved to FryWorld.Sdk; the store lists those builds for FryWorld, and FryPDF's own installer
        // cannot load them.
        var handler = new MockHttpMessageHandler(Array.Empty<byte>(), MixedCatalogFixture);
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sp = CreateTestServices(httpClient: httpClient, registryBaseUrl: PluginMarketplaceService.DefaultRegistryBaseUrl);
        var marketplace = sp.GetRequiredService<IPluginMarketplaceService>();

        var remoteItems = await marketplace.FetchRemoteCatalogAsync(forceRefresh: true);

        Assert.Equal(new[] { "com.frypdf.plugin.sample" }, remoteItems.Select(i => i.Id));
        Assert.DoesNotContain(await marketplace.GetCatalogAsync(), i => i.Id is "frypdf.overlay.snake" or "com.fryworld.snake");
    }

    private const string MixedCatalogFixture = """
[
  { "id": "com.frypdf.plugin.sample", "name": "Sample Overlay", "publisher": "Code Fry Dev", "description": "Fixture entry.", "version": "1.0.0", "dependencies": [ "PdfEditorApp.Core >= 1.0.0" ],
    "downloadUrl": "https://raw.githubusercontent.com/codefrydev/PDFCreator-resources/refs/heads/main/plugins/com.frypdf.plugin.sample/Sample.fryplugin" },
  { "id": "com.frypdf.plugin.tictactoe", "name": "Tic-Tac-Toe", "publisher": "Code Fry Dev", "description": "Fixture entry.", "version": "1.0.0", "dependencies": [ "PdfEditorApp.Core >= 1.0.0" ],
    "downloadUrl": "https://raw.githubusercontent.com/codefrydev/PDFCreator-resources/refs/heads/main/plugins/com.frypdf.plugin.tictactoe/TicTacToe.fryplugin" },
  { "id": "frypdf.overlay.telemetry", "name": "Document Telemetry HUD", "publisher": "Code Fry Dev", "description": "Fixture entry.", "version": "1.0.0", "dependencies": [ "PdfEditorApp.Core >= 1.0.0" ],
    "downloadUrl": "https://raw.githubusercontent.com/codefrydev/PDFCreator-resources/refs/heads/main/plugins/frypdf.overlay.telemetry/Telemetry.fryplugin" },
  { "id": "frypdf.overlay.snake", "name": "Retro Arcade Snake Game", "publisher": "Code Fry Dev", "description": "Fixture entry.", "version": "1.0.0", "dependencies": [ "PdfEditorApp.Core >= 1.0.0" ],
    "downloadUrl": "https://raw.githubusercontent.com/codefrydev/PDFCreator-resources/refs/heads/main/plugins/frypdf.overlay.snake/Snake.fryplugin" },
  { "id": "com.frypdf.plugin.csharpeditor", "name": "C# Code Studio", "publisher": "Code Fry Dev", "description": "Fixture entry.", "version": "1.0.0", "dependencies": [ "PdfEditorApp.Core >= 1.0.0" ],
    "downloadUrl": "https://raw.githubusercontent.com/codefrydev/PDFCreator-resources/refs/heads/main/plugins/com.frypdf.plugin.csharpeditor/CSharpEditor.fryplugin" },
  { "id": "com.fryworld.snake", "name": "Fry Snake", "publisher": "Code Fry Dev", "description": "Fixture entry.", "version": "1.0.0", "dependencies": [ "FryWorld.Sdk >= 0.0.2" ],
    "downloadUrl": "https://raw.githubusercontent.com/codefrydev/PDFCreator-resources/refs/heads/main/plugins/com.fryworld.snake/Snake.fryplugin" }
]
""";

    [Fact]
    public async Task FetchRemoteCatalogAsync_HandlesOfflineGracefully_WithoutThrowing()
    {
        // Simulate completely invalid / offline registry
        using var offlineClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        var sp = CreateTestServices(httpClient: offlineClient, registryBaseUrl: "http://invalid-unreachable-domain-987654.xyz");
        var marketplace = sp.GetRequiredService<IPluginMarketplaceService>();

        // Must not throw any unhandled exception
        var remote = await marketplace.FetchRemoteCatalogAsync();
        Assert.NotNull(remote);

        var catalog = await marketplace.GetCatalogAsync();
        Assert.NotNull(catalog);
    }

    [Fact]
    public async Task InstallPluginAsync_SupportsRemotePluginPackage_WithMockHttp()
    {
        var tempDir = Path.Combine(AppContext.BaseDirectory, $"frypdf_remote_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Build self-contained mock package from current test assembly
            var stagingDir = Path.Combine(tempDir, "staging");
            Directory.CreateDirectory(stagingDir);

            var manifest = new PluginManifest
            {
                Id = "com.frypdf.test.marketplace",
                Name = "Test Marketplace Plugin",
                Version = "1.0.0",
                EntryPoint = "PdfEditorApp.Tests.dll",
                Description = "Self-contained test mock plugin package."
            };
            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(stagingDir, "plugin.json"), manifestJson);

            var testDllPath = typeof(RemotePluginMarketplaceTests).Assembly.Location;
            File.Copy(testDllPath, Path.Combine(stagingDir, "PdfEditorApp.Tests.dll"), overwrite: true);

            var mockPkgPath = Path.Combine(tempDir, "TestMarketplacePlugin.fryplugin");
            ZipFile.CreateFromDirectory(stagingDir, mockPkgPath);
            var pkgBytes = await File.ReadAllBytesAsync(mockPkgPath);

            // Mock HttpMessageHandler returning mock catalog and real package
            var mockHandler = new MockHttpMessageHandler(pkgBytes);
            using var httpClient = new HttpClient(mockHandler) { Timeout = TimeSpan.FromSeconds(5) };

            var sp = CreateTestServices(testDir: tempDir, httpClient: httpClient, registryBaseUrl: "https://mock.frypdf.dev/plugins");
            var marketplace = sp.GetRequiredService<IPluginMarketplaceService>();
            var host = sp.GetRequiredService<PluginHost>();

            // Fetch catalog
            var remoteItems = await marketplace.FetchRemoteCatalogAsync(forceRefresh: true);
            Assert.Contains(remoteItems, i => i.Id == "com.frypdf.test.marketplace");
            var item = remoteItems.First(i => i.Id == "com.frypdf.test.marketplace");
            Assert.Equal("com.frypdf.test.marketplace", item.Id);

            // Install plugin
            string lastStatus = "";
            bool installed = await marketplace.InstallPluginAsync("com.frypdf.test.marketplace", statusCallback: s => lastStatus = s);
            Assert.True(installed, $"Install failed with status: {lastStatus}");
            Assert.True(marketplace.IsPluginInstalled("com.frypdf.test.marketplace"));
            Assert.True(host.IsPluginActive("com.frypdf.test.marketplace"));

            // Uninstall
            bool uninstalled = await marketplace.UninstallPluginAsync("com.frypdf.test.marketplace");
            Assert.True(uninstalled);
            Assert.False(marketplace.IsPluginInstalled("com.frypdf.test.marketplace"));
            Assert.False(host.IsPluginActive("com.frypdf.test.marketplace"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task InstallPluginAsync_CorruptPackage_LogsErrorWithFullExceptionDetail_AndReturnsFalse()
    {
        var tempDir = Path.Combine(AppContext.BaseDirectory, $"frypdf_corrupt_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Not a valid zip archive — triggers ZipFile.OpenRead to throw inside FryPluginPackageLoader.
            var corruptBytes = System.Text.Encoding.UTF8.GetBytes("this is not a valid zip archive");
            var mockHandler = new MockHttpMessageHandler(corruptBytes);
            using var httpClient = new HttpClient(mockHandler) { Timeout = TimeSpan.FromSeconds(5) };

            var sp = CreateTestServices(testDir: tempDir, httpClient: httpClient, registryBaseUrl: "https://mock.frypdf.dev/plugins");
            var marketplace = sp.GetRequiredService<IPluginMarketplaceService>();

            // Same control flow as before this change (install fails, returns false) —
            // now the real exception detail is also visible in the diagnostic log, not just ex.Message.
            bool installed = await marketplace.InstallPluginAsync("com.frypdf.test.marketplace");
            Assert.False(installed);

            var snapshot = AppLogService.Instance.GetSnapshot();
            var errorEntry = snapshot.LastOrDefault(e =>
                e.Category == "PluginInstall" &&
                e.Level == AppLogLevel.Error &&
                e.Message.Contains("Test Marketplace Plugin")); // logged install failure names item.Name, not item.Id

            Assert.NotNull(errorEntry);
            Assert.Contains("Exception", errorEntry!.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }


    [Fact]
    public async Task PluginsManagerViewModel_Search_RanksNamePrefixMatchFirst()
    {
        var mockHandler = new MockHttpMessageHandler(Array.Empty<byte>());
        using var httpClient = new HttpClient(mockHandler) { Timeout = TimeSpan.FromSeconds(5) };

        var sp = CreateTestServices(httpClient: httpClient, registryBaseUrl: "https://mock.frypdf.dev/plugins");
        var marketplace = sp.GetRequiredService<IPluginMarketplaceService>();
        var host = sp.GetRequiredService<PluginHost>();

        var vm = new PdfEditorApp.ViewModels.PluginsManagerViewModel(host, marketplace);
        await vm.LoadAllDataAsync();

        vm.SelectedTab = PdfEditorApp.ViewModels.PluginsManagerTab.Marketplace;
        vm.SearchQuery = "tile";

        Assert.NotEmpty(vm.FilteredMarketplacePlugins);
        // Tile Board should be ranked at index 0 because its name starts with "tile"
        Assert.Equal("com.frypdf.test.tiles", vm.FilteredMarketplacePlugins[0].Id);
        Assert.Equal("Tile Board", vm.FilteredMarketplacePlugins[0].Name);
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly byte[] _pkgBytes;
        private readonly string? _catalogJson;

        /// <param name="catalogJson">
        /// Catalog to serve for <c>catalog.json</c>. Defaults to the two-entry mock registry
        /// below; pass a fixture to exercise a different schema.
        /// </param>
        public MockHttpMessageHandler(byte[] pkgBytes, string? catalogJson = null)
        {
            _pkgBytes = pkgBytes;
            _catalogJson = catalogJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;

            if (uri.EndsWith("catalog.json", StringComparison.OrdinalIgnoreCase))
            {
                var catalogJson = _catalogJson ?? @"[
  {
    ""id"": ""com.frypdf.test.marketplace"",
    ""name"": ""Test Marketplace Plugin"",
    ""publisher"": ""FryPDF Core Team"",
    ""version"": ""1.0.0"",
    ""category"": ""UI & Extensions"",
    ""description"": ""Test mock plugin for automated installation."",
    ""downloadUrl"": ""https://mock.frypdf.dev/plugins/TestMarketplacePlugin.fryplugin"",
    ""formattedSize"": ""25 KB""
  },
  {
    ""id"": ""com.frypdf.test.tiles"",
    ""name"": ""Tile Board"",
    ""publisher"": ""Code Fry Dev"",
    ""version"": ""1.0.0"",
    ""category"": ""UI & Extensions"",
    ""description"": ""Floating tile puzzle overlay."",
    ""downloadUrl"": ""https://mock.frypdf.dev/plugins/TileBoard.fryplugin"",
    ""formattedSize"": ""51 KB""
  }
]";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(catalogJson, System.Text.Encoding.UTF8, "application/json")
                });
            }

            if (uri.EndsWith(".fryplugin", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_pkgBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
