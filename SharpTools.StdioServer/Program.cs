using SharpTools.Tools.Interfaces;
using SharpTools.Tools.Mcp.Tools;
using SharpTools.Tools.Extensions;
using Serilog;
using System.CommandLine;
using System.CommandLine.Parsing;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace SharpTools.StdioServer;

public static class Program {
    public const string ApplicationName = "SharpToolsMcpStdioServer";
    public const string ApplicationVersion = "0.0.1";
    public const string LogOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";
    public static async Task<int> Main(string[] args) {
        _ = typeof(SolutionTools);
        _ = typeof(AnalysisTools);
        _ = typeof(ModificationTools);

        var logDirOption = new Option<string?>(
            name: "--log-directory",
            description: "Optional path to a log directory. If not specified, logs only go to console.");

        var logLevelOption = new Option<Serilog.Events.LogEventLevel>(
            name: "--log-level",
            description: "Minimum log level for console and file.",
            getDefaultValue: () => Serilog.Events.LogEventLevel.Information);

        var loadSolutionOption = new Option<string?>(
            name: "--load-solution",
            description: "Path to a solution file (.sln) to load immediately on startup.");

        var rootCommand = new RootCommand("SharpTools MCP StdIO Server")
        {
        logDirOption,
        logLevelOption,
        loadSolutionOption
    };

        ParseResult? parseResult = null;
        rootCommand.SetHandler((invocationContext) => {
            parseResult = invocationContext.ParseResult;
        });

        await rootCommand.InvokeAsync(args);

        if (parseResult == null) {
            Console.Error.WriteLine("Failed to parse command line arguments.");
            return 1;
        }

        string? logDirPath = parseResult.GetValueForOption(logDirOption);
        Serilog.Events.LogEventLevel minimumLogLevel = parseResult.GetValueForOption(logLevelOption);
        string? solutionPath = parseResult.GetValueForOption(loadSolutionOption);

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

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog();
        builder.Services.WithSharpToolsServices();

        builder.Services
            .AddMcpServer(options => {
                options.ServerInfo = new Implementation {
                    Name = ApplicationName,
                    Version = ApplicationVersion,
                };
            })
            .WithStdioServerTransport()
            .WithSharpTools();

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

