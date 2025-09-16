namespace SharpTools.Tools.Interfaces;

/// <summary>
/// Service for monitoring file changes and coordinating with MCP operations to detect external modifications.
/// </summary>
public interface IFileMonitoringService : IDisposable
{
    /// <summary>
    /// Start monitoring a directory for file changes.
    /// </summary>
    /// <param name="directory">Directory to monitor recursively</param>
    void StartMonitoring(string directory);

    /// <summary>
    /// Stop monitoring.
    /// </summary>
    void StopMonitoring();
    
    /// <summary>
    /// Provide the set of files to watch after solution load.
    /// This will reconcile any changes that happened during startup.
    /// </summary>
    /// <param name="filePathsToWatch">Set of file paths that are part of the solution</param>
    void SetKnownFilePaths(ISet<string> filePathsToWatch);

    /// <summary>
    /// Whether a solution reload is needed due to external file changes.
    /// </summary>
    bool IsReloadNeeded { get; }

    /// <summary>
    /// Register an expected file change for backward compatibility.
    /// </summary>
    /// <param name="filePath">File path that will be modified</param>
    void RegisterExpectedChange(string filePath);
}