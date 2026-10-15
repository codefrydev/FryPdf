using System;

namespace PdfEditorApp.Core.Plugins.Descriptors;

/// <summary>
/// Declarative descriptor for a keyboard shortcut contributed by host subsystems or plugins.
/// </summary>
public class ShortcutDescriptor
{
    /// <summary>
    /// Unique dotted or reverse-DNS identifier (e.g. "csharp.debug.start", "file.save", "csharp.notebook.runCell").
    /// </summary>
    public string Id { get; init; } = "";

    /// <summary>
    /// User-visible title (e.g. "Start Debugging", "Save Project").
    /// </summary>
    public string Title { get; init; } = "";

    /// <summary>
    /// Category grouping for settings and cheatsheets (e.g. "C# Code Studio", "C# Notebook", "File &amp; Project", "Edit", "View &amp; Navigation").
    /// </summary>
    public string Category { get; init; } = "General";

    /// <summary>
    /// Explanatory summary of what this shortcut triggers.
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// Default cross-platform key gesture (e.g. "F5", "Ctrl+S", "Ctrl+Enter", "Ctrl+Alt+E").
    /// </summary>
    public string DefaultGesture { get; init; } = "";

    /// <summary>
    /// Optional macOS specific default gesture (e.g. "Cmd+S", "Cmd+Enter").
    /// If null or empty, system will automatically map "Ctrl" to "Cmd" on macOS.
    /// </summary>
    public string? MacGesture { get; init; }

    /// <summary>
    /// Scope of activation: Global, Workspace, or Context.
    /// </summary>
    public ShortcutScope Scope { get; init; } = ShortcutScope.Workspace;

    /// <summary>
    /// Matching workspace/page ID when <see cref="Scope"/> is <see cref="ShortcutScope.Workspace"/> or <see cref="ShortcutScope.Context"/>
    /// (e.g. "PdfEditor", "PdfViewer").
    /// </summary>
    public string? ContextId { get; init; }

    /// <summary>
    /// Execution delegate invoked when the shortcut is triggered.
    /// </summary>
    public Action<IServiceProvider>? Action { get; init; }

    /// <summary>
    /// Optional predicate guard checking if the action can execute.
    /// </summary>
    public Func<IServiceProvider, bool>? CanExecute { get; init; }

    /// <summary>
    /// Whether users are allowed to rebind this shortcut in settings. Defaults to true.
    /// </summary>
    public bool IsCustomizable { get; init; } = true;
}
