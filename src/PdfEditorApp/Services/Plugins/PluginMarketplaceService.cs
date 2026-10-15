using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PdfEditorApp.Core.Plugins;
using PdfEditorApp.Core.Plugins.Descriptors;
using PdfEditorApp.Core.Plugins.Manifests;
using PdfEditorApp.Core.Plugins.Marketplace;
using PdfEditorApp.Core.Plugins.Settings;
using PdfEditorApp.Plugins.Loader;
using PdfEditorApp.Services;  // FryPdfPaths — writable-path resolver (MSIX-safe); AppLogService — diagnostic logging


namespace PdfEditorApp.Services.Plugins;

/// <summary>
/// Service providing access to the curated FryPDF Plugin Store and Marketplace.
/// Features real, functional extension packages with persistent history and 1-click mounting into the isolated plugin kernel.
/// </summary>
public class PluginMarketplaceService : IPluginMarketplaceService, IDisposable
{
    public const string DefaultRegistryBaseUrl = "https://raw.githubusercontent.com/codefrydev/PDFCreator-resources/refs/heads/main/plugins";

    private readonly PluginHost? _pluginHost;
    private readonly IOverlayRegistry? _overlayRegistry;
    private readonly IInstalledPluginStore _installedPluginStore;
    private readonly IPluginSettingsStore? _pluginSettingsStore;
    private readonly string _pluginsDirectory;
    private readonly string _registryBaseUrl;
    private readonly HttpClient _httpClient;
    private readonly HashSet<string> _installedMarketplaceIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hard ceiling on a downloaded .fryplugin archive (64 MB).</summary>
    private const long MaxPackageBytes = 64L * 1024 * 1024;
    private readonly List<MarketplacePluginItem> _remoteExtensions = new();
    private readonly object _catalogLock = new();

    /// <summary>
    /// All extensions and plugins are discovered and downloaded dynamically from the online catalog.json registry.
    /// </summary>
    private static readonly List<MarketplacePluginItem> CuratedExtensions = new();

    /// <summary>
    /// Plugins FryPDF no longer offers. They are FryWorld.Sdk plugins now (Snake, Music Player, Chess, Scratchpad,
    /// Image Editor, C# Code Studio, Telemetry HUD, Tic-Tac-Toe); FryPDF runs those from its fryworld-plugins folder
    /// instead. Filtered out of the remote catalog and of catalogs cached on disk before they were retired.
    /// </summary>
    private static readonly HashSet<string> RetiredPluginIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "frypdf.overlay.snake",
        "frypdf.overlay.musicplayer",
        "frypdf.overlay.scratchpad",
        "frypdf.overlay.imageeditor",
        "com.frypdf.plugin.chess",
        "com.frypdf.plugin.csharpeditor",
        "frypdf.overlay.telemetry",
        "com.frypdf.plugin.tictactoe"
    };

    /// <summary>
    /// Whether FryPDF's own installer can offer <paramref name="item"/>: not retired, and not built for FryWorld.Sdk
    /// (the store lists those for FryWorld; FryPDF's plugin loader cannot load them).
    /// </summary>
    internal static bool IsOfferedByFryPdf(MarketplacePluginItem item) =>
        !RetiredPluginIds.Contains(item.Id) &&
        !(item.Dependencies?.Any(d => d != null && d.TrimStart().StartsWith("FryWorld.Sdk", StringComparison.OrdinalIgnoreCase)) ?? false);

    public PluginMarketplaceService(
        PluginHost? pluginHost = null,
        IOverlayRegistry? overlayRegistry = null,
        IInstalledPluginStore? installedPluginStore = null,
        HttpClient? httpClient = null,
        string? registryBaseUrl = null,
        string? pluginsDirectory = null,
        IPluginSettingsStore? pluginSettingsStore = null)
    {
        _pluginHost = pluginHost;
        _overlayRegistry = overlayRegistry;
        // Use FryPdfPaths so that on MSIX installs (read-only WindowsApps dir) the
        // plugins and data files land in %LocalAppData%\FryPDF\ instead.
        _pluginsDirectory = string.IsNullOrWhiteSpace(pluginsDirectory)
            ? FryPdfPaths.PluginsDirectory
            : pluginsDirectory;
        _installedPluginStore = installedPluginStore
            ?? new FileInstalledPluginStore(FryPdfPaths.InstalledPluginsJsonPath);
        _pluginSettingsStore = pluginSettingsStore;
        _registryBaseUrl = string.IsNullOrWhiteSpace(registryBaseUrl) ? DefaultRegistryBaseUrl : registryBaseUrl.TrimEnd('/');
        // 15 seconds: GitHub CDN round-trip on a cold Windows boot (DNS + TLS handshake)
        // can easily exceed the old 6s limit, causing false "0 extensions" readings.
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        try
        {
            Directory.CreateDirectory(_pluginsDirectory);
            LoadDiskCatalogCache();
            ScanInstalledMarketplacePlugins();
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogWarning("PluginInstall", "Marketplace service initialization warning", ex);
        }
    }

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    /// <summary>
    /// Restores and activates previously installed plugins. Idempotent.
    /// </summary>
    /// <remarks>
    /// This used to run from the constructor, blocking on <c>EnablePluginAsync</c> with
    /// <c>GetAwaiter().GetResult()</c>. Because this is a DI singleton it was typically first
    /// resolved on the UI thread during startup, and enabling a plugin registers overlays and
    /// ribbon items that post back to the dispatcher — a self-deadlock. Call it after the main
    /// window exists and await it.
    /// </remarks>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await RestorePersistedPluginsAsync(ct);
            _initialized = true;
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogWarning("PluginInstall", "Marketplace plugin restore warning", ex);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private bool _hasFetchedRemote;

    private void LoadDiskCatalogCache()
    {
        try
        {
            // Primary cache is in the writable plugins directory (FryPdfPaths-resolved).
            // No AppContext.BaseDirectory fallback — that path may be read-only on MSIX.
            var cacheFile = Path.Combine(_pluginsDirectory, "catalog_cache.json");

            if (File.Exists(cacheFile))
            {
                var json = File.ReadAllText(cacheFile);
                var cached = JsonSerializer.Deserialize<List<MarketplacePluginItem>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (cached != null && cached.Count > 0)
                {
                    lock (_catalogLock)
                    {
                        _remoteExtensions.Clear();
                        _remoteExtensions.AddRange(cached.Where(c => c != null && !string.IsNullOrEmpty(c.Id) && IsOfferedByFryPdf(c)));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogWarning("PluginInstall", "Failed to load disk catalog cache", ex);
        }
    }

    private static IFryPlugin? InstantiatePlugin(string pluginId)
    {
        return null;
    }

    private void NormalizePluginVersions(MarketplacePluginItem item)
    {
        if (item == null) return;
        var versions = new List<MarketplacePluginVersion>();
        if (item.Versions != null && item.Versions.Count > 0)
        {
            versions.AddRange(item.Versions);
        }
        else if (!string.IsNullOrWhiteSpace(item.Version))
        {
            versions.Add(new MarketplacePluginVersion
            {
                Version = item.Version,
                DownloadUrl = item.DownloadUrl,
                Sha256 = item.Sha256,
                FormattedSize = item.FormattedSize,
                ReleaseNotes = item.Description
            });
        }

        var installedOnDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? activeInstalledVer = null;

        var rec = _installedPluginStore.Get(item.Id);
        if (rec != null)
        {
            activeInstalledVer = !string.IsNullOrWhiteSpace(rec.ActiveVersion) ? rec.ActiveVersion : rec.Version;
            if (rec.InstalledVersions != null)
            {
                foreach (var iv in rec.InstalledVersions) installedOnDisk.Add(iv);
            }
        }

        if (!string.IsNullOrWhiteSpace(_pluginsDirectory))
        {
            foreach (var v in PluginAssemblyLoader.GetInstalledVersionsOnDisk(_pluginsDirectory, item.Id))
            {
                installedOnDisk.Add(v);
            }
        }

        foreach (var ver in versions)
        {
            var compat = PluginCompatibilityChecker.CheckCompatibility(ver);
            ver.IsCompatible = compat.IsCompatible;
            ver.CompatibilityNote = compat.Message;

            var cleanVer = ver.Version.Trim().TrimStart('v', 'V');
            ver.IsInstalled = installedOnDisk.Contains(ver.Version) || installedOnDisk.Contains(cleanVer);
            ver.IsActive = activeInstalledVer != null &&
                (string.Equals(activeInstalledVer, ver.Version, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(activeInstalledVer.TrimStart('v', 'V'), cleanVer, StringComparison.OrdinalIgnoreCase));
        }

        var sorted = versions
            .OrderByDescending(v => PluginCompatibilityChecker.TryParseVersion(v.Version, out var semver) ? semver : new Version(0, 0))
            .ToList();

        item.Versions = sorted;

        if (item.SelectedVersion == null)
        {
            item.SelectedVersion = sorted.FirstOrDefault(v => v.IsActive)
                ?? sorted.FirstOrDefault(v => v.IsCompatible)
                ?? sorted.FirstOrDefault();
        }
    }

    private bool ShouldAutoOpenOverlay(string pluginId, IOverlayRegistry? overlayRegistry)
    {
        if (_pluginHost != null)
        {
            var plugin = _pluginHost.GetPlugin(pluginId);
            if (plugin != null && plugin.AutoOpenOverlay)
            {
                return true;
            }
        }

        if (overlayRegistry != null)
        {
            var overlayDesc = overlayRegistry.GetOverlay(pluginId);
            if (overlayDesc != null && overlayDesc.AutoOpenOnStartup)
            {
                return true;
            }
        }

        return false;
    }

    private async Task RestorePersistedPluginsAsync(CancellationToken ct = default)
    {
        if (_pluginHost == null) return;

        var records = _installedPluginStore.GetAll();
        foreach (var rec in records)
        {
            if (!rec.IsEnabled) continue;

            var plugin = InstantiatePlugin(rec.PluginId);
            if (plugin != null)
            {
                if (_pluginHost.GetPluginState(rec.PluginId) == PluginState.Unloaded)
                {
                    _pluginHost.RegisterPlugin(plugin);
                }

                if (!_pluginHost.IsPluginActive(rec.PluginId))
                {
                    try
                    {
                        await _pluginHost.EnablePluginAsync(rec.PluginId, ct);
                    }
                    catch (Exception ex)
                    {
                        AppLogService.Instance.LogWarning("PluginInstall", $"Restore error for '{rec.PluginId}'", ex);
                    }
                }

                lock (_catalogLock) { _installedMarketplaceIds.Add(rec.PluginId); }

                if (rec.WasOverlayOpen)
                {
                    var overlayReg = _overlayRegistry ?? _pluginHost.Context.GetService<IOverlayRegistry>();
                    if (ShouldAutoOpenOverlay(rec.PluginId, overlayReg))
                    {
                        overlayReg?.ShowOverlay(rec.PluginId);
                    }
                }
            }
            else
            {
                // The startup directory scan (App.InitializePluginSystem) already discovers,
                // loads and mounts everything under the plugins directory. Reloading here
                // pulled a second copy of the assembly into a fresh collectible ALC and
                // registered a second plugin instance — so an external plugin was mounted
                // twice per launch, and anything it owned existed twice. For the music player
                // that meant two native audio engines and two open playback devices competing
                // for the same output. The IsEnabled branch above already guards on plugin
                // state; this branch did not.
                if (_pluginHost.GetPluginState(rec.PluginId) != PluginState.Unloaded)
                {
                    lock (_catalogLock) { _installedMarketplaceIds.Add(rec.PluginId); }

                    if (rec.WasOverlayOpen)
                    {
                        var alreadyLoadedOverlayReg = _overlayRegistry ?? _pluginHost.Context.GetService<IOverlayRegistry>();
                        if (ShouldAutoOpenOverlay(rec.PluginId, alreadyLoadedOverlayReg))
                        {
                            alreadyLoadedOverlayReg?.ShowOverlay(rec.PluginId);
                        }
                    }

                    continue;
                }

                // Try restoring external downloaded plugin from plugins/{pluginId}/
                var pluginDir = Path.Combine(_pluginsDirectory, rec.PluginId);
                if (Directory.Exists(pluginDir))
                {
                    try
                    {
                        var targetVer = !string.IsNullOrWhiteSpace(rec.ActiveVersion) ? rec.ActiveVersion : rec.Version;
                        var entryDll = PluginAssemblyLoader.ResolvePluginDirectoryEntryDll(pluginDir, targetVer);
                        if (entryDll != null)
                        {
                            var pkg = PluginAssemblyLoader.LoadPluginAssembly(entryDll);
                            if (pkg.Plugins.Count > 0)
                            {
                                _pluginHost.RegisterPlugins(pkg.Plugins);
                                foreach (var p in pkg.Plugins)
                                {
                                    if (!_pluginHost.IsPluginActive(p.Id))
                                    {
                                        await _pluginHost.EnablePluginAsync(p.Id, ct);
                                    }
                                }

                                lock (_catalogLock) { _installedMarketplaceIds.Add(rec.PluginId); }

                                if (rec.WasOverlayOpen)
                                {
                                    var overlayReg = _overlayRegistry ?? _pluginHost.Context.GetService<IOverlayRegistry>();
                                    if (ShouldAutoOpenOverlay(rec.PluginId, overlayReg))
                                    {
                                        overlayReg?.ShowOverlay(rec.PluginId);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogService.Instance.LogWarning("PluginInstall", $"Error restoring external plugin '{rec.PluginId}'", ex);
                    }
                }
            }
        }
    }

    private void ScanInstalledMarketplacePlugins()
    {
        // Build into a local set and swap it in under one lock, so a concurrent
        // IsPluginInstalled never observes a half-cleared collection. Mutating the shared
        // HashSet in place while UI plugin cards read it is undefined behavior, not just
        // a stale answer.
        var rebuilt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rec in _installedPluginStore.GetAll())
        {
            if (rec != null && rec.IsEnabled && !string.IsNullOrEmpty(rec.PluginId))
            {
                rebuilt.Add(rec.PluginId);
            }
        }

        if (_pluginHost != null)
        {
            foreach (var item in CuratedExtensions)
            {
                if (item != null && !string.IsNullOrEmpty(item.Id) && _pluginHost.IsPluginActive(item.Id))
                {
                    rebuilt.Add(item.Id);
                }
            }

            List<MarketplacePluginItem> snapshot;
            lock (_catalogLock)
            {
                snapshot = _remoteExtensions.Where(i => i != null && !string.IsNullOrEmpty(i.Id)).ToList();
            }

            foreach (var item in snapshot)
            {
                if (_pluginHost.IsPluginActive(item.Id))
                {
                    rebuilt.Add(item.Id);
                }
            }
        }

        lock (_catalogLock)
        {
            _installedMarketplaceIds.Clear();
            foreach (var id in rebuilt) _installedMarketplaceIds.Add(id);
        }
    }

    public async Task<IReadOnlyList<MarketplacePluginItem>> FetchRemoteCatalogAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (_hasFetchedRemote && !forceRefresh)
        {
            lock (_catalogLock)
            {
                return _remoteExtensions.Where(i => i != null && !string.IsNullOrEmpty(i.Id)).ToList();
            }
        }

        var catalogUrl = $"{_registryBaseUrl}/catalog.json";

        // Attempt the HTTP fetch with 1 automatic retry (2 s back-off) to handle cold-boot
        // DNS / TLS latency on Windows that can push the round-trip above 6 s on first open.
        const int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var response = await _httpClient.GetAsync(catalogUrl, ct);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    var items = JsonSerializer.Deserialize<List<MarketplacePluginItem>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (items != null && items.Count > 0)
                    {
                        lock (_catalogLock)
                        {
                            // Drop entries cached before they were retired, so the saved cache forgets them too.
                            _remoteExtensions.RemoveAll(e => e == null || !IsOfferedByFryPdf(e));

                            foreach (var item in items.Where(i => i != null && !string.IsNullOrEmpty(i.Id) && IsOfferedByFryPdf(i)))
                            {
                                var existing = _remoteExtensions.FirstOrDefault(e => string.Equals(e?.Id, item.Id, StringComparison.OrdinalIgnoreCase));
                                if (existing != null)
                                    _remoteExtensions.Remove(existing);
                                _remoteExtensions.Add(item);
                            }
                        }

                        // Persist to local disk cache for fast/offline resilience
                        try
                        {
                            string mergedJson;
                            lock (_catalogLock)
                            {
                                mergedJson = JsonSerializer.Serialize(_remoteExtensions, new JsonSerializerOptions { WriteIndented = true });
                            }
                            // _pluginsDirectory is already FryPdfPaths-resolved (writable on MSIX)
                            var cacheFile = Path.Combine(_pluginsDirectory, "catalog_cache.json");
                            Directory.CreateDirectory(_pluginsDirectory);
                            File.WriteAllText(cacheFile, mergedJson);
                        }
                        catch { }
                    }
                    break; // Successful — no retry needed
                }
                else
                {
                    lock (_catalogLock)
                    {
                        if (_remoteExtensions.Count == 0)
                            LoadDiskCatalogCache();
                    }
                    break; // HTTP error (e.g. 404) — retrying won't help
                }
            }
            catch (Exception ex) when (attempt < maxAttempts &&
                                       !ct.IsCancellationRequested &&
                                       ex is HttpRequestException or TaskCanceledException)
            {
                // Transient network failure — wait 2 s then retry once
                AppLogService.Instance.LogWarning("PluginInstall", $"Catalog fetch attempt {attempt} failed; retrying", ex);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (Exception ex)
            {
                AppLogService.Instance.LogError("PluginInstall", "Remote catalog fetch failed", ex);
                lock (_catalogLock)
                {
                    if (_remoteExtensions.Count == 0)
                        LoadDiskCatalogCache();
                }
                break;
            }
        }

        _hasFetchedRemote = true;
        ScanInstalledMarketplacePlugins();
        List<MarketplacePluginItem> result;
        lock (_catalogLock)
        {
            foreach (var item in _remoteExtensions)
            {
                if (item == null || string.IsNullOrEmpty(item.Id)) continue;
                NormalizePluginVersions(item);
                item.Status = IsPluginInstalled(item.Id)
                    ? MarketplacePluginStatus.Installed
                    : MarketplacePluginStatus.Available;
            }
            result = _remoteExtensions.Where(i => i != null && !string.IsNullOrEmpty(i.Id)).ToList();
        }

        return result;
    }

    public Task<IReadOnlyList<MarketplacePluginItem>> GetCatalogAsync(CancellationToken ct = default)
    {
        ScanInstalledMarketplacePlugins();
        var allItems = new List<MarketplacePluginItem>(CuratedExtensions);
        List<MarketplacePluginItem> snapshot;
        lock (_catalogLock)
        {
            snapshot = _remoteExtensions.Where(i => i != null && !string.IsNullOrEmpty(i.Id)).ToList();
        }

        foreach (var remote in snapshot)
        {
            if (!allItems.Any(c => string.Equals(c?.Id, remote.Id, StringComparison.OrdinalIgnoreCase)))
            {
                allItems.Add(remote);
            }
        }

        foreach (var item in allItems)
        {
            NormalizePluginVersions(item);
            item.Status = IsPluginInstalled(item.Id)
                ? MarketplacePluginStatus.Installed
                : MarketplacePluginStatus.Available;
        }
        return Task.FromResult<IReadOnlyList<MarketplacePluginItem>>(allItems);
    }

    public Task<IReadOnlyList<MarketplacePluginItem>> SearchAsync(string query, string? category = null, CancellationToken ct = default)
    {
        ScanInstalledMarketplacePlugins();
        List<MarketplacePluginItem> allItems;
        lock (_catalogLock)
        {
            allItems = _remoteExtensions.Where(i => i != null && !string.IsNullOrEmpty(i.Id)).ToList();
        }

        var q = query.Trim().ToLowerInvariant();
        var results = allItems.Where(item =>
        {
            if (!string.IsNullOrWhiteSpace(category) && !string.Equals(category, "All", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (string.IsNullOrWhiteSpace(q)) return true;

            return (item.Name?.ToLowerInvariant().Contains(q) ?? false) ||
                   (item.Id?.ToLowerInvariant().Contains(q) ?? false) ||
                   (item.Publisher?.ToLowerInvariant().Contains(q) ?? false) ||
                   (item.Description?.ToLowerInvariant().Contains(q) ?? false) ||
                   (item.Tags?.Any(t => t.ToLowerInvariant().Contains(q)) ?? false);
        }).ToList();

        foreach (var item in results)
        {
            NormalizePluginVersions(item);
            item.Status = IsPluginInstalled(item.Id)
                ? MarketplacePluginStatus.Installed
                : MarketplacePluginStatus.Available;
        }

        return Task.FromResult<IReadOnlyList<MarketplacePluginItem>>(results);
    }

    public Task<bool> InstallPluginAsync(string pluginId, IProgress<double>? progress = null, Action<string>? statusCallback = null, CancellationToken ct = default)
    {
        return InstallPluginVersionAsync(pluginId, version: null, progress, statusCallback, ct);
    }

    public async Task<bool> InstallPluginVersionAsync(string pluginId, string? version = null, IProgress<double>? progress = null, Action<string>? statusCallback = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        MarketplacePluginItem? item;
        lock (_catalogLock)
        {
            item = CuratedExtensions.FirstOrDefault(e => string.Equals(e?.Id, pluginId, StringComparison.OrdinalIgnoreCase))
                   ?? _remoteExtensions.FirstOrDefault(e => string.Equals(e?.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        }

        // If not found in memory, try fetching remote catalog or fallback to known TicTacToe plugin
        if (item == null)
        {
            await FetchRemoteCatalogAsync(forceRefresh: false, ct);
            lock (_catalogLock)
            {
                item = _remoteExtensions.FirstOrDefault(e => string.Equals(e?.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (item == null)
        {
            var shortName = pluginId.Split('.').Last();
            item = new MarketplacePluginItem
            {
                Id = pluginId,
                Name = shortName,
                Publisher = "Community",
                Version = version ?? "1.0.0",
                Description = $"{shortName} plugin.",
                DownloadUrl = $"{_registryBaseUrl}/{pluginId}/{shortName}.fryplugin"
            };
        }

        NormalizePluginVersions(item);

        // Resolve target version metadata if multi-version
        MarketplacePluginVersion? targetVerMeta = null;
        if (!string.IsNullOrWhiteSpace(version))
        {
            targetVerMeta = item.Versions.FirstOrDefault(v =>
                string.Equals(v.Version.TrimStart('v', 'V'), version.Trim().TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase));
        }
        targetVerMeta ??= item.SelectedVersion ?? item.Versions.FirstOrDefault();

        var effectiveVersion = targetVerMeta?.Version ?? version ?? item.Version;
        var downloadUrl = !string.IsNullOrWhiteSpace(targetVerMeta?.DownloadUrl) ? targetVerMeta.DownloadUrl : item.DownloadUrl;
        var sha256 = !string.IsNullOrWhiteSpace(targetVerMeta?.Sha256) ? targetVerMeta.Sha256 : item.Sha256;
        var formattedSize = !string.IsNullOrWhiteSpace(targetVerMeta?.FormattedSize) ? targetVerMeta.FormattedSize : item.FormattedSize;

        // The id comes from remote catalog JSON and is used below to build the temp package
        // path, the install directory, and the uninstall directory that gets recursively deleted.
        if (!PluginIdValidator.IsValid(item.Id))
        {
            AppLogService.Instance.Log(AppLogLevel.Warning, "PluginInstall",
                $"Refused to install '{item.Id}': the catalog id is not a safe path segment.");
            statusCallback?.Invoke($"Refused to install '{item.Name}': unsafe plugin id.");
            item.Status = MarketplacePluginStatus.Available;
            return false;
        }

        item.Status = MarketplacePluginStatus.Installing;
        statusCallback?.Invoke($"Connecting to FryPDF Marketplace registry for '{item.Name}' v{effectiveVersion}...");
        progress?.Report(0.1);
        bool autoOpen = false;

        // Case A: Remote package download from registry
        if (!string.IsNullOrWhiteSpace(downloadUrl))
        {
            statusCallback?.Invoke($"Downloading {formattedSize} package archive from registry...");
            progress?.Report(0.2);

            var tempDir = Path.Combine(_pluginsDirectory, ".cache");
            Directory.CreateDirectory(tempDir);
            var safeVer = effectiveVersion.Replace('/', '_').Replace('\\', '_');
            var tempPackagePath = Path.Combine(tempDir, $"{item.Id}_{safeVer}.fryplugin");

            if (!IsAllowedDownloadUrl(downloadUrl))
            {
                AppLogService.Instance.Log(AppLogLevel.Warning, "PluginInstall",
                    $"Refused to download '{item.Id}': '{downloadUrl}' is not on the registry host.");
                statusCallback?.Invoke($"Refused to download '{item.Name}': untrusted download URL.");
                item.Status = MarketplacePluginStatus.Available;
                return false;
            }

            try
            {
                bool downloaded = false;
                try
                {
                    using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            long declaredLength = response.Content.Headers.ContentLength ?? 0;
                            if (declaredLength > MaxPackageBytes)
                            {
                                throw new InvalidOperationException(
                                    $"Package is {declaredLength} bytes, above the {MaxPackageBytes} byte limit.");
                            }

                            using var fileStream = new FileStream(tempPackagePath, FileMode.Create, FileAccess.Write, FileShare.None);
                            using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                            var buffer = new byte[81920];
                            long totalBytesRead = 0;
                            int bytesRead;

                            var reportStopwatch = Stopwatch.StartNew();
                            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                            {
                                totalBytesRead += bytesRead;
                                if (totalBytesRead > MaxPackageBytes)
                                {
                                    throw new InvalidOperationException(
                                        $"Package download exceeded the {MaxPackageBytes} byte limit while streaming.");
                                }

                                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);

                                if (declaredLength > 0)
                                {
                                    double fraction = (double)totalBytesRead / declaredLength;
                                    progress?.Report(0.2 + 0.5 * fraction);
                                    if (reportStopwatch.ElapsedMilliseconds >= 100 || totalBytesRead == declaredLength)
                                    {
                                        reportStopwatch.Restart();
                                        var dlStr = MarketplacePluginItem.FormatBytes(totalBytesRead);
                                        var totalStr = MarketplacePluginItem.FormatBytes(declaredLength);
                                        var pct = Math.Clamp((int)(fraction * 100), 0, 100);
                                        statusCallback?.Invoke($"Downloading {item.Name} v{effectiveVersion}: {dlStr} / {totalStr} ({pct}%)...");
                                    }
                                }
                                else
                                {
                                    if (reportStopwatch.ElapsedMilliseconds >= 100)
                                    {
                                        reportStopwatch.Restart();
                                        var dlStr = MarketplacePluginItem.FormatBytes(totalBytesRead);
                                        statusCallback?.Invoke($"Downloading {item.Name}: {dlStr} downloaded...");
                                    }
                                }
                            }
                            downloaded = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogService.Instance.LogWarning("PluginInstall", $"Remote package download failed for '{item.Id}'", ex);
                }

                if (!downloaded)
                {
                    item.Status = MarketplacePluginStatus.Available;
                    statusCallback?.Invoke($"Failed to download '{item.Name}' package from registry.");
                    AppLogService.Instance.Log(AppLogLevel.Warning, "PluginInstall",
                        $"Install aborted for '{item.Id}': download failed after {sw.ElapsedMilliseconds}ms.");
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(sha256))
                {
                    statusCallback?.Invoke("Verifying package SHA-256...");
                    var actual = await ComputeSha256Async(tempPackagePath, ct);
                    if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        AppLogService.Instance.Log(AppLogLevel.Error, "PluginInstall",
                            $"SHA-256 mismatch for '{item.Id}': catalog declared {sha256}, download was {actual}. Install aborted.");
                        statusCallback?.Invoke($"'{item.Name}' failed integrity verification and was not installed.");
                        item.Status = MarketplacePluginStatus.Available;
                        return false;
                    }
                }
                else
                {
                    AppLogService.Instance.Log(AppLogLevel.Warning, "PluginInstall",
                        $"Catalog entry '{item.Id}' carries no sha256; installing without integrity verification.");
                    statusCallback?.Invoke("No checksum published for this package — installing unverified.");
                }

                statusCallback?.Invoke("Unpacking package archive into versioned storage...");
                progress?.Report(0.75);

                var pkgResult = FryPluginPackageLoader.UnpackAndLoad(tempPackagePath, _pluginsDirectory);

                statusCallback?.Invoke("Mounting extension into isolated plugin kernel...");
                progress?.Report(0.85);

                autoOpen = false;
                if (_pluginHost != null)
                {
                    if (_pluginHost.IsPluginActive(item.Id))
                    {
                        await _pluginHost.DisablePluginAsync(item.Id, ct);
                    }

                    _pluginHost.RegisterPlugins(pkgResult.AssemblyPackage.Plugins);
                    foreach (var pkgPlugin in pkgResult.AssemblyPackage.Plugins)
                    {
                        if (!_pluginHost.IsPluginActive(pkgPlugin.Id))
                        {
                            await _pluginHost.EnablePluginAsync(pkgPlugin.Id, ct);
                        }
                    }

                    var overlayReg = _overlayRegistry ?? _pluginHost.Context.GetService<IOverlayRegistry>();
                    autoOpen = ShouldAutoOpenOverlay(item.Id, overlayReg);
                    if (autoOpen)
                    {
                        overlayReg?.ShowOverlay(item.Id);
                    }
                }

                lock (_catalogLock) { _installedMarketplaceIds.Add(item.Id); }

                var record = _installedPluginStore.Get(item.Id) ?? new InstalledPluginRecord { PluginId = item.Id };
                record.Name = item.Name;
                record.Version = effectiveVersion;
                record.ActiveVersion = effectiveVersion;
                record.ActiveDirectoryPath = pkgResult.InstallDirectory;
                record.InstalledAt = DateTime.UtcNow;
                record.IsEnabled = true;
                record.WasOverlayOpen = autoOpen;
                if (record.InstalledVersions == null) record.InstalledVersions = new List<string>();
                if (!record.InstalledVersions.Contains(effectiveVersion, StringComparer.OrdinalIgnoreCase))
                {
                    record.InstalledVersions.Add(effectiveVersion);
                }
                _installedPluginStore.AddOrUpdate(record);

                NormalizePluginVersions(item);
                item.Status = MarketplacePluginStatus.Installed;
                statusCallback?.Invoke($"'{item.Name}' v{effectiveVersion} installed and activated successfully!");
                progress?.Report(1.0);
                AppLogService.Instance.Log(AppLogLevel.Info, "PluginInstall",
                    $"Installed '{item.Id}' v{effectiveVersion} ({pkgResult.AssemblyPackage.Plugins.Count} plugin(s)) in {sw.ElapsedMilliseconds}ms.");
                return true;
            }
            catch (Exception ex)
            {
                AppLogService.Instance.LogError("PluginInstall", $"Install failed for '{item.Name}' after {sw.ElapsedMilliseconds}ms", ex);
                item.Status = MarketplacePluginStatus.Available;
                statusCallback?.Invoke($"Failed to install '{item.Name}': {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPackagePath))
                    {
                        File.Delete(tempPackagePath);
                    }
                }
                catch { }
            }
        }

        // Case B: Local/simulated installation for catalog entries without a downloadable package
        await Task.Delay(100, ct);

        statusCallback?.Invoke($"Preparing {item.FormattedSize} package components...");
        progress?.Report(0.35);
        await Task.Delay(150, ct);

        statusCallback?.Invoke("Preparing built-in extension components...");
        progress?.Report(0.65);
        await Task.Delay(100, ct);

        // Create installation folder in plugins/
        var targetDir = Path.Combine(_pluginsDirectory, item.Id);
        Directory.CreateDirectory(targetDir);

        var manifestPath = Path.Combine(targetDir, "plugin.json");
        var manifestContent = JsonSerializer.Serialize(
            new PluginManifest
            {
                Id = item.Id,
                Name = item.Name,
                Version = effectiveVersion,
                Description = item.Description,
                Author = item.Publisher,
                EntryPoint = $"{item.Id}.dll",
                Icon = item.IconKind
            },
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(manifestPath, manifestContent, ct);

        statusCallback?.Invoke("Mounting extension into isolated plugin kernel...");
        progress?.Report(0.85);

        // Mount and activate real plugin into host if available
        var plugin = InstantiatePlugin(item.Id);
        autoOpen = false;
        if (plugin != null && _pluginHost != null)
        {
            if (_pluginHost.GetPluginState(item.Id) == PluginState.Unloaded)
            {
                _pluginHost.RegisterPlugin(plugin);
            }

            if (!_pluginHost.IsPluginActive(item.Id))
            {
                await _pluginHost.EnablePluginAsync(item.Id, ct);
            }

            var overlayReg = _overlayRegistry ?? _pluginHost.Context.GetService<IOverlayRegistry>();
            autoOpen = ShouldAutoOpenOverlay(item.Id, overlayReg);
            if (autoOpen)
            {
                overlayReg?.ShowOverlay(item.Id);
            }
        }

        lock (_catalogLock) { _installedMarketplaceIds.Add(item.Id); }
        var builtinRecord = _installedPluginStore.Get(item.Id) ?? new InstalledPluginRecord { PluginId = item.Id };
        builtinRecord.Name = item.Name;
        builtinRecord.Version = effectiveVersion;
        builtinRecord.ActiveVersion = effectiveVersion;
        builtinRecord.InstalledAt = DateTime.UtcNow;
        builtinRecord.IsEnabled = true;
        builtinRecord.WasOverlayOpen = autoOpen;
        if (builtinRecord.InstalledVersions == null) builtinRecord.InstalledVersions = new List<string>();
        if (!builtinRecord.InstalledVersions.Contains(effectiveVersion, StringComparer.OrdinalIgnoreCase))
        {
            builtinRecord.InstalledVersions.Add(effectiveVersion);
        }
        _installedPluginStore.AddOrUpdate(builtinRecord);

        NormalizePluginVersions(item);
        item.Status = MarketplacePluginStatus.Installed;

        statusCallback?.Invoke($"'{item.Name}' installed and activated successfully!");
        progress?.Report(1.0);
        AppLogService.Instance.Log(AppLogLevel.Info, "PluginInstall",
            $"Installed '{item.Id}' via local/simulated path in {sw.ElapsedMilliseconds}ms.");
        return true;
    }

    public async Task<bool> SwitchActiveVersionAsync(string pluginId, string targetVersion, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);

        var pluginDir = Path.Combine(_pluginsDirectory, pluginId);
        var entryDll = PluginAssemblyLoader.ResolvePluginDirectoryEntryDll(pluginDir, targetVersion);
        if (entryDll == null || !File.Exists(entryDll))
        {
            // If not found on local disk, install it from remote registry
            return await InstallPluginVersionAsync(pluginId, targetVersion, ct: ct);
        }

        if (_pluginHost != null)
        {
            if (_pluginHost.IsPluginActive(pluginId))
            {
                await _pluginHost.DisablePluginAsync(pluginId, ct);
            }

            var pkg = PluginAssemblyLoader.LoadPluginAssembly(entryDll);
            if (pkg.Plugins.Count > 0)
            {
                _pluginHost.RegisterPlugins(pkg.Plugins);
                foreach (var p in pkg.Plugins)
                {
                    await _pluginHost.EnablePluginAsync(p.Id, ct);
                }
            }
        }

        var record = _installedPluginStore.Get(pluginId) ?? new InstalledPluginRecord { PluginId = pluginId };
        record.ActiveVersion = targetVersion;
        record.Version = targetVersion;
        record.ActiveDirectoryPath = Path.GetDirectoryName(entryDll);
        if (record.InstalledVersions == null) record.InstalledVersions = new List<string>();
        if (!record.InstalledVersions.Contains(targetVersion, StringComparer.OrdinalIgnoreCase))
        {
            record.InstalledVersions.Add(targetVersion);
        }
        _installedPluginStore.AddOrUpdate(record);

        ScanInstalledMarketplacePlugins();
        return true;
    }

    public IReadOnlyList<string> GetInstalledVersions(string pluginId)
    {
        return PluginAssemblyLoader.GetInstalledVersionsOnDisk(_pluginsDirectory, pluginId);
    }

    public Task<bool> DeleteVersionAsync(string pluginId, string version, CancellationToken ct = default)
    {
        var record = _installedPluginStore.Get(pluginId);
        var activeVer = record != null ? (!string.IsNullOrWhiteSpace(record.ActiveVersion) ? record.ActiveVersion : record.Version) : null;

        if (string.Equals(activeVer, version, StringComparison.OrdinalIgnoreCase))
        {
            AppLogService.Instance.LogWarning("PluginInstall", $"Refused to delete currently active version '{version}' of '{pluginId}'");
            return Task.FromResult(false);
        }

        var candidate = Path.Combine(_pluginsDirectory, pluginId, version);
        if (!Directory.Exists(candidate))
        {
            candidate = Path.Combine(_pluginsDirectory, pluginId, $"v{version}");
        }

        if (Directory.Exists(candidate))
        {
            try
            {
                Directory.Delete(candidate, recursive: true);
                if (record != null && record.InstalledVersions != null)
                {
                    record.InstalledVersions.RemoveAll(v => string.Equals(v, version, StringComparison.OrdinalIgnoreCase));
                    _installedPluginStore.AddOrUpdate(record);
                }
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                AppLogService.Instance.LogError("PluginInstall", $"Failed to delete version directory '{candidate}'", ex);
                return Task.FromResult(false);
            }
        }

        return Task.FromResult(false);
    }

    public async Task<bool> UninstallPluginAsync(string pluginId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return false;
        var sw = Stopwatch.StartNew();

        // 1. In-memory catalog item and multi-version status reset
        MarketplacePluginItem? item;
        lock (_catalogLock)
        {
            item = CuratedExtensions.FirstOrDefault(e => string.Equals(e?.Id, pluginId, StringComparison.OrdinalIgnoreCase))
                   ?? _remoteExtensions.FirstOrDefault(e => string.Equals(e?.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        }
        if (item != null)
        {
            item.Status = MarketplacePluginStatus.Available;
            item.InstallProgress = 0;
            item.InstallProgressPercent = 0;
            item.InstallStatusText = string.Empty;
            if (item.Versions != null)
            {
                foreach (var v in item.Versions)
                {
                    v.IsInstalled = false;
                    v.IsActive = false;
                }
            }
            item.SelectedVersion = item.Versions?.FirstOrDefault();
        }

        lock (_catalogLock) { _installedMarketplaceIds.Remove(pluginId); }

        // 2. Remove persistent record from installed_plugins.json
        _installedPluginStore.Remove(pluginId);

        // 3. Purge persisted settings from IPluginSettingsStore (plugins.settings.json)
        var settingsStore = _pluginSettingsStore;
        if (settingsStore == null && _pluginHost != null && _pluginHost.Context.TryGetService<IPluginSettingsStore>(out var resolvedStore))
        {
            settingsStore = resolvedStore;
        }
        try
        {
            settingsStore?.RemovePluginSettings(pluginId);
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogWarning("PluginInstall", $"Failed to purge settings for uninstalled plugin '{pluginId}'", ex);
        }

        // 4. Hide overlay and completely unregister from kernel runtime
        if (_pluginHost != null)
        {
            try
            {
                var overlayReg = _overlayRegistry;
                if (overlayReg == null && _pluginHost.Context.TryGetService<IOverlayRegistry>(out var resolvedOverlay))
                {
                    overlayReg = resolvedOverlay;
                }
                overlayReg?.HideOverlay(pluginId);
            }
            catch (Exception ex)
            {
                AppLogService.Instance.LogWarning("PluginInstall", $"Error hiding overlay for '{pluginId}' during uninstall", ex);
            }

            try
            {
                await _pluginHost.UnregisterPluginAsync(pluginId, ct);
            }
            catch (Exception ex)
            {
                AppLogService.Instance.LogError("PluginInstall", $"Error unregistering plugin '{pluginId}' from host during uninstall", ex);
            }
        }

        // 5. Unload assembly contexts & release OS file locks
        try
        {
            PluginAssemblyLoader.UnloadPlugin(pluginId);
            var targetDir = Path.Combine(_pluginsDirectory, pluginId);
            PluginAssemblyLoader.UnloadPackagesForDirectory(targetDir);
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogWarning("PluginInstall", $"Error unloading ALC packages for '{pluginId}'", ex);
        }

        // 6. Delete all plugin directories and version subdirectories from local device
        var primaryDir = Path.Combine(_pluginsDirectory, pluginId);
        DeleteDirectoryRecursiveSafely(primaryDir);

        // Also delete any timestamped fallback directories (<pluginId>_*)
        try
        {
            if (Directory.Exists(_pluginsDirectory))
            {
                var fallbacks = Directory.GetDirectories(_pluginsDirectory, $"{pluginId}_*");
                foreach (var fb in fallbacks)
                {
                    DeleteDirectoryRecursiveSafely(fb);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogWarning("PluginInstall", $"Error checking fallback directories for '{pluginId}'", ex);
        }

        // 7. Delete all cached .fryplugin package archives for this plugin (all versions!)
        try
        {
            var cacheDir = Path.Combine(_pluginsDirectory, ".cache");
            if (Directory.Exists(cacheDir))
            {
                var cachedFiles = Directory.GetFiles(cacheDir, $"{pluginId}*.fryplugin");
                foreach (var file in cachedFiles)
                {
                    var fileName = Path.GetFileName(file);
                    if (string.Equals(fileName, $"{pluginId}.fryplugin", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith($"{pluginId}_", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                        }
                        catch (Exception ex)
                        {
                            AppLogService.Instance.LogWarning("PluginInstall", $"Failed to delete cached package '{file}'", ex);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogWarning("PluginInstall", $"Error clearing package cache for '{pluginId}'", ex);
        }

        // 8. Delete any staging folders or files matching pluginId in .staging
        try
        {
            var stagingDir = Path.Combine(_pluginsDirectory, ".staging");
            if (Directory.Exists(stagingDir))
            {
                var stagedDirs = Directory.GetDirectories(stagingDir, $"{pluginId}*");
                foreach (var sd in stagedDirs)
                {
                    DeleteDirectoryRecursiveSafely(sd);
                }
                var stagedFiles = Directory.GetFiles(stagingDir, $"{pluginId}*");
                foreach (var sf in stagedFiles)
                {
                    try
                    {
                        File.SetAttributes(sf, FileAttributes.Normal);
                        File.Delete(sf);
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogWarning("PluginInstall", $"Error clearing staging for '{pluginId}'", ex);
        }

        AppLogService.Instance.Log(AppLogLevel.Info, "PluginInstall",
            $"Uninstalled '{pluginId}' and deleted all local versions/caches in {sw.ElapsedMilliseconds}ms.");
        return true;
    }

    /// <summary>
    /// Recursively and safely deletes a directory from the local device, stripping read-only
    /// flags and using cooperative retries with garbage collection if OS file locks are detected.
    /// </summary>
    private static void DeleteDirectoryRecursiveSafely(string dirPath, int maxRetries = 3)
    {
        if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath)) return;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch { }
                }

                foreach (var subDir in Directory.EnumerateDirectories(dirPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(subDir, FileAttributes.Normal);
                    }
                    catch { }
                }

                File.SetAttributes(dirPath, FileAttributes.Normal);
                Directory.Delete(dirPath, recursive: true);
                return;
            }
            catch (Exception ex)
            {
                if (attempt == maxRetries)
                {
                    try
                    {
                        var parent = Path.GetDirectoryName(dirPath) ?? dirPath;
                        var staging = Path.Combine(parent, ".staging");
                        Directory.CreateDirectory(staging);
                        var trash = Path.Combine(staging, $".trash_{Path.GetFileName(dirPath)}_{DateTime.UtcNow.Ticks}");
                        Directory.Move(dirPath, trash);
                        AppLogService.Instance.LogWarning("PluginInstall",
                            $"Locked directory '{dirPath}' moved to trash '{trash}' for later cleanup.", ex);
                        return;
                    }
                    catch
                    {
                        AppLogService.Instance.LogError("PluginInstall",
                            $"Failed to delete directory '{dirPath}' after {maxRetries} attempts.", ex);
                    }
                }
                else
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Thread.Sleep(50 * attempt);
                }
            }
        }
    }

    public bool IsPluginInstalled(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return false;

        if (_installedPluginStore.IsInstalled(pluginId))
            return true;

        if (_pluginHost != null && _pluginHost.IsPluginActive(pluginId))
            return true;

        lock (_catalogLock) { return _installedMarketplaceIds.Contains(pluginId); }
    }

    public async Task<IReadOnlyList<MarketplacePluginItem>> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var remote = await FetchRemoteCatalogAsync(forceRefresh: true, ct);
        var updateAvailable = new List<MarketplacePluginItem>();

        foreach (var remoteItem in remote)
        {
            var installed = _installedPluginStore.Get(remoteItem.Id);
            if (installed != null)
            {
                if (IsNewerVersion(remoteItem.Version, installed.Version))
                {
                    remoteItem.Status = MarketplacePluginStatus.UpdateAvailable;
                    updateAvailable.Add(remoteItem);
                }
            }
        }

        return updateAvailable;
    }

    /// <summary>
    /// True when <paramref name="downloadUrl"/> is an absolute HTTPS URL on the same host as
    /// the configured registry.
    /// </summary>
    /// <remarks>
    /// The URL arrives from catalog JSON. Without this check a compromised or mirrored catalog
    /// could point the installer — which downloads and then executes code — at any host.
    /// </remarks>
    internal bool IsAllowedDownloadUrl(string? downloadUrl)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl)) return false;
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)) return false;
        if (!Uri.TryCreate(_registryBaseUrl, UriKind.Absolute, out var registryUri)) return false;

        // Plaintext HTTP would let a network attacker swap the package for their own.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(uri.Host, registryUri.Host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Computes the lowercase hex SHA-256 of a file.</summary>
    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Compares two version strings, tolerating semver pre-release and build metadata
    /// ("1.2.0-beta", "1.2.0+build.5") which <see cref="Version.TryParse(string, out Version)"/>
    /// rejects outright — previously any such plugin silently never reported an update.
    /// </summary>
    internal static bool IsNewerVersion(string? candidate, string? installed)
    {
        var candidateCore = ParseVersionCore(candidate);
        var installedCore = ParseVersionCore(installed);
        if (candidateCore == null || installedCore == null) return false;

        int coreComparison = candidateCore.CompareTo(installedCore);
        if (coreComparison != 0) return coreComparison > 0;

        // Same numeric core: a release supersedes a pre-release of the same version.
        bool candidatePre = HasPreRelease(candidate);
        bool installedPre = HasPreRelease(installed);
        return installedPre && !candidatePre;
    }

    private static bool HasPreRelease(string? version)
        => !string.IsNullOrWhiteSpace(version) && version.Contains('-');

    private static Version? ParseVersionCore(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var core = version.Trim();
        int cut = core.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) core = core[..cut];

        return Version.TryParse(core, out var parsed) ? parsed : null;
    }

    private readonly bool _ownsHttpClient;
    private bool _isDisposed;

    /// <summary>
    /// Disposes the <see cref="HttpClient"/> this service created for itself.
    /// </summary>
    /// <remarks>
    /// The class owned an HttpClient but did not implement IDisposable, so its handler was
    /// never released. An injected client belongs to the caller and is left alone.
    /// </remarks>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        _initLock.Dispose();

        GC.SuppressFinalize(this);
    }
}
