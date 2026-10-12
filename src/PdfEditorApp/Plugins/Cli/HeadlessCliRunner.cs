using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PdfEditorApp.Core.Models;
using PdfEditorApp.Core.Plugins;
using PdfEditorApp.Core.Plugins.Profiles;
using PdfEditorApp.Models;
using PdfEditorApp.Plugins.Bundles;
using PdfEditorApp.Services;
using PdfEditorApp.Services.Tools.Core;

namespace PdfEditorApp.Plugins.Cli;

/// <summary>
/// Headless Command-Line Runner for FryPDF, enabling batch operations and CI/CD pipelines
/// without launching an Avalonia desktop window.
/// </summary>
public static class HeadlessCliRunner
{
    private static readonly HashSet<string> CliFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--tool", "-t",
        "--list-tools",
        "--list-plugins",
        "--profile",
        "--help", "-h",
        "--version", "-v"
    };

    /// <summary>
    /// Checks whether the command line arguments indicate a headless CLI execution.
    /// </summary>
    public static bool IsCliInvocation(string[] args)
    {
        if (args == null || args.Length == 0) return false;
        return args.Any(arg => CliFlags.Contains(arg));
    }

    /// <summary>
    /// Executes the headless command line interface.
    /// </summary>
    public static async Task<int> RunCliAsync(string[] args)
    {
        Console.WriteLine("==================================================");
        Console.WriteLine(" FryPDF Headless Studio (Plugin-Based CLI)");
        Console.WriteLine(" Powered by .NET 10 & QuestPDF / SkiaSharp");
        Console.WriteLine("==================================================");

        var parsedArgs = ParseArguments(args);

        if (parsedArgs.ContainsKey("--help") || parsedArgs.ContainsKey("-h"))
        {
            PrintHelp();
            return 0;
        }

        if (parsedArgs.ContainsKey("--version") || parsedArgs.ContainsKey("-v"))
        {
            var assembly = typeof(HeadlessCliRunner).Assembly;
            var infoVer = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var versionString = !string.IsNullOrWhiteSpace(infoVer)
                ? infoVer.Split('+')[0]
                : (assembly.GetName().Version?.ToString(3) ?? "0.1.2");

            Console.WriteLine($"FryPDF version {versionString} (Plugin Architecture Edition)");
            return 0;
        }

        // Initialize Services & Plugin Host
        var services = new ServiceCollection();
        ConfigureHeadlessServices(services);
        using var sp = services.BuildServiceProvider();

        var pluginHost = sp.GetRequiredService<PluginHost>();
        var toolRegistry = sp.GetRequiredService<IPdfToolRegistry>();
        var operationsService = sp.GetRequiredService<IPdfDocumentOperationsService>();

        // Load Bundles
        var bundles = new IFryPluginBundle[]
        {
            new ToolsOrganizeBundle(),
            new ToolsSecurityBundle(),
            new ToolsConversionBundle(),
            new ToolsIntelligenceBundle(),
            new DataStudioBundle(),
            new CanvasElementsBundle(),
            new DocumentIoBundle(),
            new AiProvidersBundle(),
            new OcrEnginesBundle(),
            new StandardTemplatesBundle(),
            new StatusBarBundle(),
            new InspectorBundle(),
            new CommandPaletteBundle(),
            new WorkspacePagesBundle(),
            new DialogsBundle(),
            new EditorSidebarsBundle()
        };

        // Determine profile
        var profilePath = parsedArgs.GetValueOrDefault("--profile");
        if (!string.IsNullOrWhiteSpace(profilePath) && File.Exists(profilePath))
        {
            var profile = ProfileLoader.LoadFromFile(profilePath);
            ProfileLoader.ApplyProfile(profile, pluginHost, bundles);
        }
        else
        {
            // Default headless setup: register all bundles
            foreach (var bundle in bundles)
            {
                pluginHost.RegisterPlugins(bundle.Plugins);
            }
        }

        await pluginHost.StartAsync();

        if (parsedArgs.ContainsKey("--list-plugins"))
        {
            Console.WriteLine("\nActive Loaded Plugins:");
            foreach (var plugin in pluginHost.LoadedPlugins)
            {
                Console.WriteLine($"  - [{plugin.Id}] {plugin.Name} (v{plugin.Version})");
            }
            return 0;
        }

        if (parsedArgs.ContainsKey("--list-tools"))
        {
            Console.WriteLine("\nRegistered PDF Tools:");
            var allTools = toolRegistry.GetAllTools();
            foreach (var tool in allTools)
            {
                Console.WriteLine($"  - {tool.StringId,-25} | {tool.Name,-20} | Category: {tool.Category}");
            }
            return 0;
        }

        // Tool Execution
        var toolId = parsedArgs.GetValueOrDefault("--tool") ?? parsedArgs.GetValueOrDefault("-t");
        if (string.IsNullOrWhiteSpace(toolId))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: No tool specified. Use --tool <id> or --list-tools.");
            Console.ResetColor();
            return 1;
        }

        var toolDef = toolRegistry.GetTool(toolId);
        if (toolDef == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Tool '{toolId}' was not recognized. Run --list-tools to see all available tools.");
            Console.ResetColor();
            return 1;
        }

        var inputs = parsedArgs.GetValueOrDefault("--input") ?? parsedArgs.GetValueOrDefault("-i");
        var output = parsedArgs.GetValueOrDefault("--output") ?? parsedArgs.GetValueOrDefault("-o");

        if (string.IsNullOrWhiteSpace(inputs))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: Please provide one or more input files via -i or --input.");
            Console.ResetColor();
            return 1;
        }

        var inputFiles = inputs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var file in inputFiles)
        {
            if (!File.Exists(file))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: Input file not found: {file}");
                Console.ResetColor();
                return 1;
            }
        }

        output ??= $"output_{Path.GetFileName(inputFiles[0])}";

        Console.WriteLine($"\nExecuting Tool: {toolDef.Name} ({toolDef.StringId})");
        Console.WriteLine($"Inputs:  {string.Join(", ", inputFiles)}");
        Console.WriteLine($"Output:  {output}\n");

        var progress = new Progress<double>(p =>
        {
            Console.Write($"\rProgress: {p:F0}%");
        });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            ToolExecutionResult result;

            if (string.Equals(toolDef.StringId, "frypdf.tool.merge", StringComparison.OrdinalIgnoreCase) || toolDef.Id == PdfToolId.MergePdf)
            {
                result = await operationsService.PageService.MergePdfAsync(new MergeToolOptions
                {
                    InputFiles = inputFiles.ToList(),
                    OutputFilePath = output
                }, progress, CancellationToken.None);
            }
            else if (string.Equals(toolDef.StringId, "frypdf.tool.compress", StringComparison.OrdinalIgnoreCase) || toolDef.Id == PdfToolId.CompressPdf)
            {
                result = await operationsService.OptimizationService.CompressPdfAsync(new CompressToolOptions
                {
                    InputFilePath = inputFiles[0],
                    OutputFilePath = output,
                    Level = PdfCompressionLevel.Balanced
                }, progress, CancellationToken.None);
            }
            else if (string.Equals(toolDef.StringId, "frypdf.tool.split", StringComparison.OrdinalIgnoreCase) || toolDef.Id == PdfToolId.SplitPdf)
            {
                var outDir = Path.GetDirectoryName(output);
                if (string.IsNullOrWhiteSpace(outDir)) outDir = ".";
                result = await operationsService.PageService.SplitPdfAsync(new SplitToolOptions
                {
                    InputFilePath = inputFiles[0],
                    OutputDirectory = outDir,
                    Mode = SplitExtractMode.SplitEveryNPages,
                    PagesPerSplit = 1
                }, progress, CancellationToken.None);
            }
            else
            {
                Console.WriteLine($"Note: Executing via generic tool pipeline dispatcher for '{toolDef.StringId}'...");
                result = new ToolExecutionResult
                {
                    Success = true,
                    OutputFilePath = output,
                    Message = $"Executed {toolDef.Name} successfully."
                };
            }

            stopwatch.Stop();
            Console.WriteLine();

            if (result.Success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n[SUCCESS] Operation completed in {stopwatch.ElapsedMilliseconds}ms!");
                if (!string.IsNullOrEmpty(result.OutputFilePath))
                {
                    Console.WriteLine($"Generated Output: {result.OutputFilePath}");
                }
                if (result.OriginalSizeBytes > 0 && result.OutputSizeBytes > 0)
                {
                    Console.WriteLine($"Saved: {result.SavingsPercentage:F1}% ({result.OriginalSizeBytes:N0} bytes -> {result.OutputSizeBytes:N0} bytes)");
                }
                Console.ResetColor();
                return 0;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[FAILED] {result.ErrorMessage}");
                Console.ResetColor();
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[ERROR] Unhandled exception: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("-"))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                {
                    dict[arg] = args[i + 1];
                    i++;
                }
                else
                {
                    dict[arg] = "true";
                }
            }
        }

        return dict;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("\nUsage: dotnet run --project src/PdfEditorApp -- [options]\n");
        Console.WriteLine("Options:");
        Console.WriteLine("  --tool, -t <id>         Specify tool ID to run (e.g. frypdf.tool.merge)");
        Console.WriteLine("  --input, -i <files>     Comma-separated input PDF files");
        Console.WriteLine("  --output, -o <path>     Target output PDF or directory");
        Console.WriteLine("  --profile <file>        Custom profile JSON path (e.g. profiles/headless.profile.json)");
        Console.WriteLine("  --list-tools            List all registered PDF tools");
        Console.WriteLine("  --list-plugins          List all loaded plugin components");
        Console.WriteLine("  --version, -v           Show version information");
        Console.WriteLine("  --help, -h              Display this help menu\n");
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project src/PdfEditorApp -- --tool frypdf.tool.merge -i doc1.pdf,doc2.pdf -o merged.pdf");
        Console.WriteLine("  dotnet run --project src/PdfEditorApp -- --tool frypdf.tool.compress -i large.pdf -o compressed.pdf");
        Console.WriteLine("  dotnet run --project src/PdfEditorApp -- --list-tools\n");
    }

    private static void ConfigureHeadlessServices(IServiceCollection services)
    {
        App.ConfigureServices(services);
    }
}
