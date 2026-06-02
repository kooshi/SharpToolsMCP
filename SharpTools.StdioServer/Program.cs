using System.CommandLine;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;
using SharpTools.Tools.Extensions;
using SharpTools.Tools.Interfaces;
using SharpTools.Tools.Mcp.Tools;
namespace SharpTools.StdioServer;

public static class Program {
    public const string ApplicationName = "SharpToolsMcpStdioServer";
    public const string ApplicationVersion = "0.0.1";
    public const string LogOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";
    public static async Task<int> Main(string[] args) {
        var logDirOption = new Option<string?>("--log-directory") {
            Description = "Optional path to a log directory. If not specified, logs only go to console."
        };

        var logLevelOption = new Option<Serilog.Events.LogEventLevel>("--log-level") {
            Description = "Minimum log level for console and file.",
            DefaultValueFactory = x => Serilog.Events.LogEventLevel.Information
        };

        var loadSolutionOption = new Option<string?>("--load-solution") {
            Description = "Path to a solution file (.sln) to load immediately on startup."
        };

        var buildConfigurationOption = new Option<string?>("--build-configuration") {
            Description = "Build configuration to use when loading the solution (Debug, Release, etc.)."
        };

        var disableGitOption = new Option<bool>("--disable-git") {
            Description = "Disable Git integration.",
            DefaultValueFactory = x => false
        };

        var listToolsOption = new Option<bool>("--list-tools") {
            Description = "List available tool and prompt type names, then exit.",
            DefaultValueFactory = x => false
        };

        var excludeToolTypesOption = new Option<List<string>>("--exclude-tool-types") {
            Description = "List of tool type names to exclude (from --list-tools).",
            AllowMultipleArgumentsPerToken = true
        };

        var rootCommand = new RootCommand("SharpTools MCP StdIO Server"){
            logDirOption,
            logLevelOption,
            loadSolutionOption,
            buildConfigurationOption,
            disableGitOption,
            listToolsOption,
            excludeToolTypesOption,
        };

        ParseResult? parseResult = rootCommand.Parse(args);
        if (parseResult == null) {
            Console.Error.WriteLine("Failed to parse command line arguments.");
            return 1;
        }

        string? logDirPath = parseResult.GetValue(logDirOption);
        Serilog.Events.LogEventLevel minimumLogLevel = parseResult.GetValue(logLevelOption);
        string? solutionPath = parseResult.GetValue(loadSolutionOption);
        string? buildConfiguration = parseResult.GetValue(buildConfigurationOption)!;
        bool disableGit = parseResult.GetValue(disableGitOption);
        bool listTools = parseResult.GetValue(listToolsOption);
        List<string> excludeToolTypes = parseResult.GetValue(excludeToolTypesOption) ?? [];

        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLogLevel)
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.CodeAnalysis", Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("ModelContextProtocol", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Async(a => a.Console(
                outputTemplate: LogOutputTemplate,
                standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose,
                restrictedToMinimumLevel: minimumLogLevel));
        
        if (!string.IsNullOrWhiteSpace(logDirPath)) {
            if (string.IsNullOrWhiteSpace(logDirPath)) {
                Console.Error.WriteLine("Log directory is not valid.");
                return 1;
            }
            if (!Directory.Exists(logDirPath)) {
                Console.Error.WriteLine($"Log directory does not exist. Creating: {logDirPath}");
                try {
                    Directory.CreateDirectory(logDirPath);
                } catch (Exception ex) {
                    Console.Error.WriteLine($"Failed to create log directory: {ex.Message}");
                    return 1;
                }
            }
            string logFilePath = Path.Combine(logDirPath, $"{ApplicationName}-.log");
            loggerConfiguration.WriteTo.Async(a => a.File(
                logFilePath,
                rollingInterval: RollingInterval.Day,
                outputTemplate: LogOutputTemplate,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 7,
                restrictedToMinimumLevel: minimumLogLevel));
            Console.Error.WriteLine($"Logging to file: {Path.GetFullPath(logDirPath)} with minimum level {minimumLogLevel}");
        }

        Log.Logger = loggerConfiguration.CreateBootstrapLogger();

        MsBuildLocatorBootstrapper.EnsureRegistered(
            message => Log.Information("{Message}", message),
            message => Log.Warning("{Message}", message));

        _ = typeof(SolutionTools);
        _ = typeof(AnalysisTools);
        _ = typeof(ModificationTools);

        if (disableGit) {
            Log.Information("Git integration is disabled.");
        }

        if (!string.IsNullOrEmpty(buildConfiguration)) {
            Log.Information("Using build configuration: {BuildConfiguration}", buildConfiguration);
        }

        if(listTools) {
            var toolAssembly = Assembly.Load("SharpTools.Tools");

            Console.WriteLine("Tools:");
            foreach (var t in toolAssembly.GetTypes()
                .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
                .OrderBy(t => t.Name)) {
                Console.WriteLine($"  {t.Name}");
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                    .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
                    .OrderBy(m => m.Name)) {
                    var attr = m.GetCustomAttribute<McpServerToolAttribute>()!;
                    var name = string.IsNullOrEmpty(attr.Name) ? m.Name : attr.Name;
                    Console.WriteLine($"    - {name}");
                }
            }

            return 0;
        }

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog();
        builder.Services.WithSharpToolsServices(!disableGit, buildConfiguration);

        builder.Services
            .AddMcpServer(options => {
                options.ServerInfo = new Implementation {
                    Name = ApplicationName,
                    Version = ApplicationVersion,
                };
            })
            .WithStdioServerTransport()
            .WithSharpTools(excludeToolTypes);

        try {
            Log.Information("Starting {AppName} v{AppVersion}", ApplicationName, ApplicationVersion);
            var host = builder.Build();

            if (!string.IsNullOrEmpty(solutionPath)) {
                try {
                    var solutionManager = host.Services.GetRequiredService<ISolutionManager>();
                    var editorConfigProvider = host.Services.GetRequiredService<IEditorConfigProvider>();

                    Log.Information("Loading solution: {SolutionPath}", solutionPath);
                    await solutionManager.LoadSolutionAsync(solutionPath, CancellationToken.None);

                    var solutionDir = Path.GetDirectoryName(solutionPath);
                    if (!string.IsNullOrEmpty(solutionDir)) {
                        await editorConfigProvider.InitializeAsync(solutionDir, CancellationToken.None);
                        Log.Information("Solution loaded successfully: {SolutionPath}", solutionPath);
                    } else {
                        Log.Warning("Could not determine directory for solution path: {SolutionPath}", solutionPath);
                    }
                } catch (Exception ex) {
                    Log.Error(ex, "Error loading solution: {SolutionPath}", solutionPath);
                }
            }

            await host.RunAsync();
            return 0;
        } catch (Exception ex) {
            Log.Fatal(ex, "{AppName} terminated unexpectedly.", ApplicationName);
            return 1;
        } finally {
            Log.Information("{AppName} shutting down.", ApplicationName);
            await Log.CloseAndFlushAsync();
        }
    }
}

