namespace PdfEditorApp.Core.Plugins.Descriptors;

/// <summary>
/// Defines the activation scope of a keyboard shortcut.
/// </summary>
public enum ShortcutScope
{
    /// <summary>
    /// Available globally throughout the entire application regardless of which workspace or control is active.
    /// Example: Command Palette (Ctrl+K), AI Assistant (Ctrl+I), C# Studio launch (Ctrl+Alt+E).
    /// </summary>
    Global,

    /// <summary>
    /// Active when a specific workspace or page is currently visible (matching <see cref="ShortcutDescriptor.ContextId"/>).
    /// Example: Save Annotation (Ctrl+S) in PdfViewer vs Save PDF Project (Ctrl+S) in PdfEditor.
    /// </summary>
    Workspace,

    /// <summary>
    /// Active only when a specific control container or element has keyboard focus.
    /// </summary>
    Context
}
