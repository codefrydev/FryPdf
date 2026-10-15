using System;

namespace PdfEditorApp.Core.Plugins.Descriptors;

/// <summary>
/// Mode determining how the shell overlay container renders framing, titlebar, and chrome for the overlay view.
/// </summary>
public enum OverlayChromeMode
{
    /// <summary>
    /// Automatic Google Material Design 3 Expressive window card frame with draggable header, title, icon, minimize pill, and close button.
    /// Ideal for standard user controls, tools, forms, and utilities without requiring manual header implementation.
    /// </summary>
    StandardCard = 0,

    /// <summary>
    /// Borderless / custom chrome where the view provides its own entire visual frame, header, and dragging physics.
    /// Used for games, custom HUDs, or full-bleed graphics overlays (e.g. a mini-game).
    /// </summary>
    CustomChrome = 1,

    /// <summary>
    /// Compact floating pill / HUD bar anchored or free-floating.
    /// </summary>
    FloatingPill = 2
}

/// <summary>
/// Descriptor representing a non-modal floating overlay panel contributed by a plugin (targeting 'shell.overlay').
/// Inspired by the Cordis dynamic overlay slot architecture.
/// </summary>
public sealed class OverlayDescriptor
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Slot { get; init; } = "shell.overlay";
    public double DefaultWidth { get; init; } = 320;
    public double DefaultHeight { get; init; } = 420;
    public double? InitialX { get; init; }
    public double? InitialY { get; init; }
    public bool IsDraggable { get; init; } = true;
    public bool IsMinimizable { get; init; } = true;
    public bool IsClosable { get; init; } = true;
    public string IconKind { get; init; } = "WindowRestore";
    public OverlayChromeMode ChromeMode { get; init; } = OverlayChromeMode.StandardCard;
    public bool HasCustomChrome => ChromeMode == OverlayChromeMode.CustomChrome;
    public bool IsResizable { get; init; } = true;
    public bool AutoOpenOnStartup { get; init; } = false;
    public double MinWidth { get; init; } = 220;
    public double MinHeight { get; init; } = 100;
    public double MaxWidth { get; init; } = 1600;
    public double MaxHeight { get; init; } = 1400;
    public Type? ViewType { get; init; }
    public Type? ViewModelType { get; init; }
    public Func<IServiceProvider, object>? ViewFactory { get; init; }
    public Func<IServiceProvider, object>? ViewModelFactory { get; init; }
}
