using System.CommandLine;
using Microsoft.AspNetCore.HttpLogging;
using ModelContextProtocol.Protocol;
using Serilog;
using SharpTools.Tools.Extensions;
using SharpTools.Tools.Interfaces;
using SharpTools.Tools.Mcp.Tools;
using SharpTools.Tools.Services;

namespace SharpTools.SseServer;

public class Program
{
    // --- Application ---
    public const string ApplicationName = "SharpToolsMcpSseServer";
    public const string ApplicationVersion = "0.0.1";
    public const string LogOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";
    public static async Task<int> Main(string[] args)
    {
        Option<int> portOption = new("--port")
        {
            Description = "The port number for the MCP server to listen on.",
            DefaultValueFactory = x => 3001
        };

        Option<string?> logFileOption = new("--log-file")
        {
            Description = "Optional path to a log file. If not specified, logs only go to console."
        };

        Option<Serilog.Events.LogEventLevel> logLevelOption = new("--log-level")
        {
            Description = "Minimum log level for console and file.",
            DefaultValueFactory = x => Serilog.Events.LogEventLevel.Information
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

        RootCommand rootCommand = new("SharpTools MCP Server")
        {
            portOption,
            logFileOption,
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

        int port = parseResult.GetValue(portOption);
        string? logFilePath = parseResult.GetValue(logFileOption);
        Serilog.Events.LogEventLevel minimumLogLevel = parseResult.GetValue(logLevelOption);
        string? solutionPath = parseResult.GetValue(loadSolutionOption);
        string? buildConfiguration = parseResult.GetValue(buildConfigurationOption)!;
        bool disableGit = parseResult.GetValue(disableGitOption);
        string serverUrl = $"http://localhost:{port}";

        LoggerConfiguration loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLogLevel)
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Routing", Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Server.Kestrel", Serilog.Events.LogEventLevel.Debug)
            .MinimumLevel.Override("Microsoft.CodeAnalysis", Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("ModelContextProtocol", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Async(a => a.Console(
                outputTemplate: LogOutputTemplate,
                standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose,
                restrictedToMinimumLevel: minimumLogLevel));

        if (string.IsNullOrWhiteSpace(logFilePath) == false)
        {
            string? logDirectory = Path.GetDirectoryName(logFilePath);
            if (string.IsNullOrWhiteSpace(logDirectory) == false && Directory.Exists(logDirectory) == false)
            {
                Directory.CreateDirectory(logDirectory);
            }
            loggerConfiguration.WriteTo.Async(a => a.File(
                logFilePath,
                rollingInterval: RollingInterval.Day,
                outputTemplate: LogOutputTemplate,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 7,
                restrictedToMinimumLevel: minimumLogLevel));
            Console.WriteLine($"Logging to file: {Path.GetFullPath(logFilePath)} with minimum level {minimumLogLevel}");
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

        try
        {
            Log.Information("Configuring {AppName} v{AppVersion} to run on {ServerUrl} with minimum log level {LogLevel}",
                ApplicationName, ApplicationVersion, serverUrl, minimumLogLevel);

            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args });

            builder.Host.UseSerilog();

            builder.Services.AddW3CLogging(logging =>
            {
                logging.LoggingFields = W3CLoggingFields.All;
                logging.FileSizeLimit = 5 * 1024 * 1024;
                logging.RetainedFileCountLimit = 2;
                logging.FileName = "access-";
            });

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
                .WithHttpTransport()
                .WithSharpTools();

            WebApplication app = builder.Build();

            if (string.IsNullOrEmpty(solutionPath) == false)
            {
                try
                {
                    ISolutionManager solutionManager = app.Services.GetRequiredService<ISolutionManager>();
                    IEditorConfigProvider editorConfigProvider = app.Services.GetRequiredService<IEditorConfigProvider>();

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

            // 2. Custom Request Logging Middleware
            app.Use(async (context, next) =>
            {
                ILogger<Program> logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogDebug("Incoming Request: {Method} {Path} {QueryString} from {RemoteIpAddress}",
                    context.Request.Method,
                    context.Request.Path,
                    context.Request.QueryString,
                    context.Connection.RemoteIpAddress);

                try
                {
                    await next(context);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error processing request: {Method} {Path}", context.Request.Method, context.Request.Path);
                    throw;
                }

                logger.LogDebug("Outgoing Response: {StatusCode} for {Method} {Path}",
                    context.Response.StatusCode,
                    context.Request.Method,
                    context.Request.Path);
            });

            // 4. MCP Middleware
            app.MapMcp();

            Log.Information("Starting {AppName} server...", ApplicationName);
            await app.RunAsync(serverUrl);

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
