using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using PdfEditorApp.Core.Plugins;
using PdfEditorApp.Core.Plugins.Descriptors;
using PdfEditorApp.Plugins.Loader;
using PdfEditorApp.Services.Navigation;
using PdfEditorApp.Services.Overlays;

namespace PdfEditorApp.Tools.UiSnapshots;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("=== FryPDF Headless UiSnapshots ===");
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
        {
            Console.WriteLine("""
                Usage:
                  dotnet run --project tools/UiSnapshots -- <command> [args]

                Commands:
                  plugin <path>       Loads and renders views for a .fryplugin or .dll plugin.
                  home                Renders the main HomeView dashboard.
                  tools               Renders the PDF Tools Studio.
                  help                Shows this help screen.
                """);
            return 0;
        }

        try
        {
            Snapshot.Start();

            var cmd = args[0].ToLowerInvariant();
            switch (cmd)
            {
                case "plugin":
                    if (args.Length < 2)
                    {
                        Console.Error.WriteLine("Error: 'plugin' requires a path to a .fryplugin archive or .dll assembly.");
                        return 1;
                    }
                    return RunPluginSnapshot(args[1]);

                case "home":
                    return RunHomeSnapshot();

                default:
                    Console.Error.WriteLine($"Unknown command '{cmd}'. Run with 'help' for usage.");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[FATAL ERROR] {ex.GetType().FullName}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            if (ex.InnerException != null)
            {
                Console.Error.WriteLine($"--- Inner Exception: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message} ---");
                Console.Error.WriteLine(ex.InnerException.StackTrace);
            }
            Console.ResetColor();
            return 1;
        }
    }

    private static int RunPluginSnapshot(string pluginPath)
    {
        if (!File.Exists(pluginPath))
        {
            Console.Error.WriteLine($"Error: File '{pluginPath}' not found.");
            return 1;
        }

        Console.WriteLine($"Loading plugin from: {pluginPath}");
        var isFryPlugin = pluginPath.EndsWith(".fryplugin", StringComparison.OrdinalIgnoreCase);

        PluginAssemblyPackage package;
        if (isFryPlugin)
        {
            Console.WriteLine("Detected .fryplugin package archive. Unpacking and loading...");
            var result = FryPluginPackageLoader.UnpackAndLoad(pluginPath);
            package = result.AssemblyPackage;
            Console.WriteLine($"Unpacked to: {result.InstallDirectory}");
        }
        else
        {
            Console.WriteLine("Detected standalone .dll assembly. Loading...");
            package = PluginAssemblyLoader.LoadPluginAssembly(pluginPath);
        }

        Console.WriteLine($"Package loaded. Plugins found: {package.Plugins.Count}");
        if (package.Plugins.Count == 0)
        {
            Console.Error.WriteLine("No IFryPlugin implementations found in assembly.");
            return 1;
        }

        var sp = PdfEditorApp.App.Services ?? new ServiceCollection().BuildServiceProvider();
        var host = sp.GetService<PluginHost>() ?? new PluginHost();
        var navRegistry = sp.GetService<INavigationRegistry>() ?? new NavigationRegistry();
        var overlayRegistry = sp.GetService<IOverlayRegistry>() ?? new OverlayRegistry(sp);

        foreach (var asm in package.Context.Assemblies)
        {
            foreach (var t in asm.GetTypes())
            {
                foreach (var p in t.GetProperties())
                {
                    if (p.PropertyType.IsGenericType && p.PropertyType.GetGenericArguments().Any(a => a.Name.Contains("Color")))
                    {
                        Console.WriteLine($"[COLOR COMMAND FOUND] {t.FullName}.{p.Name} -> {p.PropertyType}");
                    }
                }
            }
        }

        foreach (var plugin in package.Plugins)
        {
            Console.WriteLine($"\n--- Inspecting Plugin: {plugin.Name} ({plugin.Id} v{plugin.Version}) ---");
            var ctx = host.Context;
            Console.WriteLine("Applying plugin to host context...");
            plugin.ApplyAsync(ctx).GetAwaiter().GetResult();
            Console.WriteLine("Plugin applied successfully.");

            // 1. Check Navigation items
            var navItems = navRegistry.GetAllItems().Where(i => i.Id.Contains("Image", StringComparison.OrdinalIgnoreCase) || i.Id.Contains(plugin.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            Console.WriteLine($"Discovered matching navigation items: {navItems.Count}");
            foreach (var item in navItems)
            {
                Console.WriteLine($"  -> NavItem: Id='{item.Id}', Title='{item.Title}', DisplayMode={item.DisplayMode}");
                if (item.ViewFactory != null)
                {
                    Console.WriteLine($"     Invoking ViewFactory for '{item.Id}'...");
                    var viewObj = item.ViewFactory(sp);
                    Console.WriteLine($"     View created: {viewObj.GetType().FullName}");
                    if (viewObj is Control ctrl)
                    {
                        Console.WriteLine($"     Rendering off-screen snapshot of '{item.Id}' (1280x800)...");
                        var outPath = Snapshot.RenderAndSave(ctrl, 1280, 800, $"{plugin.Id}_nav_{item.Id}");
                        Console.WriteLine($"     SUCCESS! Snapshot saved to: {outPath}");
                    }
                }
            }

            // 2. Check Overlays
            var overlays = overlayRegistry.GetAllOverlays().Where(o => o.Id == plugin.Id || o.Id.Contains("image", StringComparison.OrdinalIgnoreCase)).ToList();
            Console.WriteLine($"Discovered matching overlay items: {overlays.Count}");
            foreach (var overlay in overlays)
            {
                Console.WriteLine($"  -> Overlay: Id='{overlay.Id}', Title='{overlay.Title}', ChromeMode={overlay.ChromeMode}");
                if (overlay.ViewFactory != null)
                {
                    Console.WriteLine($"     Invoking ViewFactory for overlay '{overlay.Id}'...");
                    var viewObj = overlay.ViewFactory(sp);
                    Console.WriteLine($"     View created: {viewObj.GetType().FullName}");
                    if (viewObj is Control ctrl)
                    {
                        Console.WriteLine($"     Rendering off-screen snapshot of overlay '{overlay.Id}' ({overlay.DefaultWidth}x{overlay.DefaultHeight})...");
                        var outPath = Snapshot.RenderAndSave(ctrl, overlay.DefaultWidth, overlay.DefaultHeight, $"{plugin.Id}_overlay_{overlay.Id}");
                        Console.WriteLine($"     SUCCESS! Snapshot saved to: {outPath}");
                    }
                }
            }
        }

        Console.WriteLine("\n[DONE] Plugin loading and UI verification completed successfully.");
        return 0;
    }

    private static int RunHomeSnapshot()
    {
        Console.WriteLine("Rendering HomeView...");
        var sp = PdfEditorApp.App.Services ?? new ServiceCollection().BuildServiceProvider();
        var homeVm = sp.GetService<PdfEditorApp.ViewModels.HomeViewModel>();
        var homeView = new PdfEditorApp.Views.HomeView { DataContext = homeVm };
        var outPath = Snapshot.RenderAndSave(homeView, 1400, 900, "frypdf_home_dashboard");
        Console.WriteLine($"HomeView snapshot saved to: {outPath}");
        return 0;
    }
}
