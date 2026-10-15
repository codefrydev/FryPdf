using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using PdfEditorApp.Core.Models;
using PdfEditorApp.Core.Plugins.Descriptors;
using PdfEditorApp.Core.Plugins.Settings;
using PdfEditorApp.Services;
using PdfEditorApp.ViewModels;
using Fw = FryWorld.Sdk.Services;

namespace PdfEditorApp.Plugins.FryWorldBridge;

/// <summary>
/// <see cref="Fw.IShellService"/> for FryWorld.Sdk plugins running in FryPDF: pages open in the home shell's
/// navigation, overlays float over the canvas, dialogs open through FryPDF's dialog registry.
/// </summary>
public sealed class FryPdfShellService : Fw.IShellService
{
    private readonly INavigationRegistry _navigation;
    private readonly IOverlayRegistry _overlays;
    private readonly IDialogRegistry _dialogs;
    private readonly Action<Action> _onUiThread;

    public FryPdfShellService(INavigationRegistry navigation, IOverlayRegistry overlays, IDialogRegistry dialogs)
        : this(navigation, overlays, dialogs, DispatchToUiThread)
    {
    }

    /// <summary>Creates the service with a custom way of running UI work, e.g. inline in tests.</summary>
    public FryPdfShellService(INavigationRegistry navigation, IOverlayRegistry overlays, IDialogRegistry dialogs, Action<Action> onUiThread)
    {
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _overlays = overlays ?? throw new ArgumentNullException(nameof(overlays));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _onUiThread = onUiThread ?? throw new ArgumentNullException(nameof(onUiThread));
    }

    // MainViewModel is transient; the one that matters is the main window's.
    private static MainViewModel? Shell =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.DataContext as MainViewModel;

    private static void DispatchToUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private void OnUiThread(Action action) => _onUiThread(action);

    public bool NavigateTo(string navigationItemId)
    {
        if (_navigation.GetItem(navigationItemId) == null) return false;

        OnUiThread(() =>
        {
            var shell = Shell;
            if (shell == null) return;
            shell.NavigateToHome();
            shell.Home.SelectNavSection(navigationItemId);
        });
        return true;
    }

    public bool ShowOverlay(string overlayId)
    {
        if (_overlays.GetOverlay(overlayId) == null) return false;
        OnUiThread(() => _overlays.ShowOverlay(overlayId));
        return true;
    }

    public bool HideOverlay(string overlayId)
    {
        if (_overlays.GetOverlay(overlayId) == null) return false;
        OnUiThread(() => _overlays.HideOverlay(overlayId));
        return true;
    }

    public bool ToggleOverlay(string overlayId)
    {
        if (_overlays.GetOverlay(overlayId) == null) return false;
        OnUiThread(() => _overlays.ToggleOverlay(overlayId));
        return true;
    }

    public bool IsOverlayVisible(string overlayId) => _overlays.IsOverlayVisible(overlayId);

    public Task<bool> ShowDialogAsync(string dialogId)
    {
        if (_dialogs.GetDialog(dialogId) == null) return Task.FromResult(false);
        OnUiThread(() => Shell?.OpenRegisteredDialog(dialogId));
        return Task.FromResult(true);
    }
}

/// <summary>
/// FryWorld.Sdk's <see cref="Fw.ISettingsManager"/> over FryPDF's plugin settings store, so FryWorld plugins'
/// <c>ctx.GetPluginSettings()</c> saves where FryPDF's plugin manager reads.
/// </summary>
public sealed class FryPdfSettingsAdapter : Fw.ISettingsManager
{
    private readonly IPluginSettingsStore _store;

    public FryPdfSettingsAdapter(IPluginSettingsStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    public event Action<string, string>? SettingChanged;

    public T GetSetting<T>(string pluginId, string settingKey, T defaultValue) => _store.GetSetting(pluginId, settingKey, defaultValue);

    public void SetSetting<T>(string pluginId, string settingKey, T value)
    {
        _store.SetSetting(pluginId, settingKey, value);
        SettingChanged?.Invoke(pluginId, settingKey);
    }

    public Task SaveAsync() => Task.Run(_store.Save);
}

/// <summary>
/// FryWorld.Sdk's <see cref="Fw.IThemeService"/> over FryPDF's theme service.
/// </summary>
public sealed class FryPdfThemeAdapter : Fw.IThemeService
{
    private readonly IThemeService _theme;

    public FryPdfThemeAdapter(IThemeService theme)
    {
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _theme.ThemeChanged += mode => ThemeChanged?.Invoke(ToFryWorld(mode));
    }

    public Fw.AppTheme CurrentTheme => ToFryWorld(_theme.CurrentTheme);

    public event Action<Fw.AppTheme>? ThemeChanged;

    public void SetTheme(Fw.AppTheme theme) => _theme.SetTheme(theme switch
    {
        Fw.AppTheme.Dark => AppThemeMode.Dark,
        Fw.AppTheme.Light => AppThemeMode.Light,
        _ => AppThemeMode.System
    });

    public void ToggleTheme() => _theme.ToggleTheme();

    private static Fw.AppTheme ToFryWorld(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Dark => Fw.AppTheme.Dark,
        AppThemeMode.Light => Fw.AppTheme.Light,
        _ => Fw.AppTheme.System
    };
}

/// <summary>
/// Posts to Avalonia's UI thread, so the FryWorld plugin host raises registry events where FryPDF's registries
/// and bound collections live.
/// </summary>
public sealed class UiThreadSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => Dispatcher.UIThread.Post(() => d(state));

    public override void Send(SendOrPostCallback d, object? state) => Dispatcher.UIThread.Invoke(() => d(state));

    public override SynchronizationContext CreateCopy() => this;
}

/// <summary>
/// Sends the FryWorld plugin host's log, including every plugin load and mount diagnostic, to FryPDF's app log.
/// </summary>
public sealed class FryPdfLogger : Microsoft.Extensions.Logging.ILogger
{
    private readonly string _category;

    public FryPdfLogger(string category) => _category = category;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Information;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        switch (logLevel)
        {
            case Microsoft.Extensions.Logging.LogLevel.Error or Microsoft.Extensions.Logging.LogLevel.Critical:
                if (exception != null) AppLogService.Instance.LogError(_category, message, exception);
                else AppLogService.Instance.Log(AppLogLevel.Error, _category, message);
                break;
            case Microsoft.Extensions.Logging.LogLevel.Warning:
                AppLogService.Instance.LogWarning(_category, message, exception);
                break;
            default:
                AppLogService.Instance.Log(AppLogLevel.Info, _category, message);
                break;
        }
    }
}
