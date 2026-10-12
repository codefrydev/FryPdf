using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

namespace PdfEditorApp.Tools.UiSnapshots;

/// <summary>
/// Headless Avalonia session using Skia renderer with no screen and no message loop.
/// Renders FryPDF views, tools, and plugins off-screen to test lifecycle and save PNG snapshots.
/// </summary>
public static class Snapshot
{
    public static string OutputFolder { get; private set; } = string.Empty;

    public static void Start(string? outputDir = null)
    {
        AppBuilder.Configure<PdfEditorApp.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        OutputFolder = Path.GetFullPath(outputDir ?? Path.Combine(Directory.GetCurrentDirectory(), "test-artifacts", "snapshots"));
        Directory.CreateDirectory(OutputFolder);
    }

    public static Window Show(Control content, double width = 1200, double height = 800)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = content
        };
        window.Show();
        Settle(10);
        return window;
    }

    public static void Settle(int frames = 10)
    {
        for (int i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
    }

    public static string Save(Window window, string name)
    {
        Settle(5);
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The window rendered nothing.");
        string path = Path.Combine(OutputFolder, name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name : name + ".png");
        frame.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"[UiSnapshots] Saved: {path}");
        return path;
    }

    public static string RenderAndSave(Control control, double width, double height, string name)
    {
        var window = Show(control, width, height);
        try
        {
            return Save(window, name);
        }
        finally
        {
            window.Close();
        }
    }
}
