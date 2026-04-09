using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Serilog;
using Serilog.Events;
using SharpTools.Tools.Extensions;
using SharpTools.Tools.Interfaces;
using SharpTools.Tools.Mcp.Tools;
using SharpTools.Tools.Services;

namespace SharpTools.StdioServer;

public static class Program
{
    public const string ApplicationName = "SharpToolsMcpStdioServer";
    public const string ApplicationVersion = "0.0.1";
    public const string LogOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";
    public static async Task<int> Main(string[] args)
    {
        Option<string?> logDirOption = new("--log-directory")
        {
            Description = "Optional path to a log directory. If not specified, logs only go to console."
        };

        Option<LogEventLevel> logLevelOption = new("--log-level")
        {
            Description = "Minimum log level for console and file.",
            DefaultValueFactory = x => LogEventLevel.Information
        };

        Option<string?> loadSolutionOption = new("--load-solution")
        {
            Description = "Path to a solution file (.sln) to load immediately on startup."
        };

        Option<string?> buildConfigurationOption = new("--build-configuration")
        {
            Description = "Build configuration to use when loading the solution (Debug, Release, etc.)."
        };

        Option<bool> disableGitOption = new("--disable-git")
        {
            Description = "Disable Git integration.",
            DefaultValueFactory = x => false
        };

        RootCommand rootCommand = new("SharpTools MCP StdIO Server")
        {
            logDirOption,
            logLevelOption,
            loadSolutionOption,
            buildConfigurationOption,
            disableGitOption
        };

        ParseResult? parseResult = rootCommand.Parse(args);
        if (parseResult == null)
        {
            Console.Error.WriteLine("Failed to parse command line arguments.");
            return 1;
        }

        string? logDirPath = parseResult.GetValue(logDirOption);
        LogEventLevel minimumLogLevel = parseResult.GetValue(logLevelOption);
        string? solutionPath = parseResult.GetValue(loadSolutionOption);
        string? buildConfiguration = parseResult.GetValue(buildConfigurationOption)!;
        bool disableGit = parseResult.GetValue(disableGitOption);

        LoggerConfiguration loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLogLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.CodeAnalysis", LogEventLevel.Information)
            .MinimumLevel.Override("ModelContextProtocol", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Async(a => a.Console(
                outputTemplate: LogOutputTemplate,
                standardErrorFromLevel: LogEventLevel.Verbose,
                restrictedToMinimumLevel: minimumLogLevel));

        if (string.IsNullOrWhiteSpace(logDirPath) == false)
        {
            if (string.IsNullOrWhiteSpace(logDirPath))
            {
                Console.Error.WriteLine("Log directory is not valid.");
                return 1;
            }
            if (Directory.Exists(logDirPath) == false)
            {
                Console.Error.WriteLine($"Log directory does not exist. Creating: {logDirPath}");
                try
                {
                    Directory.CreateDirectory(logDirPath);
                }
                catch (Exception ex)
                {
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

        if (disableGit)
        {
            Log.Information("Git integration is disabled.");
        }

        if (string.IsNullOrEmpty(buildConfiguration) == false)
        {
            Log.Information("Using build configuration: {BuildConfiguration}", buildConfiguration);
        }

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog();
        builder.Services.WithSharpToolsServices(!disableGit, buildConfiguration);

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = ApplicationName,
                    Version = ApplicationVersion,
                };
            })
            .WithStdioServerTransport()
            .WithSharpTools();

        try
        {
            Log.Information("Starting {AppName} v{AppVersion}", ApplicationName, ApplicationVersion);
            IHost host = builder.Build();

            if (string.IsNullOrEmpty(solutionPath) == false)
            {
                try
                {
                    ISolutionManager solutionManager = host.Services.GetRequiredService<ISolutionManager>();
                    IEditorConfigProvider editorConfigProvider = host.Services.GetRequiredService<IEditorConfigProvider>();

                    Log.Information("Loading solution: {SolutionPath}", solutionPath);
                    await solutionManager.LoadSolutionAsync(solutionPath, CancellationToken.None);

                    string? solutionDir = Path.GetDirectoryName(solutionPath);
                    if (string.IsNullOrEmpty(solutionDir) == false)
                    {
                        await editorConfigProvider.InitializeAsync(solutionDir, CancellationToken.None);
                        Log.Information("Solution loaded successfully: {SolutionPath}", solutionPath);
                    }
                    else
                    {
                        Log.Warning("Could not determine directory for solution path: {SolutionPath}", solutionPath);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error loading solution: {SolutionPath}", solutionPath);
                }
            }

            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "{AppName} terminated unexpectedly.", ApplicationName);
            return 1;
        }
        finally
        {
            Log.Information("{AppName} shutting down.", ApplicationName);
            await Log.CloseAndFlushAsync();
        }
    }
}


