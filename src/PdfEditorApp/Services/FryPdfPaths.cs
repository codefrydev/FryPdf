using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PdfEditorApp.Services;

/// <summary>
/// Resolves writable data directories for FryPDF across all deployment modes:
/// <list type="bullet">
///   <item><description>Developer / sideload: next to the executable (AppContext.BaseDirectory)</description></item>
///   <item><description>MSIX / Microsoft Store: %LocalAppData%\FryPDF\ (install dir is read-only)</description></item>
///   <item><description>EXE installer in Program Files: %LocalAppData%\FryPDF\ (OS-protected write)</description></item>
///   <item><description>macOS / Linux: ~/.local/share/FryPDF/</description></item>
/// </list>
/// All consumers must use these properties instead of raw AppContext.BaseDirectory paths
/// so that MSIX packages and Program Files EXE installs never attempt to write to the
/// read-only installation directory.
/// </summary>
public static class FryPdfPaths
{
    private static string? _pluginsDirectory;
    private static string? _fryWorldPluginsDirectory;
    private static string? _dataDirectory;
    private static string? _profilesDirectory;
    private static string? _logsDirectory;
    private static bool? _needsUserDataDir;

    /// <summary>Writable directory for user-installed external plugins.</summary>
    public static string PluginsDirectory => _pluginsDirectory ??= ResolveWritableDir("plugins");

    /// <summary>
    /// Writable directory for plugins built on FryWorld.Sdk. Kept apart from <see cref="PluginsDirectory"/> so FryPDF's
    /// own plugin loader and FryWorld's never process each other's packages.
    /// </summary>
    public static string FryWorldPluginsDirectory => _fryWorldPluginsDirectory ??= ResolveWritableDir("fryworld-plugins");

    /// <summary>Writable directory for persistent app data (installed_plugins.json, catalog cache, etc.).</summary>
    public static string DataDirectory => _dataDirectory ??= ResolveWritableDir("data");

    /// <summary>Writable directory for profile JSON files.</summary>
    public static string ProfilesDirectory => _profilesDirectory ??= ResolveWritableDir("profiles");

    /// <summary>Writable directory for the rolling diagnostic log file.</summary>
    public static string LogsDirectory => _logsDirectory ??= ResolveWritableDir("logs");

    /// <summary>Fully qualified path to the installed-plugins JSON manifest.</summary>
    public static string InstalledPluginsJsonPath
        => Path.Combine(DataDirectory, "installed_plugins.json");

    /// <summary>Fully qualified path to the rolling diagnostic log file (see <see cref="AppLogService"/>).</summary>
    public static string LogFilePath => Path.Combine(LogsDirectory, "frypdf.log");

    /// <summary>
    /// True when the app is installed in a system-protected directory where writing is denied
    /// to standard user processes — regardless of whether it is MSIX, Program Files EXE, or similar.
    /// When true all user data is redirected to <see cref="GetUserDataRoot"/>.
    /// </summary>
    public static bool NeedsUserDataDirectory => _needsUserDataDir ??= DetectNeedsUserDataDir();

    // ─── Implementation ────────────────────────────────────────────────────────

    private static bool DetectNeedsUserDataDir()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false; // macOS/Linux installs are always user-writable

        var baseDir = AppContext.BaseDirectory;

        // Fast-path: MSIX — always read-only by OS design
        if (baseDir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
        {
            AppLogService.Instance?.Log(AppLogLevel.Info, "PluginPaths",
                $"Detected MSIX install under '{baseDir}'; user data will redirect to '{GetUserDataRoot()}'.");
            return true;
        }

        // Fast-path: any sub-path of Program Files / Program Files (x86)
        // These are read-only without elevation even for admins at runtime.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if ((!string.IsNullOrEmpty(programFiles) &&
             baseDir.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(programFilesX86) &&
             baseDir.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase)))
        {
            AppLogService.Instance?.Log(AppLogLevel.Info, "PluginPaths",
                $"Detected Program Files install under '{baseDir}'; user data will redirect to '{GetUserDataRoot()}'.");
            return true;
        }

        // Slow-path: do a write probe for any other potentially restricted location
        var writable = IsWritable(Path.Combine(baseDir, "plugins"));
        AppLogService.Instance?.Log(AppLogLevel.Info, "PluginPaths",
            $"Write probe for '{baseDir}' returned writable={writable}.");
        return !writable;
    }

    private static string ResolveWritableDir(string subFolder)
    {
        if (!NeedsUserDataDirectory)
        {
            // Dev / sideload on non-protected path — write next to the executable
            var candidate = Path.Combine(AppContext.BaseDirectory, subFolder);
            Directory.CreateDirectory(candidate);
            return candidate;
        }

        // MSIX, Program Files EXE, or any other read-only install dir:
        // redirect to user-scoped writable directory.
        var userDir = Path.Combine(GetUserDataRoot(), subFolder);
        Directory.CreateDirectory(userDir);
        AppLogService.Instance?.Log(AppLogLevel.Info, "PluginPaths",
            $"Redirected '{subFolder}' directory to '{userDir}'.");
        return userDir;
    }

    private static string GetUserDataRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // LocalApplicationData = %LocalAppData% (non-roaming, per-user).
            // This is also what MSIX ApplicationData.Current.LocalFolder maps to.
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localApp, "FryPDF");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "FryPDF");
        }

        // Linux / other Unix — honour XDG base-dir spec
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgData))
            return Path.Combine(xdgData, "FryPDF");

        var linuxHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(linuxHome, ".local", "share", "FryPDF");
    }

    /// <summary>
    /// Probes whether a directory is writable by attempting a temp-file create-delete cycle.
    /// Returns <c>false</c> on any <see cref="UnauthorizedAccessException"/> or
    /// <see cref="IOException"/> without throwing.
    /// </summary>
    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".frypdf_write_probe_{Guid.NewGuid():N}");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLogService.Instance?.LogWarning("PluginPaths", $"Write probe denied for '{dir}'", ex);
            return false;
        }
        catch (IOException ex)
        {
            AppLogService.Instance?.LogWarning("PluginPaths", $"Write probe failed for '{dir}'", ex);
            return false;
        }
    }
}
