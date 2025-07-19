using ModelContextProtocol;

namespace SharpTools.Tools.Mcp;

/// <summary>
/// Provides centralized error handling helpers for SharpTools.
/// </summary>
internal static class ErrorHandlingHelpers {
    /// <summary>
    /// Executes a function with comprehensive error handling and logging.
    /// </summary>
    public static async Task<T> ExecuteWithErrorHandlingAsync<T, TLogCategory>(
        Func<Task<T>> operation,
        ILogger<TLogCategory> logger,
        string operationName,
        CancellationToken cancellationToken,
        [CallerMemberName] string callerName = "") {
        try {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation();
        } catch (OperationCanceledException) {
            logger.LogWarning("{Operation} operation in {Caller} was cancelled", operationName, callerName);
            throw new McpException($"The operation '{operationName}' was cancelled by the user or system.");
        } catch (McpException ex) {
            logger.LogError("McpException in {Operation} ({Caller}): {Message}", operationName, callerName, ex.Message);
            throw;
        } catch (ArgumentException ex) {
            logger.LogError(ex, "Invalid argument in {Operation} ({Caller}): {Message}", operationName, callerName, ex.Message);
            throw new McpException($"Invalid argument for '{operationName}': {ex.Message}");
        } catch (InvalidOperationException ex) {
            logger.LogError(ex, "Invalid operation in {Operation} ({Caller}): {Message}", operationName, callerName, ex.Message);
            throw new McpException($"Operation '{operationName}' failed: {ex.Message}");
        } catch (FileNotFoundException ex) {
            logger.LogError(ex, "File not found in {Operation} ({Caller}): {Message}", operationName, callerName, ex.Message);
            throw new McpException($"File not found during '{operationName}': {ex.Message}");
        } catch (IOException ex) {
            logger.LogError(ex, "IO error in {Operation} ({Caller}): {Message}", operationName, callerName, ex.Message);
            throw new McpException($"File operation error during '{operationName}': {ex.Message}");
        } catch (UnauthorizedAccessException ex) {
            logger.LogError(ex, "Access denied in {Operation} ({Caller}): {Message}", operationName, callerName, ex.Message);
            throw new McpException($"Access denied during '{operationName}': {ex.Message}");
        } catch (Exception ex) {
            logger.LogError(ex, "Unhandled exception in {Operation} ({Caller}): {Message}", operationName, callerName, ex.Message);
            throw new McpException($"An unexpected error occurred during '{operationName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Validates that a parameter is not null or whitespace.
    /// </summary>
    public static void ValidateStringParameter(string? value, string paramName, ILogger logger) {
        if (string.IsNullOrWhiteSpace(value)) {
            logger.LogError("Parameter validation failed: {ParamName} is null or empty", paramName);
            throw new McpException($"Parameter '{paramName}' cannot be null or empty.");
        }
    }

    /// <summary>
    /// Validates that a file path is valid and not empty.
    /// </summary>
    public static void ValidateFilePath(string? filePath, ILogger logger) {
        ValidateStringParameter(filePath, "filePath", logger);

        try {
            // Check if the path is valid
            var fullPath = Path.GetFullPath(filePath!);

            // Additional checks if needed (e.g., file exists, is accessible, etc.)
        } catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) {
            logger.LogError(ex, "Invalid file path: {FilePath}", filePath);
            throw new McpException($"Invalid file path: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates that a file exists at the specified path.
    /// </summary>
    public static void ValidateFileExists(string? filePath, ILogger logger) {
        ValidateFilePath(filePath, logger);

        if (!File.Exists(filePath)) {
            logger.LogError("File does not exist at path: {FilePath}", filePath);
            throw new McpException($"File does not exist at path: {filePath}");
        }
    }
    /// <summary>
    /// Checks for compilation errors in a document after code has been modified.
    /// </summary>
    /// <param name="solutionManager">The solution manager</param>
    /// <param name="document">The document to check for errors</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A tuple containing (hasErrors, errorMessages)</returns>
    public static async Task<(bool HasErrors, string ErrorMessages)> CheckCompilationErrorsAsync<TLogCategory>(
        ISolutionManager solutionManager,
        Document document,
        ILogger<TLogCategory> logger,
        CancellationToken cancellationToken) {

        // Delegate to the centralized implementation in ContextInjectors
        return await ContextInjectors.CheckCompilationErrorsAsync(solutionManager, document, logger, cancellationToken);
    }
}
