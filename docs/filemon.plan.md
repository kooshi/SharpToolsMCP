# File Monitoring Implementation Plan

## Phase 1: Core File Monitoring Service

### 1.1 Create IFileMonitoringService Interface
```csharp
public interface IFileMonitoringService : IDisposable
{
    // Phase 1: Start monitoring immediately on startup.
    void StartMonitoring(string directory);
    void StopMonitoring();

    // Phase 2: Provide the set of files to watch after solution load.
    // This will reconcile any changes that happened in the interim.
    void SetKnownFilePaths(ISet<string> filePathsToWatch);

    bool IsMonitoring { get; }
    bool IsReloadNeeded { get; }
    void MarkReloadComplete();

    // Operation tracking with file change visitor pattern
    IFileChangeVisitor BeginOperation(string operationId);
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
    void EndOperation();
    bool IsChangeExpected(string filePath);
}
```

### 1.2 Implement FileMonitoringService
- On `StartMonitoring`, use a `FileSystemWatcher` to watch the root directory recursively. Record all file change events (path and timestamp) into a temporary, thread-safe backlog.
- On `SetKnownFilePaths`:
    - Atomically replace the internal set of known file paths.
    - Process the backlog of changes. For each change, if the file path is in the new set of known files, set `IsReloadNeeded = true` and stop processing the backlog.
    - Clear the backlog.
- For new events arriving after `SetKnownFilePaths` has been called:
    - Check if the file path is in the known set. If not, ignore it.
    - If it is a known file, proceed with the existing logic (check for active operations, etc.).
- Maintain thread-safe access to the backlog, the known file set, and the `IsReloadNeeded` flag.

### 1.3 Add to Dependency Injection
- Register `IFileMonitoringService` as singleton in `ServiceCollectionExtensions.cs`
- Ensure proper disposal through DI container

## Phase 2: Integration with SolutionManager

### 2.1 Modify SolutionManager
- In `LoadSolutionAsync`, call `_fileMonitoring.StartMonitoring(solutionDirectory)` *before* beginning the Roslyn solution load.
- After the solution is loaded successfully, gather the list of file paths.
- Call `_fileMonitoring.SetKnownFilePaths(solutionFilePaths)` to complete the setup.
- Add lazy reload check to key operations:
  - `EnsureSolutionLoaded()` method checks `fileMonitoring.IsReloadNeeded`
  - If reload needed, calls `ReloadSolutionFromDiskAsync()` and `fileMonitoring.MarkReloadComplete()`

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

          if (additionalFilePaths != null)
          // Register additional files (non-code files from tools like FindAndReplace)
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
          fileChangeVisitor.EndOperation();
      }
  }
  ```

### 2.3 Finalizing Monitor Configuration

After the solution is loaded, the `SolutionManager` provides the file monitoring service with the definitive list of files to watch. The service can then reconcile any changes that occurred during the load process.

A conceptual example of the `SolutionManager` logic:
```csharp
public async Task LoadSolutionAsync(string solutionPath)
{
    var solutionDirectory = Path.GetDirectoryName(solutionPath);
    _fileMonitoring.StartMonitoring(solutionDirectory); // Start watching immediately

    // ... proceed to load the solution with Roslyn ...
    _currentSolution = await workspace.OpenSolutionAsync(solutionPath);

    FinalizeFileMonitoring(); // Now provide the list of files
}

private void FinalizeFileMonitoring()
{
    var solution = _solutionManager.CurrentSolution;

    // 1. Discover all file paths from the Roslyn solution
    var solutionFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (!string.IsNullOrEmpty(solution.FilePath))
    {
        solutionFilePaths.Add(solution.FilePath);
    }

    foreach (var project in solution.Projects)
    {
        if (!string.IsNullOrEmpty(project.FilePath))
            solutionFilePaths.Add(project.FilePath);

        foreach (var document in project.Documents)
        {
            if (!string.IsNullOrEmpty(document.FilePath))
                solutionFilePaths.Add(document.FilePath);
        }

        // Also include AdditionalDocuments, AnalyzerConfigDocuments, etc.
        foreach (var additionalDoc in project.AdditionalDocuments)
        {
            if (!string.IsNullOrEmpty(additionalDoc.FilePath))
                solutionFilePaths.Add(additionalDoc.FilePath);
        }
        foreach (var configDoc in project.AnalyzerConfigDocuments)
        {
             if (!string.IsNullOrEmpty(configDoc.FilePath))
                solutionFilePaths.Add(configDoc.FilePath);
        }
    }

    // 2. Provide the definitive set of files to the monitoring service.
    _fileMonitoring.SetKnownFilePaths(solutionFilePaths);
    _logger.LogInformation("File monitoring is now active for {FileCount} files in the solution.", solutionFilePaths.Count);
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
          fileChangeVisitor.EndOperation(operationId);
      }
  }
  ```

## Phase 3: Configuration and Control

### 3.1 Add Configuration Options
```csharp
public class FileMonitoringOptions
{
    public bool EnableFileMonitoring { get; set; } = true;
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