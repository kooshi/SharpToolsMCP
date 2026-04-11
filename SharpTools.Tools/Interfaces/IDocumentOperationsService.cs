namespace SharpTools.Tools.Interfaces;

/// <summary>
/// Service for performing file system operations on documents within a solution.
/// Provides capabilities for reading, writing, and manipulating files.
/// </summary>
public interface IDocumentOperationsService
{
    /// <summary>
    /// Reads the content of a file at the specified path
    /// </summary>
    /// <param name="filePath">The absolute path to the file</param>
    /// <param name="omitLeadingSpaces">If true, leading spaces are removed from each line</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The content of the file as a string</returns>
    Task<(string contents, int lines)> ReadFileAsync(
        string filePath,
        bool omitLeadingSpaces,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a new file with the specified content at the given path
    /// </summary>
    /// <param name="filePath">The absolute path where the file should be created</param>
    /// <param name="content">The content to write to the file</param>
    /// <param name="overwriteIfExists">Whether to overwrite the file if it already exists</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="commitMessage">The commit message to use if the file is in a Git repository</param>
    /// <returns>True if the file was created, false if it already exists and overwrite was not allowed</returns>
    Task<bool> WriteFileAsync(
        string filePath,
        string content,
        bool overwriteIfExists,
        CancellationToken cancellationToken,
        string commitMessage);

    /// <summary>
    /// Processes Git operations for multiple file paths
    /// </summary>
    /// <param name="filePaths">The file paths to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="commitMessage">The commit message to use</param>
    /// <returns>A Task representing the asynchronous operation</returns>
    Task ProcessGitOperationsAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken,
        string commitMessage);

    /// <summary>
    /// Checks if a file exists at the specified path
    /// </summary>
    /// <param name="filePath">The absolute path to check</param>
    /// <returns>True if the file exists, false otherwise</returns>
    bool FileExists(string filePath);

    /// <summary>
    /// Validates if a file path is safe to read from
    /// </summary>
    /// <param name="filePath">The absolute path to validate</param>
    /// <returns>True if the path is accessible for reading, false otherwise</returns>
    bool IsPathReadable(string filePath);

    /// <summary>
    /// Validates if a file path is safe to write to
    /// </summary>
    /// <param name="filePath">The absolute path to validate</param>
    /// <returns>True if the path is accessible for writing, false otherwise</returns>
    bool IsPathWritable(string filePath);

    /// <summary>
    /// Determines if a file is likely a source code file
    /// </summary>
    /// <param name="filePath">The file path to check</param>
    /// <returns>True if the file appears to be a code file, false otherwise</returns>
    bool IsCodeFile(string filePath);

    /// <summary>
    /// Gets information about the path in relation to the solution
    /// </summary>
    /// <param name="filePath">The path to evaluate</param>
    /// <returns>A PathInfo object with details about the path's relationship to the solution</returns>
    PathInfo GetPathInfo(string filePath);
}
