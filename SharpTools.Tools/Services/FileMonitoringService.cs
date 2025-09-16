using SharpTools.Tools.Interfaces;

namespace SharpTools.Tools.Services;

/// <summary>
/// Stub implementation of file monitoring service.
/// This implementation has no side effects and does not trigger reloads.
/// </summary>
public sealed class FileMonitoringService : IFileMonitoringService
{
    private readonly ILogger<FileMonitoringService> _logger;
    private bool _disposed;

    public FileMonitoringService(ILogger<FileMonitoringService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void StartMonitoring(string directory)
    {
        _logger.LogInformation("Starting file monitoring for directory: {Directory}", directory);
    }

    public void StopMonitoring()
    {
        _logger.LogInformation("Stopping file monitoring");
    }

    public void SetKnownFilePaths(ISet<string> filePathsToWatch)
    {
        _logger.LogInformation("Setting known file paths: {FileCount} files", filePathsToWatch.Count);
    }
    public Task<bool> AssessIfReloadNecessary() {
        return Task.FromResult(false);
    }

    public void RegisterExpectedChange(string filePath, string fileContents)
    {
        _logger.LogTrace("Registering expected change to {FilePath}", filePath);
    }

    public void Dispose()
    {
        if (_disposed) return;

        StopMonitoring();
        _disposed = true;
    }
}