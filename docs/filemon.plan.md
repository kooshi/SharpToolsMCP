# File Monitoring Implementation Plan

## Phase 1: Core File Monitoring Service

### 1.1 Create IFileMonitoringService Interface
```csharp
public interface IFileMonitoringService : IDisposable
{
    void StartMonitoring(string solutionPath);
    void StopMonitoring();
    void AddWatchPath(string filePath);
    void RemoveWatchPath(string filePath);
    bool IsMonitoring { get; }
    bool IsReloadNeeded { get; }
    void MarkReloadComplete();

    // Operation tracking with file change visitor pattern
    IFileChangeVisitor BeginOperation(string operationId);
    void EndOperation(string operationId);
    bool IsOperationInProgress(string operationId);

    // For backward compatibility and simple operations
    void RegisterExpectedChange(string operationId, string filePath);
    bool IsChangeExpected(string filePath);
    void MarkExpectedChangeOccurred(string filePath);
}

public interface IFileChangeVisitor
{
    string OperationId { get; }
    void ExpectChange(string filePath);
    void ExpectChanges(IEnumerable<string> filePaths);
    bool IsChangeExpected(string filePath);
    int ExpectedChangeCount { get; }
    int ActualChangeCount { get; }
}
```

### 1.2 Implement FileMonitoringService
- Use `FileSystemWatcher` for efficient file system monitoring
- Monitor relevant file extensions: `.sln`, `.csproj`, `.vbproj`, `.fsproj`, `.cs`, `.vb`, `.fs`, `.editorconfig`
- **Expected Change Tracking**: For each operation, track exactly which files are expected to change
- **Precise External Change Detection**: Trigger reload only when:
  - File changes and no operation is active, OR
  - File changes but it wasn't in the expected files list for current operations, OR
  - Same file changes multiple times during a single operation (indicates external interference)
- **Change Accounting**: Track which expected changes have occurred vs. which are still pending
- Handle watcher disposal and error recovery
- Support both directory-based and individual file monitoring

### 1.3 Add to Dependency Injection
- Register `IFileMonitoringService` as singleton in `ServiceCollectionExtensions.cs`
- Ensure proper disposal through DI container

## Phase 2: Integration with SolutionManager

### 2.1 Modify SolutionManager
- Add `IFileMonitoringService` dependency injection
- Add lazy reload check to key operations:
  - `EnsureSolutionLoaded()` method checks `fileMonitoring.IsReloadNeeded`
  - If reload needed, calls `ReloadSolutionFromDiskAsync()` and `fileMonitoring.MarkReloadComplete()`
- Setup monitoring in `LoadSolutionAsync()` after successful load

### 2.2 Modify CodeModificationService
- Add `IFileMonitoringService` dependency injection
- Use visitor pattern to dynamically register expected file changes as they're discovered:
  ```csharp
  public async Task ApplyChangesAsync(Solution newSolution, CancellationToken cancellationToken, string commitMessage, IEnumerable<string>? additionalFilePaths = null)
  {
      var operationId = Guid.NewGuid().ToString();
      var fileChangeVisitor = _fileMonitoring.BeginOperation(operationId);

      try
      {
          var originalSolution = _solutionManager.CurrentSolution!;
          var solutionChanges = newSolution.GetChanges(originalSolution);

          // Register expected changes as we discover them during processing
          foreach (var projectChange in solutionChanges.GetProjectChanges())
          {
              // Register changed documents
              foreach (var changedDocumentId in projectChange.GetChangedDocuments())
              {
                  var document = newSolution.GetDocument(changedDocumentId);
                  if (document?.FilePath != null)
                  {
                      fileChangeVisitor.ExpectChange(document.FilePath);
                      _logger.LogTrace("Expecting change to: {FilePath}", document.FilePath);
                  }
              }

              // Register added documents
              foreach (var addedDocumentId in projectChange.GetAddedDocuments())
              {
                  var document = newSolution.GetDocument(addedDocumentId);
                  if (document?.FilePath != null)
                  {
                      fileChangeVisitor.ExpectChange(document.FilePath);
                      _logger.LogTrace("Expecting new file: {FilePath}", document.FilePath);
                  }
              }

              // Register removed documents
              foreach (var removedDocumentId in projectChange.GetRemovedDocuments())
              {
                  var document = originalSolution.GetDocument(removedDocumentId);
                  if (document?.FilePath != null)
                  {
                      fileChangeVisitor.ExpectChange(document.FilePath);
                      _logger.LogTrace("Expecting removal of: {FilePath}", document.FilePath);
                  }
              }
          }

          // Register additional files (non-code files from tools like FindAndReplace)
          if (additionalFilePaths != null)
          {
              fileChangeVisitor.ExpectChanges(additionalFilePaths);
          }

          // Apply changes to workspace - this triggers the actual file writes
          if (workspace.TryApplyChanges(finalSolutionToApply))
          {
              _solutionManager.RefreshCurrentSolution();
          }

          // Register git files if git operations enabled
          if (_gitEnabled && !string.IsNullOrEmpty(commitMessage))
          {
              fileChangeVisitor.ExpectChange(".git/index");
              fileChangeVisitor.ExpectChange(".git/logs/HEAD");
              fileChangeVisitor.ExpectChange(".git/COMMIT_EDITMSG");
              await CommitChangesIfEnabled(commitMessage, cancellationToken);
          }
      }
      finally
      {
          _fileMonitoring.EndOperation(operationId);
          _logger.LogDebug("Operation {OperationId} completed. Expected: {Expected}, Actual: {Actual}",
              operationId, fileChangeVisitor.ExpectedChangeCount, fileChangeVisitor.ActualChangeCount);
      }
  }
  ```

### 2.3 File Discovery Integration
```csharp
private void SetupFileMonitoring()
{
    // Monitor solution file
    _fileMonitoring.AddWatchPath(_currentSolution.FilePath);

    // Monitor all project files
    foreach (var project in _currentSolution.Projects)
    {
        if (!string.IsNullOrEmpty(project.FilePath))
            _fileMonitoring.AddWatchPath(project.FilePath);

        // Monitor all source documents
        foreach (var document in project.Documents)
        {
            if (!string.IsNullOrEmpty(document.FilePath))
                _fileMonitoring.AddWatchPath(document.FilePath);
        }
    }

    // Monitor .editorconfig files in solution directory tree
    MonitorEditorConfigFiles(Path.GetDirectoryName(_currentSolution.FilePath));
}
```

### 2.4 Lazy Reload Integration
- Add reload check to `ToolHelpers.EnsureSolutionLoaded()` method
- All MCP tools use this helper, ensuring automatic reload when needed
- Add logging when reload is triggered
- Handle edge cases (file deletion, temporary files, etc.)

### 2.5 Integration with Document Operations
- Modify `DocumentOperationsService.WriteFileAsync()` to accept file change visitor for tracking:
  ```csharp
  public async Task WriteFileAsync(string filePath, string content, bool overwrite,
      CancellationToken cancellationToken, string commitMessage, IFileChangeVisitor? fileChangeVisitor = null)
  {
      // Register expected change just before performing the write
      fileChangeVisitor?.ExpectChange(filePath);

      // If git is enabled, also expect git metadata changes
      if (_gitEnabled && !string.IsNullOrEmpty(commitMessage))
      {
          fileChangeVisitor?.ExpectChange(".git/index");
          fileChangeVisitor?.ExpectChange(".git/logs/HEAD");
      }

      // ... perform file write
  }
  ```

### 2.6 High-Level Tool Integration
- Modify MCP tools to pass file change visitor to sub-operations:
  ```csharp
  [McpServerTool(Name = "sharp_find_and_replace")]
  public static async Task<string> FindAndReplace(/*...*/)
  {
      var operationId = Guid.NewGuid().ToString();
      var fileChangeVisitor = fileMonitoring.BeginOperation(operationId);

      try
      {
          // Tools can register expected changes as they discover them
          if (isNonCodeFileOperation)
          {
              fileChangeVisitor.ExpectChange(targetFilePath);
          }

          await modificationService.ApplyChangesAsync(newSolution, cancellationToken, commitMessage);
          // ApplyChangesAsync will register its own expected changes via the fileChangeVisitor
      }
      finally
      {
          fileMonitoring.EndOperation(operationId);
      }
  }
  ```

## Phase 3: Configuration and Control

### 3.1 Add Configuration Options
```csharp
public class FileMonitoringOptions
{
    public bool EnableFileMonitoring { get; set; } = true;
    public string[] MonitoredExtensions { get; set; } = { ".cs", ".vb", ".fs", ".sln", ".csproj", ".vbproj", ".fsproj", ".editorconfig" };
    public bool LogFileChanges { get; set; } = true;
}
```

### 3.2 Add MCP Tool for Control
```csharp
[McpServerTool(Name = "sharp_configure_file_monitoring")]
public static object ConfigureFileMonitoring(
    ISolutionManager solutionManager,
    [Description("Enable or disable automatic file monitoring")] bool enabled)
```

## Phase 4: Testing and Validation

### 4.1 Unit Tests
- Test FileMonitoringService file detection and flag setting
- Test lazy reload logic in SolutionManager
- Test integration with ToolHelpers.EnsureSolutionLoaded()
- Mock file system changes for reliable testing

### 4.2 Integration Tests
- Test with real solution files
- Verify lazy reload triggered on MCP requests after file changes
- Test performance impact of monitoring large solutions
- Test edge cases (network drives, permission issues)

### 4.3 Performance Testing
- Measure impact on solution loading time
- Test with large solutions (100+ projects)
- Memory usage analysis for long-running monitoring