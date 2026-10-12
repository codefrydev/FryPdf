using Avalonia;
using Avalonia.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PdfEditorApp.Plugins.Cli;
using PdfEditorApp.Services;

namespace PdfEditorApp;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        if (HeadlessCliRunner.IsCliInvocation(args))
        {
            return HeadlessCliRunner.RunCliAsync(args).GetAwaiter().GetResult();
        }

        ConfigureThreadPool();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Last-resort diagnostic for an exception that would otherwise abort the process with zero
    /// trace anywhere. Writes synchronously and directly to disk rather than through
    /// <see cref="AppLogService"/>, whose file writer enqueues to a background channel with no
    /// guarantee it flushes before the CLR fails fast.
    /// </summary>
    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FryPdfPaths.LogFilePath)!);
            File.AppendAllText(FryPdfPaths.LogFilePath,
                $"[{DateTime.Now:HH:mm:ss.fff}] [Fatal  ] [{source}]{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort: never let crash logging itself throw during process teardown.
        }

        Console.Error.WriteLine($"[{source}] {ex}");
    }

    /// <summary>
    /// Raises the thread-pool floor so a burst of offloaded work does not queue behind the
    /// pool's thread-injection rate.
    /// </summary>
    /// <remarks>
    /// The app offloads aggressively — around 77 <c>Task.Run</c> call sites — and several
    /// service methods still block a pooled thread on sync-over-async I/O (OCR, PDF
    /// linearization). Past <see cref="Environment.ProcessorCount"/> the pool only injects
    /// roughly one thread every 500ms, so an interaction that fans out several work items
    /// while others sit blocked leaves the rest waiting on injection rather than on CPU.
    ///
    /// That delay is not only a UI-responsiveness problem here: plugins run in-process and may
    /// schedule their own non-real-time work (metadata scans, decode read-ahead) on the same
    /// pool, and starving that work is what turns into an audible gap for an audio plugin.
    /// The floor is a lower bound, not a reservation — idle threads still retire.
    /// </remarks>
    private static void ConfigureThreadPool()
    {
        ThreadPool.GetMinThreads(out int currentWorker, out int currentCompletionPort);

        int desiredWorker = Math.Max(currentWorker, Environment.ProcessorCount * 2);

        if (desiredWorker > currentWorker)
        {
            // Failure here is non-fatal: the pool simply keeps its default injection behaviour.
            ThreadPool.SetMinThreads(desiredWorker, currentCompletionPort);
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            // Areas are listed explicitly so Avalonia's Binding area is NOT routed into Trace.
            // AppLogService installs a TraceListener, so every failed binding became a log entry:
            // InspectorSidebarView has a section per element type and all but one bind against a
            // null view-model property at any moment, so a single rebuild emitted ~180 entries and
            // flushed the diagnostic ring buffer, evicting the events we actually need to see.
            .LogToTrace(LogEventLevel.Warning, LogArea.Control, LogArea.Layout, LogArea.Visual, LogArea.Platform);
}
