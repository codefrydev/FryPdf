using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using PdfEditorApp.Core.Plugins.Descriptors;
using PdfEditorApp.ViewModels;

namespace PdfEditorApp.Services.Overlays;

/// <summary>
/// Thread-safe registry and lifecycle manager for floating plugins targeting the 'shell.overlay' slot.
/// </summary>
public sealed class OverlayRegistry : IOverlayRegistry, IDisposable
{
    private readonly ConcurrentDictionary<string, OverlayDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, OverlayInstanceViewModel> _activeInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceProvider _serviceProvider;

    public event Action? RegistryChanged;
    public event Action? ActiveOverlaysChanged;

    public ObservableCollection<OverlayInstanceViewModel> ActiveOverlays { get; } = new();

    public OverlayRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public IDisposable RegisterOverlay(OverlayDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _descriptors[descriptor.Id] = descriptor;

        if (_activeInstances.TryGetValue(descriptor.Id, out var existingInstance))
        {
            try
            {
                var newContent = descriptor.ViewFactory != null
                    ? descriptor.ViewFactory(_serviceProvider)
                    : (descriptor.ViewType != null ? Activator.CreateInstance(descriptor.ViewType) : null);

                RunOnUIThread(() =>
                {
                    // Re-registration (plugin hot-reload) replaces the live view. Without this the
                    // outgoing view model was dropped on the floor still holding its resources.
                    var outgoing = existingInstance.Content;
                    existingInstance.Content = newContent;
                    if (!ReferenceEquals(outgoing, newContent))
                    {
                        OverlayInstanceViewModel.DisposeContent(outgoing);
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OverlayRegistry] Error refreshing view for {descriptor.Id}: {ex.Message}");
            }
        }

        RegistryChanged?.Invoke();

        return new UnregisterDisposable(() =>
        {
            UnregisterOverlay(descriptor.Id);
        });
    }

    public bool UnregisterOverlay(string overlayId)
    {
        if (_descriptors.TryRemove(overlayId, out _))
        {
            HideOverlay(overlayId);
            DisposeInstance(overlayId);
            RegistryChanged?.Invoke();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Removes a cached instance from <see cref="_activeInstances"/> and disposes it.
    /// </summary>
    /// <remarks>
    /// Only called when the overlay goes away for good — the plugin is unregistered, or the
    /// app is shutting down. Merely hiding an overlay keeps the instance cached; see
    /// <see cref="HideOverlay"/>.
    /// </remarks>
    private void DisposeInstance(string overlayId)
    {
        var desc = GetOverlay(overlayId);
        var targetId = desc?.Id ?? overlayId;

        if (_activeInstances.TryRemove(targetId, out var instance))
        {
            RunOnUIThread(instance.Dispose);
        }
    }

    public OverlayDescriptor? GetOverlay(string overlayId)
    {
        if (_descriptors.TryGetValue(overlayId, out var descriptor))
        {
            return descriptor;
        }

        if (!overlayId.StartsWith("frypdf.overlay.", StringComparison.OrdinalIgnoreCase))
        {
            if (_descriptors.TryGetValue($"frypdf.overlay.{overlayId}", out descriptor))
            {
                return descriptor;
            }
        }

        return null;
    }

    public IReadOnlyList<OverlayDescriptor> GetAllOverlays()
    {
        return _descriptors.Values.OrderBy(d => d.Title).ToList();
    }

    public void ShowOverlay(string overlayId)
    {
        var desc = GetOverlay(overlayId);
        if (desc == null) return;

        UpdateStoreOverlayState(desc.Id, true);

        if (_activeInstances.TryGetValue(desc.Id, out var existingInstance))
        {
            RunOnUIThread(() =>
            {
                if (existingInstance.Content == null)
                {
                    existingInstance.Content = desc.ViewFactory != null
                        ? desc.ViewFactory(_serviceProvider)
                        : (desc.ViewType != null ? Activator.CreateInstance(desc.ViewType) : null);
                }
                existingInstance.IsVisible = true;
                existingInstance.IsMinimized = false;
                if (!ActiveOverlays.Contains(existingInstance))
                {
                    ActiveOverlays.Add(existingInstance);
                }
                ActiveOverlaysChanged?.Invoke();
            });
            return;
        }

        object? content = null;
        if (desc.ViewFactory != null)
        {
            try
            {
                content = desc.ViewFactory(_serviceProvider);
            }
            catch (Exception ex)
            {
                AppLogService.Instance?.LogError("OverlayRegistry", $"Error creating view for '{desc.Id}' via ViewFactory", ex);
            }
        }
        else if (desc.ViewType != null)
        {
            try
            {
                content = Activator.CreateInstance(desc.ViewType);
            }
            catch (Exception ex)
            {
                AppLogService.Instance?.LogError("OverlayRegistry", $"Error instantiating ViewType {desc.ViewType} for '{desc.Id}'", ex);
            }
        }

        if (content == null)
        {
            AppLogService.Instance?.Log(AppLogLevel.Error, "OverlayRegistry", $"Overlay content resolved to null for '{desc.Id}'.");
        }

        var instance = new OverlayInstanceViewModel(desc, inst =>
        {
            HideOverlay(inst.Id);
        })
        {
            Content = content,
            X = desc.InitialX ?? 0,
            Y = desc.InitialY ?? 0,
            IsVisible = true,
            IsMinimized = false
        };

        if (_activeInstances.TryAdd(desc.Id, instance))
        {
            RunOnUIThread(() =>
            {
                if (!ActiveOverlays.Contains(instance))
                {
                    ActiveOverlays.Add(instance);
                }
                ActiveOverlaysChanged?.Invoke();
            });
        }
    }

    /// <summary>
    /// Hides an overlay, keeping its instance cached for an instant re-open.
    /// </summary>
    /// <remarks>
    /// This used to remove the instance from <see cref="_activeInstances"/> without disposing
    /// it. <see cref="ShowOverlay"/> then no longer found it and fell through to
    /// <c>desc.ViewFactory</c>, building a brand new view and view model — so every
    /// hide/show cycle permanently leaked one plugin instance, and the orphan could not even
    /// be collected because its own running DispatcherTimer rooted it. For the music player
    /// that meant an extra native audio engine, an extra open playback device and an extra
    /// pair of UI-thread timers per toggle, which is why audio degraded the longer the app ran.
    ///
    /// Keeping the instance cached fixes the leak and satisfies the view-caching rule in
    /// .agents/rules/performance_and_zero_lag_mandate.md section 2. Instances are torn down in
    /// <see cref="DisposeInstance"/> when the overlay is unregistered or the registry is
    /// disposed. <see cref="IsOverlayVisible"/> already keys off <c>IsVisible</c> rather than
    /// dictionary membership, so toggle semantics are unchanged.
    /// </remarks>
    public void HideOverlay(string overlayId)
    {
        var desc = GetOverlay(overlayId);
        var targetId = desc?.Id ?? overlayId;

        if (_activeInstances.TryGetValue(targetId, out var instance))
        {
            UpdateStoreOverlayState(targetId, false, instance.X, instance.Y);
            RunOnUIThread(() =>
            {
                instance.IsVisible = false;
                ActiveOverlays.Remove(instance);
                ActiveOverlaysChanged?.Invoke();
            });
        }
        else
        {
            UpdateStoreOverlayState(targetId, false);
        }
    }

    private void UpdateStoreOverlayState(string overlayId, bool isOpen, double? x = null, double? y = null)
    {
        try
        {
            var store = _serviceProvider.GetService(typeof(PdfEditorApp.Core.Plugins.Marketplace.IInstalledPluginStore)) as PdfEditorApp.Core.Plugins.Marketplace.IInstalledPluginStore;
            if (store == null) return;

            store.UpdateOverlayState(overlayId, isOpen, x, y);
            if (overlayId.StartsWith("frypdf.overlay.", StringComparison.OrdinalIgnoreCase))
            {
                var stripped = overlayId["frypdf.overlay.".Length..];
                store.UpdateOverlayState(stripped, isOpen, x, y);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OverlayRegistry] Error updating overlay state in store: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread, or inline if already on it.
    /// </summary>
    /// <remarks>
    /// The actions passed here mutate <c>ActiveOverlays</c> — an ObservableCollection bound to
    /// the shell — and set Content on live views. The marshal used to be gated on the
    /// application lifetime being desktop and otherwise fell through to an inline call, so a
    /// background plugin install could mutate the bound collection off-thread. The only
    /// legitimate reason to run inline is that no dispatcher loop exists at all (headless
    /// tests), which is what the catch below covers.
    /// </remarks>
    /// <summary>
    /// True when an Avalonia application exists, i.e. there is a dispatcher loop that will
    /// actually pump a posted action. In a unit-test host there is no application, and
    /// Dispatcher.UIThread.Post succeeds silently without ever running the callback.
    /// </summary>
    private static bool HasDispatcherLoop => Avalonia.Application.Current != null;

    private static void RunOnUIThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess() || !HasDispatcherLoop)
        {
            action();
            return;
        }

        // Previously gated on the lifetime being IClassicDesktopStyleApplicationLifetime and
        // otherwise fell through to an inline call, so a background plugin install under any
        // other lifetime mutated the bound ActiveOverlays collection off-thread.
        Dispatcher.UIThread.Post(action);
    }

    public void ToggleOverlay(string overlayId)
    {
        if (IsOverlayVisible(overlayId))
        {
            HideOverlay(overlayId);
        }
        else
        {
            ShowOverlay(overlayId);
        }
    }

    public bool IsOverlayVisible(string overlayId)
    {
        var desc = GetOverlay(overlayId);
        var targetId = desc?.Id ?? overlayId;
        return _activeInstances.TryGetValue(targetId, out var inst) && inst.IsVisible;
    }

    /// <summary>
    /// Tears down every cached overlay instance, releasing plugin-held resources on shutdown.
    /// </summary>
    public void Dispose()
    {
        foreach (var id in _activeInstances.Keys.ToList())
        {
            if (_activeInstances.TryRemove(id, out var instance))
            {
                instance.Dispose();
            }
        }

        _activeInstances.Clear();

        RunOnUIThread(() =>
        {
            ActiveOverlays.Clear();
            ActiveOverlaysChanged?.Invoke();
        });
    }

    private sealed class UnregisterDisposable : IDisposable
    {
        private Action? _action;

        public UnregisterDisposable(Action action)
        {
            _action = action;
        }

        public void Dispose()
        {
            var action = System.Threading.Interlocked.Exchange(ref _action, null);
            action?.Invoke();
        }
    }
}
