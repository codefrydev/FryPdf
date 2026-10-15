using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using PdfEditorApp.Services;
using PdfEditorApp.ViewModels;
using Fp = PdfEditorApp.Core.Plugins.Descriptors;
using Fw = FryWorld.Sdk.Descriptors;
using FwPlugins = FryWorld.Sdk.Plugins;

namespace PdfEditorApp.Plugins.FryWorldBridge;

/// <summary>
/// Shows FryWorld.Sdk plugins in FryPDF. Everything they register in the FryWorld plugin host (pages, overlays,
/// commands, sidebars, inspectors, dialogs, shortcuts, ribbon tabs, status bar widgets) is mirrored into
/// FryPDF's own registries, which FryPDF's UI already renders, and removed again when the plugin unmounts.
/// </summary>
/// <remarks>
/// FryPDF's built-in features and its existing plugins keep running on FryPDF's own plugin runtime; this bridge
/// only adds FryWorld plugins on top, so nothing about them changes.
/// </remarks>
public sealed class FryWorldPluginBridge : IDisposable
{
    private const string LogCategory = "FryWorldBridge";

    private readonly FwPlugins.IFryPluginContext _fryWorld;
    private readonly IServiceProvider _services;
    private readonly List<IMirror> _mirrors = new();
    private readonly List<Action> _unsubscribe = new();
    private bool _attached;

    public FryWorldPluginBridge(FwPlugins.PluginHost fryWorldHost, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(fryWorldHost);
        _fryWorld = fryWorldHost.Context;
        _services = services ?? throw new ArgumentNullException(nameof(services));

        AddMirror<Fw.NavigationItemDescriptor, Fp.INavigationRegistry>(
            ctx => ctx.GetRegisteredNavigationItems(), (r, d) => r.RegisterNavigationItem(ToFryPdf(d)),
            h => _fryWorld.NavigationItemsChanged += h, h => _fryWorld.NavigationItemsChanged -= h);

        AddMirror<Fw.OverlayDescriptor, Fp.IOverlayRegistry>(
            ctx => ctx.GetRegisteredOverlays(), (r, d) => r.RegisterOverlay(ToFryPdf(d)),
            h => _fryWorld.OverlaysChanged += h, h => _fryWorld.OverlaysChanged -= h);

        AddMirror<Fw.CommandPaletteDescriptor, Fp.ICommandPaletteRegistry>(
            ctx => ctx.GetRegisteredCommands(), (r, d) => r.RegisterCommand(ToFryPdf(d)),
            h => _fryWorld.CommandsChanged += h, h => _fryWorld.CommandsChanged -= h);

        AddMirror<Fw.StatusBarWidgetDescriptor, Fp.IStatusBarRegistry>(
            ctx => ctx.GetRegisteredStatusBarWidgets(), (r, d) => r.RegisterWidget(ToFryPdf(d)),
            h => _fryWorld.StatusBarWidgetsChanged += h, h => _fryWorld.StatusBarWidgetsChanged -= h);

        AddMirror<Fw.SidebarTabDescriptor, Fp.ISidebarRegistry>(
            ctx => ctx.GetRegisteredSidebarTabs(), (r, d) => r.RegisterTab(ToFryPdf(d)),
            h => _fryWorld.SidebarsChanged += h, h => _fryWorld.SidebarsChanged -= h);

        AddMirror<Fw.InspectorSectionDescriptor, Fp.IInspectorRegistry>(
            ctx => ctx.GetRegisteredInspectorSections(), (r, d) => r.RegisterSection(ToFryPdf(d)),
            h => _fryWorld.InspectorsChanged += h, h => _fryWorld.InspectorsChanged -= h);

        AddMirror<Fw.DialogDescriptor, Fp.IDialogRegistry>(
            ctx => ctx.GetRegisteredDialogs(), (r, d) => r.RegisterDialog(ToFryPdf(d)),
            h => _fryWorld.DialogsChanged += h, h => _fryWorld.DialogsChanged -= h);

        AddMirror<Fw.ShortcutDescriptor, Fp.IShortcutRegistry>(
            ctx => ctx.GetRegisteredShortcuts(), (r, d) => r.RegisterShortcut(ToFryPdf(d)),
            h => _fryWorld.ShortcutsChanged += h, h => _fryWorld.ShortcutsChanged -= h);

        AddMirror<Fw.RibbonTabDescriptor, Fp.IRibbonRegistry>(
            ctx => ctx.GetRegisteredRibbonTabs(), RegisterRibbonTab,
            h => _fryWorld.RibbonChanged += h, h => _fryWorld.RibbonChanged -= h);
    }

    /// <summary>Starts mirroring. Registrations made before this call are picked up too.</summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;

        foreach (var mirror in _mirrors)
        {
            _unsubscribe.Add(mirror.Subscribe());
            mirror.Sync();
        }
    }

    public void Dispose()
    {
        foreach (var unsubscribe in _unsubscribe) unsubscribe();
        _unsubscribe.Clear();
        foreach (var mirror in _mirrors) mirror.Clear();
    }

    private void AddMirror<TDescriptor, TRegistry>(
        Func<FwPlugins.IFryPluginContext, IReadOnlyList<TDescriptor>> read,
        Func<TRegistry, TDescriptor, IDisposable> register,
        Action<Action> subscribe,
        Action<Action> unsubscribe)
        where TDescriptor : class
        where TRegistry : class
    {
        var registry = _services.GetService<TRegistry>();
        if (registry == null)
        {
            AppLogService.Instance.LogWarning(LogCategory, $"{typeof(TRegistry).Name} is not available; FryWorld plugins' {typeof(TDescriptor).Name}s will not show.");
            return;
        }

        _mirrors.Add(new Mirror<TDescriptor>(() => read(_fryWorld), d => register(registry, d), subscribe, unsubscribe));
    }

    // --- FryWorld descriptor -> FryPDF descriptor ---

    private static Func<IServiceProvider, object> View(Func<Control> factory) => _ => factory();

    private static Func<IServiceProvider, object>? ViewModel(Func<object?>? factory) =>
        factory == null ? null : _ => factory()!;

    internal static Fp.NavigationItemDescriptor ToFryPdf(Fw.NavigationItemDescriptor d) => new()
    {
        Id = d.Id,
        Title = d.Title,
        Group = d.Category ?? "Extensions",
        IconKind = d.IconKind ?? "ApplicationOutline",
        Order = d.Order,
        ViewFactory = View(d.ViewFactory),
        ViewModelFactory = ViewModel(d.ViewModelFactory),
        // FryWorld pages are full-viewport workspaces.
        DisplayMode = Fp.NavigationDisplayMode.FullViewport
    };

    internal static Fp.OverlayDescriptor ToFryPdf(Fw.OverlayDescriptor d) => new()
    {
        Id = d.Id,
        Title = d.Title,
        IconKind = d.IconKind ?? "WindowRestore",
        DefaultWidth = d.DefaultWidth,
        DefaultHeight = d.DefaultHeight,
        MinWidth = d.MinWidth,
        MinHeight = d.MinHeight,
        IsResizable = d.IsResizable,
        AutoOpenOnStartup = d.AutoOpenOnMount,
        ChromeMode = Enum.TryParse<Fp.OverlayChromeMode>(d.ChromeMode.ToString(), out var chrome) ? chrome : Fp.OverlayChromeMode.StandardCard,
        ViewFactory = View(d.ViewFactory),
        ViewModelFactory = ViewModel(d.ViewModelFactory)
    };

    internal static Fp.CommandPaletteDescriptor ToFryPdf(Fw.CommandPaletteDescriptor d) => new()
    {
        Id = d.Id,
        Title = d.Title,
        Subtitle = d.Subtitle ?? string.Empty,
        Category = d.Category ?? "Plugins",
        IconKind = d.IconKind ?? "LightningBolt",
        Shortcut = d.ShortcutKey,
        Action = _ => Run(d.Id, d.AsyncAction, d.Command)
    };

    internal static Fp.StatusBarWidgetDescriptor ToFryPdf(Fw.StatusBarWidgetDescriptor d) => new()
    {
        WidgetId = d.Id,
        ToolTip = d.Tooltip ?? string.Empty,
        Alignment = d.AlignRight ? Fp.StatusBarAlignment.Right : Fp.StatusBarAlignment.Left,
        Order = d.Order,
        // FryPDF renders status widgets from view models; external plugins follow its placement policy.
        IsExternal = true,
        Factory = _ => new StatusBarWidgetViewModel
        {
            WidgetId = d.Id,
            Label = d.Label,
            ToolTip = d.Tooltip,
            IconKind = d.IconKind,
            Content = d.CustomControlFactory?.Invoke() ?? (object?)d.Label,
            Command = d.ClickCommand
        }
    };

    internal static Fp.SidebarTabDescriptor ToFryPdf(Fw.SidebarTabDescriptor d) => new()
    {
        Id = d.Id,
        Title = d.Title,
        IconKind = d.IconKind ?? "DockLeft",
        Order = d.Order,
        ViewFactory = View(d.ViewFactory)
    };

    internal static Fp.InspectorSectionDescriptor ToFryPdf(Fw.InspectorSectionDescriptor d) => new()
    {
        SectionId = d.Id,
        Title = d.Title,
        Order = d.Order,
        Factory = (_, _) => d.ViewFactory()
    };

    internal static Fp.DialogDescriptor ToFryPdf(Fw.DialogDescriptor d) => new()
    {
        Id = d.Id,
        Title = d.Title,
        PreferredWidth = d.DefaultWidth,
        PreferredHeight = d.DefaultHeight,
        ViewFactory = View(d.ViewFactory)
    };

    internal static Fp.ShortcutDescriptor ToFryPdf(Fw.ShortcutDescriptor d) => new()
    {
        Id = d.Id,
        Title = d.Description,
        Description = d.Description,
        Category = "Plugins",
        DefaultGesture = d.KeyGesture,
        Action = _ => d.Command?.Execute(null),
        CanExecute = d.Command == null ? null : _ => d.Command.CanExecute(null)
    };

    /// <summary>A FryWorld ribbon tab is a FryPDF tab, one group per action group, and the actions.</summary>
    private static IDisposable RegisterRibbonTab(Fp.IRibbonRegistry ribbon, Fw.RibbonTabDescriptor tab)
    {
        var handles = new List<IDisposable>
        {
            ribbon.RegisterTab(new Fp.RibbonTabDescriptor { Id = tab.Id, Title = tab.Header, Order = tab.Order })
        };

        foreach (var group in tab.Actions.GroupBy(a => a.GroupId ?? "default"))
        {
            var groupId = $"{tab.Id}.{group.Key}";
            handles.Add(ribbon.RegisterGroup(new Fp.RibbonGroupDescriptor { Id = groupId, TabId = tab.Id, Title = group.Key }));

            foreach (var action in group)
            {
                ribbon.RegisterAction(new Fp.RibbonActionDescriptor
                {
                    Id = action.Id,
                    TabId = tab.Id,
                    GroupId = groupId,
                    Label = action.Label,
                    Tooltip = action.Tooltip,
                    IconKind = action.IconKind ?? "Puzzle",
                    Order = action.Order,
                    Action = _ => action.Command?.Execute(null)
                });
                var actionId = action.Id;
                handles.Add(new Disposable(() => ribbon.UnregisterAction(actionId)));
            }
        }

        handles.Reverse();
        return new Disposable(() => handles.ForEach(h => h.Dispose()));
    }

    private static void Run(string id, Func<System.Threading.Tasks.Task>? asyncAction, System.Windows.Input.ICommand? command)
    {
        try
        {
            if (asyncAction != null)
            {
                _ = asyncAction().ContinueWith(
                    t => AppLogService.Instance.LogError(LogCategory, $"Command '{id}' failed", t.Exception!),
                    System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
            }
            else if (command?.CanExecute(null) == true)
            {
                command.Execute(null);
            }
        }
        catch (Exception ex)
        {
            AppLogService.Instance.LogError(LogCategory, $"Command '{id}' failed", ex);
        }
    }

    // --- Mirroring ---

    private interface IMirror
    {
        Action Subscribe();
        void Sync();
        void Clear();
    }

    /// <summary>
    /// Keeps one FryPDF registration per FryWorld descriptor instance: registers new ones and disposes the
    /// registrations of descriptors that are gone.
    /// </summary>
    private sealed class Mirror<T> : IMirror where T : class
    {
        private readonly Func<IReadOnlyList<T>> _read;
        private readonly Func<T, IDisposable> _register;
        private readonly Action<Action> _subscribe;
        private readonly Action<Action> _unsubscribe;
        private readonly Dictionary<T, IDisposable> _mirrored = new(ReferenceEqualityComparer.Instance);
        private readonly object _lock = new();

        public Mirror(Func<IReadOnlyList<T>> read, Func<T, IDisposable> register, Action<Action> subscribe, Action<Action> unsubscribe)
        {
            _read = read;
            _register = register;
            _subscribe = subscribe;
            _unsubscribe = unsubscribe;
        }

        public Action Subscribe()
        {
            _subscribe(Sync);
            return () => _unsubscribe(Sync);
        }

        public void Sync()
        {
            lock (_lock)
            {
                var current = _read();
                var live = new HashSet<T>(current, ReferenceEqualityComparer.Instance);

                foreach (var (descriptor, handle) in _mirrored.ToList())
                {
                    if (live.Contains(descriptor)) continue;
                    _mirrored.Remove(descriptor);
                    TryDispose(handle);
                }

                foreach (var descriptor in current)
                {
                    if (_mirrored.ContainsKey(descriptor)) continue;
                    try
                    {
                        _mirrored[descriptor] = _register(descriptor);
                    }
                    catch (Exception ex)
                    {
                        AppLogService.Instance.LogWarning(LogCategory, $"Could not show a FryWorld plugin's {typeof(T).Name} in FryPDF", ex);
                    }
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                foreach (var handle in _mirrored.Values) TryDispose(handle);
                _mirrored.Clear();
            }
        }

        private static void TryDispose(IDisposable handle)
        {
            try
            {
                handle.Dispose();
            }
            catch (Exception ex)
            {
                AppLogService.Instance.LogWarning(LogCategory, "Removing a mirrored registration failed", ex);
            }
        }
    }

    private sealed class Disposable : IDisposable
    {
        private Action? _dispose;
        public Disposable(Action dispose) => _dispose = dispose;
        public void Dispose() => System.Threading.Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
