using ModelContextProtocol;

namespace SharpTools.Tools.Mcp.Tools;

// Marker class for ILogger<T> category specific to MiscTools
public class MiscToolsLogCategory { }

[McpServerToolType]
public static class MiscTools {
    private static readonly string RequestLogFilePath = Path.Combine(
        AppContext.BaseDirectory,
        "logs",
        "tool-requests.json");

    //TODO: Convert into `CreateIssue` for feature requests and bug reports combined
    [McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(RequestNewTool), Idempotent = true, ReadOnly = false, Destructive = false, OpenWorld = false),
    Description("Allows requesting a new tool to be added to the SharpTools MCP server. Logs the request for review.")]
    public static async Task<string> RequestNewTool(
        ILogger<MiscToolsLogCategory> logger,
        [Description("Name for the proposed tool.")] string toolName,
        [Description("Detailed description of what the tool should do.")] string toolDescription,
        [Description("Expected input parameters and their descriptions.")] string expectedParameters,
        [Description("Expected output and format.")] string expectedOutput,
        [Description("Justification for why this tool would be valuable.")] string justification,
        CancellationToken cancellationToken = default) {

        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () => {
            // Validate parameters
            ErrorHandlingHelpers.ValidateStringParameter(toolName, "toolName", logger);
            ErrorHandlingHelpers.ValidateStringParameter(toolDescription, "toolDescription", logger);
            ErrorHandlingHelpers.ValidateStringParameter(expectedParameters, "expectedParameters", logger);
            ErrorHandlingHelpers.ValidateStringParameter(expectedOutput, "expectedOutput", logger);
            ErrorHandlingHelpers.ValidateStringParameter(justification, "justification", logger);

            logger.LogInformation("Tool request received: {ToolName}", toolName);

            var request = new ToolRequest {
                RequestTimestamp = DateTimeOffset.UtcNow,
                ToolName = toolName,
                Description = toolDescription,
                Parameters = expectedParameters,
                ExpectedOutput = expectedOutput,
                Justification = justification
            };

            try {
                // Ensure the logs directory exists
                var logsDirectory = Path.GetDirectoryName(RequestLogFilePath);
                if (string.IsNullOrEmpty(logsDirectory)) {
                    throw new InvalidOperationException("Failed to determine logs directory path");
                }

                if (!Directory.Exists(logsDirectory)) {
                    try {
                        Directory.CreateDirectory(logsDirectory);
                    } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                        logger.LogError(ex, "Failed to create logs directory at {LogsDirectory}", logsDirectory);
                        throw new McpException($"Failed to create logs directory: {ex.Message}");
                    }
                }

                // Load existing requests if the file exists
                List<ToolRequest> existingRequests = new();
                if (File.Exists(RequestLogFilePath)) {
                    try {
                        string existingJson = await File.ReadAllTextAsync(RequestLogFilePath, cancellationToken);
                        existingRequests = JsonSerializer.Deserialize<List<ToolRequest>>(existingJson) ?? new List<ToolRequest>();
                    } catch (JsonException ex) {
                        logger.LogWarning(ex, "Failed to deserialize existing tool requests, starting with a new list");
                        // Continue with an empty list
                    } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                        logger.LogError(ex, "Failed to read existing tool requests file");
                        throw new McpException($"Failed to read existing tool requests: {ex.Message}");
                    }
                }

                // Add the new request
                existingRequests.Add(request);

                // Write the updated requests back to the file
                string jsonContent = JsonSerializer.Serialize(existingRequests, new JsonSerializerOptions {
                    WriteIndented = true
                });

                try {
                    await File.WriteAllTextAsync(RequestLogFilePath, jsonContent, cancellationToken);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    logger.LogError(ex, "Failed to write tool requests to file at {FilePath}", RequestLogFilePath);
                    throw new McpException($"Failed to save tool request: {ex.Message}");
                }

                logger.LogInformation("Tool request for '{ToolName}' has been logged to {RequestLogFilePath}", toolName, RequestLogFilePath);
                return $"Thank you for your tool request. '{toolName}' has been logged for review. Tool requests are evaluated periodically for potential implementation.";
            } catch (McpException) {
                throw;
            } catch (Exception ex) {
                logger.LogError(ex, "Failed to log tool request for '{ToolName}'", toolName);
                throw new McpException($"Failed to log tool request: {ex.Message}");
            }
        }, logger, nameof(RequestNewTool), cancellationToken);
    }

    // Define a record to store tool requests
    private record ToolRequest {
        public DateTimeOffset RequestTimestamp { get; init; }
        public string ToolName { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Parameters { get; init; } = string.Empty;
        public string ExpectedOutput { get; init; } = string.Empty;
        public string Justification { get; init; } = string.Empty;
    }
}