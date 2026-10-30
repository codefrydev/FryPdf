using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PdfEditorApp.Core.Models;
using PdfEditorApp.Core.Plugins;
using PdfEditorApp.Core.Plugins.Descriptors;
using PdfEditorApp.Models;
using PdfEditorApp.Services;
using PdfEditorApp.Services.Tools.Core;

namespace PdfEditorApp.ViewModels;

public partial class MainViewModel
{
    public string AppVersion
    {
        get
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var infoVer = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(infoVer))
                {
                    return infoVer.Split('+')[0];
                }

                var ver = assembly.GetName().Version;
                if (ver != null)
                {
                    return $"{ver.Major}.{ver.Minor}.{ver.Build}";
                }
            }
            catch
            {
                // fallback
            }

            return "0.2.0";
        }
    }

    public string AppVersionDisplay => $"v{AppVersion} Open Source";

    // --- COMMAND PALETTE & SHORTCUTS HELP STATE ---

    [ObservableProperty]
    private bool _isCommandPaletteOpen;

    [ObservableProperty]
    private string _commandSearchQuery = "";

    [ObservableProperty]
    private int _selectedPaletteIndex;

    [ObservableProperty]
    private bool _isShortcutsHelpDialogOpen;

    [ObservableProperty]
    private bool _isAboutDialogOpen;

    [ObservableProperty]
    private bool _isPluginsDialogOpen;

    [ObservableProperty]
    private string _pluginSearchQuery = "";

    public ObservableCollection<PluginItemViewModel> FilteredPluginsList { get; } = new();
    private readonly List<PluginItemViewModel> _allLoadedPlugins = new();

    partial void OnPluginSearchQueryChanged(string value)
    {
        FilterLoadedPlugins(value);
    }

    private void FilterLoadedPlugins(string query)
    {
        FilteredPluginsList.Clear();
        var q = query.Trim().ToLowerInvariant();
        foreach (var p in _allLoadedPlugins)
        {
            if (string.IsNullOrWhiteSpace(q) ||
                p.Name.ToLowerInvariant().Contains(q) ||
                p.Id.ToLowerInvariant().Contains(q) ||
                p.Category.ToLowerInvariant().Contains(q) ||
                p.Description.ToLowerInvariant().Contains(q))
            {
                FilteredPluginsList.Add(p);
            }
        }
    }

    [RelayCommand]
    public void OpenPluginsDialog()
    {
        PopulateLoadedPlugins();
        PluginSearchQuery = "";
        FilterLoadedPlugins("");
        IsPluginsDialogOpen = true;
        // Route through plugin dialog registry
        OpenRegisteredDialog("frypdf.dialog.plugins");
    }

    [ObservableProperty]
    private string _activeProfileName = "desktop";

    [RelayCommand]
    public void SwitchProfile(string profileName)
    {
        try
        {
            var host = PluginHost ?? _pluginHost ?? App.Services?.GetService<PluginHost>();
            if (host == null) return;

            string profilePath = System.IO.Path.Combine(AppContext.BaseDirectory, "profiles", $"{profileName}.profile.json");
            if (!System.IO.File.Exists(profilePath))
            {
                profilePath = $"profiles/{profileName}.profile.json";
            }

            if (System.IO.File.Exists(profilePath))
            {
                var profile = PdfEditorApp.Core.Plugins.Profiles.ProfileLoader.LoadFromFile(profilePath);
                ActiveProfileName = profile.ProfileName;

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
                    new PdfEditorApp.Plugins.Bundles.EditorSidebarsBundle()
                };

                PdfEditorApp.Core.Plugins.Profiles.ProfileLoader.ApplyProfile(profile, host, availableBundles);
                PopulateLoadedPlugins();
                ShowToast($"Switched to '{profileName}' profile!", ToastNotificationType.Success, "Tune");
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Failed to switch profile: {ex.Message}", ToastNotificationType.Danger, "AlertCircleOutline");
        }
    }

    [RelayCommand]
    public void ClosePluginsDialog()
    {
        IsPluginsDialogOpen = false;
        CloseDynamicDialog();
    }

    public void PopulateLoadedPlugins()
    {
        _allLoadedPlugins.Clear();
        try
        {
            var host = PluginHost ?? _pluginHost ?? App.Services?.GetService<PluginHost>();
            var toolRegistry = _toolRegistry ?? App.Services?.GetService<IPdfToolRegistry>();
            var toolDefs = toolRegistry?.GetAllTools() ?? Array.Empty<PdfToolDefinition>();
            var toolMap = new Dictionary<string, PdfToolDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in toolDefs)
            {
                if (!string.IsNullOrWhiteSpace(t.StringId))
                {
                    toolMap[t.StringId] = t;
                }
            }

            if (host != null)
            {
                var pluginsToShow = host.RegisteredPlugins.Count > 0 ? host.RegisteredPlugins : host.LoadedPlugins;
                foreach (var plugin in pluginsToShow)
                {
                    toolMap.TryGetValue(plugin.Id, out var tool);

                    string category = tool?.CategoryDisplayName ?? "Plugin Extension";
                    string description = tool?.Description ?? $"Plugin component '{plugin.Name}'";
                    string iconKind = tool?.IconKind ?? "PuzzleOutline";
                    string iconColor = tool?.IconColorHex ?? "#7C3AED";

                    if (tool == null)
                    {
                        if (plugin.Id.StartsWith("frypdf.page.", StringComparison.OrdinalIgnoreCase))
                        {
                            category = "Workspace Navigation";
                            iconKind = "ViewDashboardOutline";
                            iconColor = "#0D9488";
                            description = $"Modular workspace view and navigation page '{plugin.Name}'";
                        }
                        else if (plugin.Id.StartsWith("frypdf.overlay", StringComparison.OrdinalIgnoreCase))
                        {
                            category = "Shell Overlays";
                            iconKind = "GamepadVariantOutline";
                            iconColor = "#10B981";
                            description = $"Floating interactive shell overlay '{plugin.Name}'";
                        }
                        else if (plugin.Id.StartsWith("frypdf.dialog", StringComparison.OrdinalIgnoreCase))
                        {
                            category = "Modal Studios & Dialogs";
                            iconKind = "WindowMaximize";
                            iconColor = "#F97316";
                            description = $"Modal studio overlay and interactive dialog '{plugin.Name}'";
                        }
                        else if (plugin.Id.StartsWith("frypdf.sidebar", StringComparison.OrdinalIgnoreCase))
                        {
                            category = "Editor Sidebars";
                            iconKind = "DockLeft";
                            iconColor = "#84CC16";
                            description = $"Document editor sidebar panel '{plugin.Name}'";
                        }
                        else if (plugin.Id.StartsWith("frypdf.element.", StringComparison.OrdinalIgnoreCase))
                        {
                            category = "Canvas Elements";
                            iconKind = "ShapeOutline";
                            iconColor = "#0284C7";
                            description = $"Canvas element provider '{plugin.Name}'";
                        }
                        else if (plugin.Id.StartsWith("frypdf.io.", StringComparison.OrdinalIgnoreCase))
                        {
                            category = "Document I/O";
                            iconKind = "SwapHorizontal";
                            iconColor = "#10B981";
                            description = $"Document format import/export filter '{plugin.Name}'";
                        }
                        else if (plugin.Id.StartsWith("frypdf.ai.", StringComparison.OrdinalIgnoreCase))
                        {
                            category = "AI Providers";
                            iconKind = "Brain";
                            iconColor = "#8B5CF6";
                            description = $"LLM intelligence and analysis provider '{plugin.Name}'";
                        }
                        else if (plugin.Id.StartsWith("frypdf.ocr.", StringComparison.OrdinalIgnoreCase))
                        {
                            category = "OCR Engines";
                            iconKind = "TextRecognition";
                            iconColor = "#F59E0B";
                            description = $"Optical character recognition engine '{plugin.Name}'";
                        }
                        else if (plugin.Id.StartsWith("frypdf.template", StringComparison.OrdinalIgnoreCase))
                        {
                            category = "Document Templates";
                            iconKind = "FileDocumentOutline";
                            iconColor = "#EC4899";
                            description = $"Structured document template pack '{plugin.Name}'";
                        }
                        else if (plugin.Id.StartsWith("frypdf.status", StringComparison.OrdinalIgnoreCase))
                        {
                            category = "Status Bar Widgets";
                            iconKind = "DockBottom";
                            iconColor = "#06B6D4";
                            description = $"Status bar metric and diagnostic widget '{plugin.Name}'";
                        }
                        else if (plugin.Id.StartsWith("frypdf.inspector.", StringComparison.OrdinalIgnoreCase))
                        {
                            category = "Property Inspector";
                            iconKind = "CardBulletedOutline";
                            iconColor = "#3B82F6";
                            description = $"Live element property inspector section '{plugin.Name}'";
                        }
                        else if (plugin.Id.StartsWith("frypdf.command", StringComparison.OrdinalIgnoreCase) || plugin.Id.StartsWith("frypdf.palette", StringComparison.OrdinalIgnoreCase))
                        {
                            category = "Command Palette";
                            iconKind = "ConsoleLine";
                            iconColor = "#6366F1";
                            description = $"Quick command and shortcut provider '{plugin.Name}'";
                        }
                    }

                    _allLoadedPlugins.Add(new PluginItemViewModel
                    {
                        Id = plugin.Id,
                        Name = plugin.Name,
                        Version = plugin.Version.ToString(),
                        Category = category,
                        Description = description,
                        IconKind = iconKind,
                        IconColorHex = iconColor,
                        IsActive = host.IsPluginActive(plugin.Id),
                        IsExternal = plugin.GetType().Assembly != typeof(MainViewModel).Assembly,
                        SourceAssembly = plugin.GetType().Assembly.GetName().Name ?? "FryPDF",
                        SettingsSchema = plugin.SettingsSchema,
                        ToggleHandler = async (id, active) =>
                        {
                            if (active) await host.EnablePluginAsync(id);
                            else await host.DisablePluginAsync(id);
                            PersistPluginActiveState(id, active);
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Plugins] Failed to populate plugins: {ex.Message}");
        }
    }

    private void PersistPluginActiveState(string pluginId, bool isActive)
    {
        try
        {
            var profilePaths = new List<string>();

            if (!string.IsNullOrWhiteSpace(FryPdfPaths.ProfilesDirectory))
            {
                profilePaths.Add(System.IO.Path.Combine(FryPdfPaths.ProfilesDirectory, $"{ActiveProfileName}.profile.json"));
            }

            profilePaths.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "profiles", $"{ActiveProfileName}.profile.json"));
            profilePaths.Add($"profiles/{ActiveProfileName}.profile.json");

            PdfEditorApp.Core.Plugins.Profiles.PluginProfile? profile = null;
            string? targetPath = null;

            foreach (var path in profilePaths)
            {
                if (System.IO.File.Exists(path))
                {
                    try
                    {
                        profile = PdfEditorApp.Core.Plugins.Profiles.ProfileLoader.LoadFromFile(path);
                        targetPath = path;
                        break;
                    }
                    catch { }
                }
            }

            if (profile == null)
            {
                profile = new PdfEditorApp.Core.Plugins.Profiles.PluginProfile { ProfileName = ActiveProfileName };
                targetPath = profilePaths[0];
            }

            if (isActive)
            {
                profile.DisabledPlugins.RemoveAll(d => string.Equals(d, pluginId, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                if (!profile.DisabledPlugins.Any(d => string.Equals(d, pluginId, StringComparison.OrdinalIgnoreCase)))
                {
                    profile.DisabledPlugins.Add(pluginId);
                }
            }

            if (!string.IsNullOrWhiteSpace(targetPath))
            {
                PdfEditorApp.Core.Plugins.Profiles.ProfileLoader.SaveToFile(profile, targetPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainViewModel] Failed to persist plugin state for '{pluginId}': {ex.Message}");
        }
    }

    [ObservableProperty]
    private PluginItemViewModel? _selectedConfiguringPlugin;

    [ObservableProperty]
    private bool _isConfiguringPluginOpen;

    public ObservableCollection<PluginSettingItemViewModel> ActivePluginConfigSettings { get; } = new();

    [RelayCommand]
    public void OpenPluginSettings(PluginItemViewModel plugin)
    {
        if (plugin?.SettingsSchema == null || plugin.SettingsSchema.Count == 0) return;
        SelectedConfiguringPlugin = plugin;
        ActivePluginConfigSettings.Clear();

        var store = App.Services?.GetService<Core.Plugins.Settings.IPluginSettingsStore>();

        foreach (var (key, def) in plugin.SettingsSchema)
        {
            var item = new PluginSettingItemViewModel
            {
                Key = key,
                Label = string.IsNullOrWhiteSpace(def.Label) ? key : def.Label,
                Description = def.Description,
                Type = def.Type?.ToLowerInvariant() ?? "string",
                Options = def.Options ?? new()
            };

            if (store != null)
            {
                if (item.Type == "boolean")
                {
                    bool defBool = def.DefaultValue is bool b ? b : (bool.TryParse(def.DefaultValue?.ToString(), out var parsedB) && parsedB);
                    item.BoolValue = store.GetSetting(plugin.Id, key, defBool);
                }
                else if (item.Type == "number")
                {
                    double defNum = def.DefaultValue is double d ? d : (double.TryParse(def.DefaultValue?.ToString(), out var parsedD) ? parsedD : 0);
                    item.NumberValue = store.GetSetting(plugin.Id, key, defNum);
                }
                else
                {
                    string defStr = def.DefaultValue?.ToString() ?? "";
                    item.StringValue = store.GetSetting(plugin.Id, key, defStr);
                }
            }
            else
            {
                item.StringValue = def.DefaultValue?.ToString() ?? "";
                if (def.DefaultValue is bool b) item.BoolValue = b;
                if (def.DefaultValue is double d) item.NumberValue = d;
            }

            ActivePluginConfigSettings.Add(item);
        }

        IsConfiguringPluginOpen = true;
    }

    [RelayCommand]
    public void SavePluginSettings()
    {
        if (SelectedConfiguringPlugin == null) return;
        var store = App.Services?.GetService<Core.Plugins.Settings.IPluginSettingsStore>();
        if (store != null)
        {
            foreach (var item in ActivePluginConfigSettings)
            {
                if (item.Type == "boolean")
                {
                    store.SetSetting(SelectedConfiguringPlugin.Id, item.Key, item.BoolValue);
                }
                else if (item.Type == "number")
                {
                    store.SetSetting(SelectedConfiguringPlugin.Id, item.Key, item.NumberValue);
                }
                else
                {
                    store.SetSetting(SelectedConfiguringPlugin.Id, item.Key, item.StringValue);
                }
            }
            store.Save();
            ShowToast($"Saved preferences for {SelectedConfiguringPlugin.Name}!", ToastNotificationType.Success, "CheckCircle");
        }
        IsConfiguringPluginOpen = false;
    }

    [RelayCommand]
    public void ClosePluginSettings()
    {
        IsConfiguringPluginOpen = false;
    }

    [RelayCommand]
    public async System.Threading.Tasks.Task LoadExternalPluginDialogAsync()
    {
        if (StorageProvider == null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Plugin Package (.fryplugin) or Assembly (.dll)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("FryPDF Plugins (*.fryplugin; *.dll)")
                {
                    Patterns = new[] { "*.fryplugin", "*.dll" }
                },
                new FilePickerFileType("FryPDF Plugin Package (*.fryplugin)")
                {
                    Patterns = new[] { "*.fryplugin" }
                },
                new FilePickerFileType(".NET Assembly (*.dll)")
                {
                    Patterns = new[] { "*.dll" }
                }
            }
        });

        if (files.Count > 0)
        {
            var filePath = files[0].Path.LocalPath;
            await InstallAndMountPluginPathAsync(filePath);
        }
    }

    public async System.Threading.Tasks.Task InstallAndMountPluginPathAsync(string filePath)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var host = App.Services?.GetService<PluginHost>();
            if (host == null) return;

            IReadOnlyList<IFryPlugin> plugins;
            string displayName;

            if (string.Equals(System.IO.Path.GetExtension(filePath), ".fryplugin", StringComparison.OrdinalIgnoreCase))
            {
                var pkgResult = PdfEditorApp.Plugins.Loader.FryPluginPackageLoader.UnpackAndLoad(filePath);
                plugins = pkgResult.AssemblyPackage.Plugins;
                displayName = pkgResult.Manifest.Name;
            }
            else
            {
                var pkg = PdfEditorApp.Plugins.Loader.PluginAssemblyLoader.LoadPluginAssembly(filePath);
                plugins = pkg.Plugins;
                displayName = System.IO.Path.GetFileName(filePath);
            }

            if (plugins.Count == 0)
            {
                AppLogService.Instance.Log(AppLogLevel.Warning, "PluginInstall",
                    $"Drag/file install of '{filePath}' produced 0 IFryPlugin implementations after {sw.ElapsedMilliseconds}ms.");
                ShowToast("No IFryPlugin implementations found in package.", ToastNotificationType.Warning, "AlertCircleOutline");
                return;
            }

            foreach (var p in plugins)
            {
                host.RegisterPlugin(p);
            }
            await host.StartAsync();

            PopulateLoadedPlugins();
            FilterLoadedPlugins(PluginSearchQuery);
            AppLogService.Instance.Log(AppLogLevel.Info, "PluginInstall",
                $"Installed and mounted {plugins.Count} plugin(s) from '{displayName}' in {sw.ElapsedMilliseconds}ms.");
            ShowToast($"Successfully mounted {plugins.Count} plugin(s) from {displayName}!", ToastNotificationType.Success, "CheckCircle");
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogError("PluginInstall", $"Failed to load plugin from '{filePath}' after {sw.ElapsedMilliseconds}ms", ex);
            ShowToast($"Failed to load plugin: {ex.Message}", ToastNotificationType.Danger, "AlertCircleOutline");
        }
    }

    public ObservableCollection<CommandPaletteItem> FilteredPaletteCommands { get; } = new();
    public List<CommandPaletteItem> AllPaletteCommands { get; } = new();

    partial void OnCommandSearchQueryChanged(string value)
    {
        FilterPaletteCommands(value);
    }

    [RelayCommand]
    public void OpenCommandPalette()
    {
        InitCommandPalette();
        CommandSearchQuery = "";
        FilterPaletteCommands("");
        IsCommandPaletteOpen = true;
    }

    [RelayCommand]
    public void CloseCommandPalette()
    {
        IsCommandPaletteOpen = false;
        CommandSearchQuery = "";
    }

    [RelayCommand]
    public void OpenShortcutsHelp()
    {
        IsShortcutsHelpDialogOpen = true;
        OpenRegisteredDialog("frypdf.dialog.shortcuts");
    }

    [RelayCommand]
    public void CloseShortcutsHelp()
    {
        IsShortcutsHelpDialogOpen = false;
        CloseDynamicDialog();
    }

    [RelayCommand]
    public void OpenAboutDialog()
    {
        IsAboutDialogOpen = true;
        OpenRegisteredDialog("frypdf.dialog.about");
    }

    [RelayCommand]
    public void CloseAboutDialog()
    {
        IsAboutDialogOpen = false;
        CloseDynamicDialog();
    }

    [RelayCommand]
    public void NavigateToLicensing()
    {
        IsAboutDialogOpen = false;
        CloseDynamicDialog();
        IsHomePageVisible = true;
        IsEditorVisible = false;
        IsPdfViewerVisible = false;
        Home.SelectNavSectionCommand.Execute("Licensing");
    }

    [RelayCommand]
    public void NavigateToPluginsPage()
    {
        IsPluginsDialogOpen = false;
        CloseDynamicDialog();
        IsHomePageVisible = true;
        IsEditorVisible = false;
        IsPdfViewerVisible = false;
        Home.SelectNavSectionCommand.Execute("Plugins");
    }

    [RelayCommand]
    public void NavigateToHelp(string? topicId = null)
    {
        IsAboutDialogOpen = false;
        IsShortcutsHelpDialogOpen = false;
        CloseDynamicDialog();
        IsHomePageVisible = true;
        IsEditorVisible = false;
        IsPdfViewerVisible = false;
        Home.OpenHelpGuideCommand.Execute(topicId);
    }

    [RelayCommand]
    public void NavigateToSettings(string? category = null)
    {
        IsAboutDialogOpen = false;
        IsShortcutsHelpDialogOpen = false;
        CloseDynamicDialog();
        IsHomePageVisible = true;
        IsEditorVisible = false;
        IsPdfViewerVisible = false;
        Home.SelectNavSectionCommand.Execute("Settings");
        if (string.Equals(category, "Shortcuts", StringComparison.OrdinalIgnoreCase))
        {
            Home.Settings.SelectedCategory = SettingsCategory.Shortcuts;
        }
    }

    [RelayCommand]
    public async System.Threading.Tasks.Task CopySupportEmail()
    {
        const string email = "codefrydev@gmail.com";
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.Clipboard != null)
            {
                await desktop.MainWindow.Clipboard.SetTextAsync(email);
            }
        }
        catch { }
        ShowToast($"Copied support email: {email}", "EmailOutline");
    }

    [RelayCommand]
    public async System.Threading.Tasks.Task OpenCompanyWebsite()
    {
        const string url = "https://codefrydev.in";
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.Launcher != null)
            {
                await desktop.MainWindow.Launcher.LaunchUriAsync(new Uri(url));
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
        }
        catch
        {
            try
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.Clipboard != null)
                {
                    await desktop.MainWindow.Clipboard.SetTextAsync(url);
                }
            }
            catch { }
            ShowToast($"Copied website link: {url}", "Web");
            return;
        }
        ShowToast("Opening codefrydev.in...", "Web");
    }

    [RelayCommand]
    public async System.Threading.Tasks.Task OpenMicrosoftStore()
    {
        const string storeUrl = "https://apps.microsoft.com/detail/9P5GW2Q81B33";
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.Launcher != null)
            {
                await desktop.MainWindow.Launcher.LaunchUriAsync(new Uri(storeUrl));
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = storeUrl, UseShellExecute = true });
            }
        }
        catch
        {
            try
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.Clipboard != null)
                {
                    await desktop.MainWindow.Clipboard.SetTextAsync(storeUrl);
                }
            }
            catch { }
            ShowToast($"Copied Microsoft Store link: {storeUrl}", "Microsoft");
            return;
        }
        ShowToast("Opening Microsoft Store...", "Microsoft");
    }

    [RelayCommand]
    public async System.Threading.Tasks.Task OpenGitHub()
    {
        const string url = "https://github.com/codefrydev";
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.Launcher != null)
            {
                await desktop.MainWindow.Launcher.LaunchUriAsync(new Uri(url));
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
        }
        catch
        {
            try
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.Clipboard != null)
                {
                    await desktop.MainWindow.Clipboard.SetTextAsync(url);
                }
            }
            catch { }
            ShowToast($"Copied GitHub link: {url}", "Github");
            return;
        }
        ShowToast("Opening GitHub...", "Github");
    }

    [RelayCommand]
    public async System.Threading.Tasks.Task CopyDiagnostics()
    {
        var diagnostics = $"FryPDF Open-Source Edition v{AppVersion}\n" +
                          $"Publisher: Code Fry Dev (CN=7E83DE15-E15F-41B6-B068-989D9548D0BF)\n" +
                          $"Package Family Name: CodeFryDev.FryPDF_ntemjm2faw5zw\n" +
                          $"Store ID: 9P5GW2Q81B33 (https://apps.microsoft.com/detail/9P5GW2Q81B33)\n" +
                          $"MSA App ID: 4d091113-f7b6-4421-9318-220eb8b7234e\n" +
                          $"Website: https://codefrydev.in\n" +
                          $"GitHub: https://github.com/codefrydev\n" +
                          $"Support: codefrydev@gmail.com\n" +
                          $"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})\n" +
                          $"Runtime: .NET {Environment.Version}\n" +
                          $"Avalonia UI: 12.1.1\n" +
                          $"Engine: QuestPDF 2026.8.0 + SkiaSharp\n" +
                          $"Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.Clipboard != null)
            {
                await desktop.MainWindow.Clipboard.SetTextAsync(diagnostics);
            }
        }
        catch { }
        ShowToast("System diagnostics copied to clipboard", "InformationOutline");
    }

    public void SelectNextPaletteCommand()
    {
        if (FilteredPaletteCommands.Count == 0) return;
        SelectedPaletteIndex = (SelectedPaletteIndex + 1) % FilteredPaletteCommands.Count;
    }

    public void SelectPreviousPaletteCommand()
    {
        if (FilteredPaletteCommands.Count == 0) return;
        SelectedPaletteIndex = (SelectedPaletteIndex - 1 + FilteredPaletteCommands.Count) % FilteredPaletteCommands.Count;
    }

    public void ExecuteSelectedPaletteCommand()
    {
        if (SelectedPaletteIndex >= 0 && SelectedPaletteIndex < FilteredPaletteCommands.Count)
        {
            var item = FilteredPaletteCommands[SelectedPaletteIndex];
            CloseCommandPalette();
            item.Action?.Invoke();
        }
    }

    public void InitCommandPalette()
    {
        AllPaletteCommands.Clear();

        // 1. File Operations
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "AI Assistant: Generate Canvas Elements", Subtitle = "Prompt local Ollama or cloud AI to generate document elements", Category = "AI Studio", IconKind = "AutoFixHigh", Shortcut = "⌘I", Action = () => OpenAiAssistantCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Open PDF to Read (PDF Reader Mode)", Subtitle = "Acrobat-style distraction-free reading, bookmarks & text search", Category = "File", IconKind = "BookOpenPageVariantOutline", Action = () => OpenPdfReaderCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Save Project", Subtitle = "Save editable FryPDF project archive (.frypdf)", Category = "File", IconKind = "ContentSaveOutline", Shortcut = "⌘S", Action = () => SaveProjectCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Open Project", Subtitle = "Open existing FryPDF project archive (.frypdf)", Category = "File", IconKind = "FolderOpenOutline", Shortcut = "⌘O", Action = () => OpenProjectCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Rename Document...", Subtitle = "Rename the current document file or title", Category = "File", IconKind = "PencilOutline", Shortcut = "F2", Action = () => RenameCurrentDocumentCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Duplicate Document", Subtitle = "Create a copy of current document on disk", Category = "File", IconKind = "ContentCopy", Action = () => DuplicateCurrentDocumentCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Reveal in File Manager / Finder", Subtitle = "Highlight document file in native OS file explorer", Category = "File", IconKind = "FolderOpenOutline", Action = () => RevealCurrentDocumentInFileManagerCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Copy Document Path", Subtitle = "Copy absolute file path to clipboard", Category = "File", IconKind = "ClipboardTextOutline", Action = () => _ = CopyCurrentDocumentPathCommand.ExecuteAsync(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Delete Document File...", Subtitle = "Permanently delete document from disk and recents", Category = "File", IconKind = "TrashCanOutline", Action = () => DeleteCurrentDocumentCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "New Document / Templates", Subtitle = "Browse and create from executive templates", Category = "File", IconKind = "FilePlusOutline", Shortcut = "⌘N", Action = () => OpenNewDocumentDialogCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Export Production PDF", Subtitle = "Compile document to high-resolution vector PDF", Category = "File", IconKind = "FilePdfBox", Shortcut = "⌘E", Action = () => ExportPdfCommand.Execute(null) });

        // 2. Edit & History
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Undo Action", Subtitle = "Revert last canvas or page operation", Category = "Edit", IconKind = "Undo", Shortcut = "⌘Z", Action = () => UndoCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Redo Action", Subtitle = "Reapply reverted operation", Category = "Edit", IconKind = "Redo", Shortcut = "⌘Y", Action = () => RedoCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Copy Element", Subtitle = "Copy selected element to internal clipboard", Category = "Edit", IconKind = "ContentCopy", Shortcut = "⌘C", Action = () => CopyCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Cut Element", Subtitle = "Cut selected element to internal clipboard", Category = "Edit", IconKind = "ContentCut", Shortcut = "⌘X", Action = () => CutCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Paste Element", Subtitle = "Paste element from clipboard to current page", Category = "Edit", IconKind = "ContentPaste", Shortcut = "⌘V", Action = () => PasteCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Duplicate Element", Subtitle = "Clone selected element with offset", Category = "Edit", IconKind = "ContentDuplicate", Shortcut = "⌘D", Action = () => DuplicateCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Delete Selected Element", Subtitle = "Remove active element from canvas", Category = "Edit", IconKind = "DeleteOutline", Shortcut = "⌫", Action = () => Inspector.DeleteSelectedElementCommand.Execute(null) });

        // 3. Insert Elements
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Text Paragraph", Subtitle = "Add multi-line editable rich text block", Category = "Insert", IconKind = "FormatColorText", Shortcut = "T", Action = () => AddTextElementCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Section Heading", Subtitle = "Add bold Georgia 22pt section title", Category = "Insert", IconKind = "FormatHeader1", Shortcut = "H", Action = () => AddHeadingElementCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Image Graphic", Subtitle = "Import PNG, JPEG, or WebP graphic from disk", Category = "Insert", IconKind = "ImageOutline", Shortcut = "⌘I", Action = () => AddImageElementCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Divider Line", Subtitle = "Add horizontal section divider line", Category = "Insert", IconKind = "VectorLine", Action = () => AddDividerElementCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Data Table", Subtitle = "Add customizable multi-column data grid", Category = "Insert", IconKind = "TableLarge", Action = () => AddTableElementCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Watermark Overlay", Subtitle = "Add confidential watermark stamp", Category = "Insert", IconKind = "Watermark", Action = () => AddWatermarkElementCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Math Equation", Subtitle = "Add vector LaTeX mathematical equation / formula", Category = "Equations", IconKind = "Sigma", Shortcut = "⌘M", Action = () => AddMathElementCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Open Math Equation Studio", Subtitle = "Visual formula builder, live preview, and symbol palettes", Category = "Equations", IconKind = "FunctionVariant", Action = () => OpenMathStudioCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Gaussian Integral", Subtitle = "∫ e^(-x²) dx = √π definite integral", Category = "Equations", IconKind = "Sigma", Action = () => AddMathElementCommand.Execute("gaussian_integral") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Quadratic Formula", Subtitle = "x = (-b ± √(b² - 4ac)) / 2a", Category = "Equations", IconKind = "Calculator", Action = () => AddMathElementCommand.Execute("quadratic_formula") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Schrödinger Equation", Subtitle = "iℏ ∂Ψ/∂t = ĤΨ quantum wave equation", Category = "Equations", IconKind = "Atom", Action = () => AddMathElementCommand.Execute("schrodinger_time_dependent") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Black-Scholes PDE", Subtitle = "∂V/∂t + ½σ²S² ∂²V/∂S² + rS ∂V/∂S - rV = 0 derivative pricing", Category = "Equations", IconKind = "ChartLineVariant", Action = () => AddMathElementCommand.Execute("black_scholes_pde") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Normal Distribution PDF", Subtitle = "Gaussian bell-curve probability density function", Category = "Equations", IconKind = "ChartBellCurve", Action = () => AddMathElementCommand.Execute("normal_distribution") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Euler's Identity", Subtitle = "e^(iπ) + 1 = 0 fundamental constants relation", Category = "Equations", IconKind = "Pi", Action = () => AddMathElementCommand.Execute("eulers_identity") });

        // 4. Shapes & Stamps
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Rectangle Shape", Subtitle = "Add geometric rectangle with fill & stroke", Category = "Shapes", IconKind = "SquareOutline", Shortcut = "R", Action = () => AddShapeElementCommand.Execute("Rectangle") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Circle / Ellipse Shape", Subtitle = "Add circular vector shape", Category = "Shapes", IconKind = "CircleOutline", Action = () => AddShapeElementCommand.Execute("Circle") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert 5-Point Star", Subtitle = "Add decorative badge star", Category = "Shapes", IconKind = "StarOutline", Action = () => AddShapeElementCommand.Execute("Star5") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert 'APPROVED' Stamp", Subtitle = "Green legal certification stamp", Category = "Stamps", IconKind = "CheckCircleOutline", Action = () => AddStampElementCommand.Execute("Approved") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert 'CONFIDENTIAL' Stamp", Subtitle = "Red security classification stamp", Category = "Stamps", IconKind = "ShieldLockOutline", Action = () => AddStampElementCommand.Execute("Confidential") });

        // 5. Data & Charts
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Bar / Column Chart", Subtitle = "LiveCharts2 vertical bar comparison chart", Category = "Charts", IconKind = "ChartBar", Action = () => AddChartElementCommand.Execute("BarColumn") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Donut Pie Chart", Subtitle = "LiveCharts2 proportional breakdown donut chart", Category = "Charts", IconKind = "ChartPie", Action = () => AddChartElementCommand.Execute("DonutPie") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Smooth Spline Chart", Subtitle = "LiveCharts2 fluid bezier curve chart", Category = "Charts", IconKind = "ChartLineSmooth", Action = () => AddChartElementCommand.Execute("SmoothLine") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Area Gradient Chart", Subtitle = "LiveCharts2 filled area volume chart", Category = "Charts", IconKind = "ChartAreaspline", Action = () => AddChartElementCommand.Execute("Area") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Candlestick Chart", Subtitle = "LiveCharts2 OHLC stock and crypto financial chart", Category = "Charts", IconKind = "ChartCandlestick", Action = () => AddChartElementCommand.Execute("Candlestick") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Radar / Spider Chart", Subtitle = "LiveCharts2 multi-metric evaluation polygon", Category = "Charts", IconKind = "Radar", Action = () => AddChartElementCommand.Execute("Radar") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert KPI Gauge Dial", Subtitle = "LiveCharts2 speedometer metric gauge", Category = "Charts", IconKind = "Gauge", Action = () => AddChartElementCommand.Execute("GaugeProgress") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Waterfall Chart", Subtitle = "LiveCharts2 financial revenue-to-profit variance bridge", Category = "Charts", IconKind = "ChartWaterfall", Action = () => AddChartElementCommand.Execute("Waterfall") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Open Data Studio & Connector", Subtitle = "Ingest data from Excel (.xlsx), CSV, REST API, or Clipboard", Category = "Data", IconKind = "DatabaseArrowDownOutline", Action = () => OpenDataStudioCommand.Execute("NewChart") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Batch Mail Merge & Mass PDF", Subtitle = "Generate 100s of personalized PDFs (payslips, invoices, certificates) in 1-click", Category = "Data", IconKind = "DatabaseArrowDownOutline", Action = () => OpenBatchGenerationCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Mass Payslips Generator", Subtitle = "Generate Employee Monthly Payslips from salary data", Category = "Data", IconKind = "CashMultiple", Action = () => OpenBatchGenerationCommand.Execute("payslip") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Import Excel (.xlsx) to Chart", Subtitle = "Generate LiveCharts2 visualization from Excel workbook", Category = "Data", IconKind = "FileExcelOutline", Action = () => _ = ImportExcelToChartAsync() });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Import Excel (.xlsx) to Table", Subtitle = "Generate formatted Table from Excel workbook", Category = "Data", IconKind = "TableLarge", Action = () => _ = ImportExcelToTableAsync() });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Fetch REST API / JSON to Chart", Subtitle = "Live HTTP JSON endpoint ingestion for charts", Category = "Data", IconKind = "Api", Action = () => FetchRestApiToChartCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Fetch REST API / JSON to Table", Subtitle = "Live HTTP JSON endpoint ingestion for tables", Category = "Data", IconKind = "CodeJson", Action = () => FetchRestApiToTableCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Convert Table to Chart", Subtitle = "Transform selected Table into interactive LiveCharts2 chart", Category = "Data", IconKind = "ChartBoxOutline", Action = () => Inspector.ConvertTableToChartCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Convert Chart to Table", Subtitle = "Transform selected Chart into editable structured Table", Category = "Data", IconKind = "Table", Action = () => Inspector.ConvertChartToTableCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Vector QR Code", Subtitle = "Dynamic URL, Wi-Fi, or vCard QR generator", Category = "Data", IconKind = "Qrcode", Action = () => AddQrCodeElementCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Barcode", Subtitle = "Code128 / EAN / PDF417 optical barcode", Category = "Data", IconKind = "Barcode", Action = () => AddBarcodeElementCommand.Execute(null) });

        // 6. Security & Markup
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Sticky Review Note", Subtitle = "Collaborative annotation note", Category = "Markup", IconKind = "NoteTextOutline", Shortcut = "N", Action = () => AddStickyNoteElementCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Highlighter Stroke", Subtitle = "Yellow semi-transparent highlight marker", Category = "Markup", IconKind = "Marker", Shortcut = "H", Action = () => AddInkElementCommand.Execute(true) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Freehand Ink Drawing", Subtitle = "Freehand pen stroke vector element", Category = "Markup", IconKind = "DrawPen", Shortcut = "D", Action = () => AddInkElementCommand.Execute(false) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Insert Redaction Blackout Box", Subtitle = "Permanent FOIA / GDPR privileged blackout", Category = "Security", IconKind = "EyeOffOutline", Action = () => AddRedactionElementCommand.Execute("[REDACTED]") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Search & Redact Pattern", Subtitle = "Auto-redact text occurrences on current page", Category = "Security", IconKind = "DatabaseSearchOutline", Action = () => OpenSearchRedactDialogCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Burn In All Redactions", Subtitle = "Permanently commit solid blackouts to PDF", Category = "Security", IconKind = "ShieldCheckOutline", Action = () => BurnInAllRedactionsCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Document Security & Passwords", Subtitle = "Configure password protection and permissions", Category = "Security", IconKind = "ShieldLockOutline", Action = () => OpenSecurityDialogCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Sanitize Document", Subtitle = "Scrub author metadata and internal review notes", Category = "Security", IconKind = "ShieldCheck", Action = () => SanitizeDocumentCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Apply Bates Numbering", Subtitle = "Sequential legal discovery numbering (CONF-BATES-000001)", Category = "Security", IconKind = "Numeric", Action = () => ApplyBatesNumberingCommand.Execute(null) });

        // 7. Fill & Sign Studio
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Open Signature Studio", Subtitle = "Draw, type cursive calligraphy, or upload digital signature", Category = "Sign", IconKind = "Draw", Action = () => OpenSignatureStudioCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Stamp Today's Date", Subtitle = "Insert dynamic verified date badge", Category = "Sign", IconKind = "CalendarClockOutline", Action = () => AddDateStampCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Stamp Signer Initials", Subtitle = "Insert circular monogram initial stamp", Category = "Sign", IconKind = "AccountOutline", Action = () => AddInitialsBadgeCommand.Execute("JD") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Stamp Checkmark (✓)", Subtitle = "Insert green verification checkmark", Category = "Sign", IconKind = "CheckBold", Action = () => AddCheckmarkBadgeCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Stamp Cross (✕)", Subtitle = "Insert red rejection cross mark", Category = "Sign", IconKind = "CloseThick", Action = () => AddCrossBadgeCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Custom Stamp Creator", Subtitle = "Create timestamped custom legal certification stamp", Category = "Stamps", IconKind = "CertificateOutline", Action = () => OpenCustomStampDialogCommand.Execute(null) });

        // 8. Watermarks & Headers/Footers
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Header & Footer Studio", Subtitle = "Configure multi-zone header/footer with dynamic macros", Category = "Organize", IconKind = "PageLayoutHeaderFooter", Action = () => OpenHeaderFooterDialogCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Watermark Manager", Subtitle = "Apply CONFIDENTIAL/DRAFT watermark across all pages", Category = "Organize", IconKind = "Watermark", Action = () => OpenWatermarkManagerCommand.Execute(null) });

        // 9. Preflight Audit & Health Diagnostics
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Preflight Health Check & Audit", Subtitle = "Inspect PDF compliance, fonts, broken links, accessibility", Category = "Audit", IconKind = "FileCheckOutline", Action = () => OpenPreflightDialogCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Export Comments Summary", Subtitle = "Export all review notes to Markdown document", Category = "Audit", IconKind = "CommentTextMultipleOutline", Action = () => ExportCommentsSummaryCommand.Execute(null) });

        // 10. Pages & Navigation
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Add Blank Page", Subtitle = "Insert new page at end of document", Category = "Pages", IconKind = "FilePlusOutline", Shortcut = "⌘⇧N", Action = () => AddPageCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Duplicate Current Page", Subtitle = "Clone active page with all elements", Category = "Pages", IconKind = "FileMultipleOutline", Shortcut = "⌘⇧D", Action = () => DuplicateCurrentPageCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Rotate Page 90° Clockwise", Subtitle = "Rotate current page orientation", Category = "Pages", IconKind = "RotateRight", Shortcut = "⌘⇧R", Action = () => RotateCurrentPageCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Delete Current Page", Subtitle = "Remove active page from document", Category = "Pages", IconKind = "DeleteOutline", Shortcut = "⌘⇧⌫", Action = () => DeleteCurrentPageCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Next Page", Subtitle = "Go to next document page", Category = "Navigation", IconKind = "ChevronRight", Shortcut = "PgDn", Action = () => NextPageCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Previous Page", Subtitle = "Go to previous document page", Category = "Navigation", IconKind = "ChevronLeft", Shortcut = "PgUp", Action = () => PreviousPageCommand.Execute(null) });

        // 11. View, Workspace Panels & Guides
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Toggle Ribbon Toolbar", Subtitle = "Collapse or expand top ribbon tools panel", Category = "View", IconKind = "ViewAgendaOutline", Shortcut = "⌘F1", Action = () => ToggleRibbonCollapseCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Toggle Pages Sidebar", Subtitle = "Collapse or expand left thumbnails & outline sidebar", Category = "View", IconKind = "DockLeft", Shortcut = "⌘B", Action = () => ToggleLeftSidebarCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Toggle Properties Inspector", Subtitle = "Collapse or expand right formatting & properties panel", Category = "View", IconKind = "DockRight", Shortcut = "⌘⇧P", Action = () => ToggleInspectorCollapseCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Expand All Panels", Subtitle = "Restore all ribbon and sidebar panels", Category = "View", IconKind = "ViewQuiltOutline", Action = () => ExpandAllPanelsCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Toggle Canvas Grid", Subtitle = "Show/hide alignment grid dots", Category = "View", IconKind = "Grid", Action = () => ToggleGridCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Toggle Snap to Grid", Subtitle = "Snap elements to precise 20pt intervals", Category = "View", IconKind = "Magnet", Action = () => ToggleSnapToGridCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Zoom In", Subtitle = "Increase canvas scale by 10%", Category = "View", IconKind = "MagnifyPlusOutline", Shortcut = "⌘+", Action = () => ZoomInCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Zoom Out", Subtitle = "Decrease canvas scale by 10%", Category = "View", IconKind = "MagnifyMinusOutline", Shortcut = "⌘-", Action = () => ZoomOutCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Reset Zoom (100%)", Subtitle = "Reset canvas view to 1:1 scale", Category = "View", IconKind = "Magnify", Shortcut = "⌘0", Action = () => ResetZoomCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Fit to Width", Subtitle = "Scale page to fill viewport width", Category = "View", IconKind = "ArrowExpandHorizontal", Shortcut = "⌘1", Action = () => FitToWidthCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Fit to Page", Subtitle = "Scale page to view whole sheet", Category = "View", IconKind = "FitToPageOutline", Shortcut = "⌘9", Action = () => FitToPageCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Toggle Dark / Light Theme", Subtitle = "Switch between Dark and Light studio theme variants", Category = "Theme", IconKind = "ThemeLightDark", Shortcut = "⌘⇧T", Action = () => ToggleThemeCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Switch to Dark Theme", Subtitle = "Activate deep dark studio palette for low-light environments", Category = "Theme", IconKind = "WeatherNight", Action = () => SetDarkThemeCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Switch to Light Theme", Subtitle = "Activate crisp clean light studio palette", Category = "Theme", IconKind = "WeatherSunny", Action = () => SetLightThemeCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Use System Theme", Subtitle = "Follow operating system dark/light theme preference", Category = "Theme", IconKind = "Laptop", Action = () => SetSystemThemeCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Help & User Guides", Subtitle = "Open comprehensive guides for all 32 tools, editor & workflows", Category = "Help", IconKind = "HelpCircleOutline", Action = () => NavigateToHelpCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Help: 32 PDF Tools Guide", Subtitle = "Step-by-step guides, formats & pro tips for every PDF tool", Category = "Help", IconKind = "Tools", Action = () => NavigateToHelpCommand.Execute("tool-merge") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Help: Live Document Editor Guide", Subtitle = "Guide to canvas tools, typography, math LaTeX & vector layers", Category = "Help", IconKind = "Draw", Action = () => NavigateToHelpCommand.Execute("editor-canvas-basics") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Help: Batch PDF Generation Guide", Subtitle = "Guide to CSV/JSON Data Studio and mass PDF generation", Category = "Help", IconKind = "DatabaseArrowRightOutline", Action = () => NavigateToHelpCommand.Execute("automation-data-studio") });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Plugins & Extensions Studio", Subtitle = "Full-screen plugin store, installed extensions, and configuration studio", Category = "Preferences", IconKind = "PuzzleOutline", Action = () => NavigateToPluginsPageCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Plugins & Extensions (Quick Dialog)", Subtitle = "Quick popup to toggle plugins, profiles, and load assemblies", Category = "Help", IconKind = "PuzzleOutline", Action = () => OpenPluginsDialogCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "About FryPDF", Subtitle = "View app version, system info, open-source credits, and support", Category = "Help", IconKind = "InformationOutline", Action = () => OpenAboutDialogCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Keyboard Shortcuts Reference", Subtitle = "Open keyboard cheatsheet dialog", Category = "Help", IconKind = "KeyboardOutline", Shortcut = "F1", Action = () => OpenShortcutsHelpCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Open Source Licenses & Third-Party Tools", Subtitle = "View all 12 libraries, licenses, maintainers & attribution text", Category = "Help", IconKind = "CertificateOutline", Action = () => NavigateToLicensingCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Copy Open Source Attributions", Subtitle = "Copy full open source license notices & copyright text", Category = "Help", IconKind = "ContentCopy", Action = () => { _ = Home.CopyFullAttributionsCommand.ExecuteAsync(null); ShowToast("Copied open source license attributions", "CertificateOutline"); } });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Contact Support (codefrydev@gmail.com)", Subtitle = "Copy developer support email to clipboard", Category = "Help", IconKind = "EmailOutline", Action = () => CopySupportEmailCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Visit CodeFryDev Website", Subtitle = "Open official codefrydev.in developer portal", Category = "Help", IconKind = "Web", Action = () => OpenCompanyWebsiteCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "GitHub Repository", Subtitle = "View open-source repository and star the project", Category = "Help", IconKind = "Github", Action = () => OpenGitHubCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Microsoft Store Page", Subtitle = "View FryPDF on Microsoft Store (9P5GW2Q81B33)", Category = "Help", IconKind = "Microsoft", Action = () => OpenMicrosoftStoreCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Copy System Diagnostics", Subtitle = "Copy OS, framework, store identity, and app version report", Category = "Help", IconKind = "BugOutline", Action = () => CopyDiagnosticsCommand.Execute(null) });
        AllPaletteCommands.Add(new CommandPaletteItem { Title = "Settings & UI Preferences", Subtitle = "Configure notification placement, snackbar style, themes, and workspace defaults", Category = "Preferences", IconKind = "CogOutline", Shortcut = "⌘,", Action = () => NavigateToSettingsCommand.Execute(null) });

        // 12. Dynamic Plugin Tools
        var tools = _toolRegistry?.GetAllTools() ?? (IReadOnlyList<PdfToolDefinition>)Array.Empty<PdfToolDefinition>();
        foreach (var tool in tools)
        {
            var targetTool = tool;
            AllPaletteCommands.Add(new CommandPaletteItem
            {
                Title = $"Tool: {targetTool.Name}",
                Subtitle = targetTool.Description,
                Category = "PDF Tools",
                IconKind = targetTool.IconKind,
                Action = () => OpenTool(targetTool.Id)
            });
        }

        // 13. Dynamic Plugin Ribbon Actions
        var actions = _ribbonRegistry?.GetAllActions() ?? (IReadOnlyList<RibbonActionDescriptor>)Array.Empty<RibbonActionDescriptor>();
        foreach (var action in actions)
        {
            var targetAction = action;
            AllPaletteCommands.Add(new CommandPaletteItem
            {
                Title = targetAction.Label,
                Subtitle = targetAction.Tooltip ?? $"Contributed by plugin ({targetAction.TabId} tab)",
                Category = "Plugins",
                IconKind = targetAction.IconKind,
                Action = () => ExecuteRibbonAction(targetAction)
            });
        }

        // 14. Dynamic Plugin Contributed Commands
        var dynamicCommands = _commandPaletteRegistry?.GetAllCommands() ?? (IReadOnlyList<CommandPaletteDescriptor>)Array.Empty<CommandPaletteDescriptor>();
        foreach (var cmd in dynamicCommands)
        {
            var targetCmd = cmd;
            AllPaletteCommands.Add(new CommandPaletteItem
            {
                Title = targetCmd.Title,
                Subtitle = targetCmd.Subtitle,
                Category = targetCmd.Category,
                IconKind = targetCmd.IconKind,
                Shortcut = targetCmd.Shortcut ?? "",
                Action = () => targetCmd.Action?.Invoke(_pluginHost?.Context ?? (IServiceProvider)this)
            });
        }

        FilterPaletteCommands("");
    }

    public void FilterPaletteCommands(string query)
    {
        FilteredPaletteCommands.Clear();
        var q = query?.Trim() ?? "";

        var matching = string.IsNullOrEmpty(q)
            ? AllPaletteCommands
            : AllPaletteCommands.Where(c =>
                c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Shortcut.Contains(q, StringComparison.OrdinalIgnoreCase));

        foreach (var cmd in matching)
        {
            FilteredPaletteCommands.Add(cmd);
        }

        SelectedPaletteIndex = FilteredPaletteCommands.Count > 0 ? 0 : -1;
    }
}
