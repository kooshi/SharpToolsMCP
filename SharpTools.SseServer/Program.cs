using SharpTools.Tools.Interfaces;
using SharpTools.Tools.Mcp.Tools;
using SharpTools.Tools.Extensions;
using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.AspNetCore.HttpLogging;
using Serilog;
using ModelContextProtocol.Protocol;

namespace SharpTools.SseServer;
public class Program {
    // --- Application ---
    public const string ApplicationName = "SharpToolsMcpSseServer";
    public const string ApplicationVersion = "0.0.1";
    public const string LogOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";
    public static async Task<int> Main(string[] args) {
        // Ensure tool assemblies are loaded for MCP SDK's WithToolsFromAssembly
        _ = typeof(SolutionTools);
        _ = typeof(AnalysisTools);
        _ = typeof(ModificationTools);

        var portOption = new Option<int>(
            name: "--port",
            description: "The port number for the MCP server to listen on.",
            getDefaultValue: () => 3001);

        var logFileOption = new Option<string?>(
            name: "--log-file",
            description: "Optional path to a log file. If not specified, logs only go to console.");

        var logLevelOption = new Option<Serilog.Events.LogEventLevel>(
            name: "--log-level",
            description: "Minimum log level for console and file.",
            getDefaultValue: () => Serilog.Events.LogEventLevel.Information);

        var loadSolutionOption = new Option<string?>(
            name: "--load-solution",
            description: "Path to a solution file (.sln) to load immediately on startup.");

        var rootCommand = new RootCommand("SharpTools MCP Server") {
        portOption,
        logFileOption,
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

        int port = parseResult.GetValueForOption(portOption);
        string? logFilePath = parseResult.GetValueForOption(logFileOption);
        Serilog.Events.LogEventLevel minimumLogLevel = parseResult.GetValueForOption(logLevelOption);
        string? solutionPath = parseResult.GetValueForOption(loadSolutionOption);
        string serverUrl = $"http://localhost:{port}";

        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLogLevel) // Set based on command line
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning) // Default override
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
            // For debugging connection issues, set AspNetCore to Information or Debug
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Routing", Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Server.Kestrel", Serilog.Events.LogEventLevel.Debug) // Kestrel connection logs
            .MinimumLevel.Override("Microsoft.CodeAnalysis", Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("ModelContextProtocol", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Async(a => a.Console(
                outputTemplate: LogOutputTemplate,
                standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose,
                restrictedToMinimumLevel: minimumLogLevel));

        if (!string.IsNullOrWhiteSpace(logFilePath)) {
            var logDirectory = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrWhiteSpace(logDirectory) && !Directory.Exists(logDirectory)) {
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

        try {
            Log.Information("Configuring {AppName} v{AppVersion} to run on {ServerUrl} with minimum log level {LogLevel}",
                ApplicationName, ApplicationVersion, serverUrl, minimumLogLevel);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args });

            builder.Host.UseSerilog();

            // Add W3CLogging for detailed HTTP request logging
            // This logs to Microsoft.Extensions.Logging, which Serilog will capture.
            builder.Services.AddW3CLogging(logging => {
                logging.LoggingFields = W3CLoggingFields.All; // Log all available fields
                logging.FileSizeLimit = 5 * 1024 * 1024; // 5 MB
                logging.RetainedFileCountLimit = 2;
                logging.FileName = "access-"; // Prefix for log files
                                              // By default, logs to a 'logs' subdirectory of the app's content root.
                                              // Can be configured: logging.RootPath = ...
            });

            builder.Services.WithSharpToolsServices();

            builder.Services
                .AddMcpServer(options => {
                    options.ServerInfo = new Implementation {
                        Name = ApplicationName,
                        Version = ApplicationVersion,
                    };
                    // For debugging, you can hook into handlers here if needed,
                    // but ModelContextProtocol's own Debug logging should be sufficient.
                })
                .WithHttpTransport()
                .WithSharpTools();

            var app = builder.Build();

            // Load solution if specified in command line arguments
            if (!string.IsNullOrEmpty(solutionPath)) {
                try {
                    var solutionManager = app.Services.GetRequiredService<ISolutionManager>();
                    var editorConfigProvider = app.Services.GetRequiredService<IEditorConfigProvider>();

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

            // --- ASP.NET Core Middleware ---

            // 1. W3C Logging Middleware (if enabled and configured to log to a file separate from Serilog)
            //    If W3CLogging is configured to write to files, it has its own middleware.
            //    If it's just for ILogger, Serilog picks it up.
            // app.UseW3CLogging(); // This is needed if W3CLogging is writing its own files.
            // If it's just feeding ILogger, Serilog handles it.

            // 2. Custom Request Logging Middleware (very early in the pipeline)
            app.Use(async (context, next) => {
                var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogDebug("Incoming Request: {Method} {Path} {QueryString} from {RemoteIpAddress}",
                    context.Request.Method,
                    context.Request.Path,
                    context.Request.QueryString,
                    context.Connection.RemoteIpAddress);

                // Log headers for more detail if needed (can be verbose)
                // foreach (var header in context.Request.Headers) {
                //     logger.LogTrace("Header: {Key}: {Value}", header.Key, header.Value);
                // }
                try {
                    await next(context);
                } catch (Exception ex) {
                    logger.LogError(ex, "Error processing request: {Method} {Path}", context.Request.Method, context.Request.Path);
                    throw; // Re-throw to let ASP.NET Core handle it
                }

                logger.LogDebug("Outgoing Response: {StatusCode} for {Method} {Path}",
                    context.Response.StatusCode,
                    context.Request.Method,
                    context.Request.Path);
            });


            // 3. Standard ASP.NET Core middleware (HTTPS redirection, routing, auth, etc. - not used here yet)
            // if (app.Environment.IsDevelopment()) { }
            // app.UseHttpsRedirection(); 

            // 4. MCP Middleware
            app.MapMcp(); // Maps the MCP endpoint (typically "/mcp")

            Log.Information("Starting {AppName} server...", ApplicationName);
            await app.RunAsync(serverUrl);

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