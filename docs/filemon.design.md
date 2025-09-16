# File Monitoring Design for SharpTools MCP Server

## Overview

The file monitoring system provides intelligent detection of external file changes to keep the MCP server's Roslyn workspace synchronized with the file system. It monitors all solution files for changes while ignoring expected changes.

## Core Concepts

### Comprehensive file monitoring

The file monitoring service is starts watching the solution folder recursively before the solution build starts. Once the
build is complete information from Roslyn is used to control what files can actually trigger a reload. All MCP operations 
will check if a reload is needed before continuing their operation.

### Tracking Expected Changes To Avoid Reloading

Operations use an `IFileChangeVisitor` to register files they expect to modify as they discover them during execution. This allows file monitoring to ignore expected changes and prevent unnecessary reloads.

```csharp
// Operation gets visitor and registers expected changes as discovered
var fileChangeVisitor = _fileMonitoring.BeginOperation(operationId);
fileChangeVisitor.ExpectChange("C:\Solution\MyClass.cs");  // As we discover changes
fileChangeVisitor.ExpectChanges(additionalFiles);         // Batch registration
fileChangeVisitor.EndOperation();
```

### Lazy Reload Strategy

Instead of immediately reloading when files change, the system sets an `IsReloadNeeded` flag and reloads only when the next MCP request is made. This provides:

- **Zero overhead** when no files have changed
- **Natural batching** of multiple file changes
- **User sees delay only when they make a request** after changes

### External Change Detection

The system triggers reload only in these scenarios:

1. **File changes while no operations are active** - Clearly external
2. **Unexpected file changes during operation** - File wasn't in expected list
3. **Multiple changes to same file during operation** - Indicates external interference

## Architecture Components

### IFileMonitoringService

Central service that coordinates file watching and operation tracking:

```csharp
public interface IFileMonitoringService : IDisposable
{
    // Basic monitoring
    void StartMonitoring(string solutionPath);
    bool IsReloadNeeded { get; }
    void MarkReloadComplete();

    // Visitor pattern for operations
    IFileChangeVisitor BeginOperation(string operationId);
}
```

### IFileChangeVisitor

Visitor that operations use to register expected file changes:

```csharp
public interface IFileChangeVisitor
{
    string OperationId { get; }
    void ExpectChange(string filePath);
    void ExpectChanges(IEnumerable<string> filePaths);
    void EndOperation();
    int ExpectedChangeCount { get; }
    int ActualChangeCount { get; }
}
```

### Integration Points

**SolutionManager**: Checks `IsReloadNeeded` in `ToolHelpers.EnsureSolutionLoaded()` and `ToolHelpers.EnsureSolutionLoadedWithDetails()` which all MCP tools use

**CodeModificationService**: Uses visitor to register Roslyn solution changes as they're discovered:
- Changed documents
- Added documents
- Removed documents
- Git metadata files

**DocumentOperationsService**: Registers non-Roslyn file operations (like `.editorconfig` edits)

**MCP Tools**: Can register additional expected changes for complex operations

## File Discovery and Monitoring Strategy

To simplify monitoring, the system watches the entire solution's root directory recursively. This automatically handles file additions and deletions without complex tracking.

The monitoring is made efficient and relevant by filtering events against the actual files that make up the solution:

1.  **Directory Watching**: A single `FileSystemWatcher` is configured to monitor the solution's root directory and all its subdirectories for any file changes.
2.  **Known File Set**: On solution load, the `SolutionManager` inspects the Roslyn workspace to get a complete list of all file paths that are part of the solution. This includes project files, source documents, additional files, and analyzer configuration files (like `.editorconfig`). This list is stored in a fast-lookup data structure like a `HashSet<string>`.
3.  **Event Filtering**: When the `FileSystemWatcher` raises a file change event, the `FileMonitoringService` checks if the full path of the changed file exists in the set of known solution files. Changes to files not in this set are ignored.

This approach ensures that the system only reacts to changes in files it genuinely cares about, providing a balance of simplicity (one watcher) and precision (filtering by exact file path).

## Handling Race Conditions on Startup

A potential race condition exists where a file could be modified by an external process after the application starts but before the Roslyn solution has been fully loaded and analyzed. To solve this, the monitoring service uses a two-phase initialization:

1.  **Early Monitoring**: The `FileMonitoringService` starts watching the solution directory for all changes *immediately* on application startup, even before it knows which files are part of the solution. It records any file change events, including the file path and the time of the change, into a temporary backlog.

2.  **Late Binding of Known Files**: After the `SolutionManager` has finished loading the solution, it provides the `FileMonitoringService` with the definitive set of "known" solution file paths.

3.  **Backlog Reconciliation**: Upon receiving the set of known files, the service reconciles its backlog of recorded changes. If any of the newly provided "known" files appear in the backlog, it means they were changed during the startup process. In this case, the `IsReloadNeeded` flag is immediately set to `true` to force a workspace reload on the next request, ensuring synchronization.

This approach closes the gap and guarantees that any changes occurring during the solution load process are correctly detected.

## Change Detection Logic

### Initial Reconciliation (when known files are set)
For each file in the provided set of known solution files:
1. Does this file exist in the backlog of changes recorded since monitoring began?
   - Yes -> Set IsReloadNeeded = true, and the backlog can be cleared.
   - No -> Continue.

### Real-time Change Detection (after initialization)
For each file change detected by FileSystemWatcher:
1. Is any operation currently active?
   - No → Set IsReloadNeeded = true (external change)
   - Yes → Continue to step 2

2. Is this file expected by any active operation?
   - No → Set IsReloadNeeded = true (unexpected change)
   - Yes → Continue to step 3

3. Has this file already changed during this operation?
   - No → Mark as occurred, continue monitoring
   - Yes → Set IsReloadNeeded = true (external interference)
```

## Example Scenarios

### Pure MCP Operation (No Reload)
```
1. sharp_add_member → BeginOperation with visitor
2. CodeModificationService registers expected file: MyClass.cs
3. File watcher sees MyClass.cs change → Expected, mark as occurred
4. visitor.EndOperation() → No reload needed ✓
```

### External Change During Operation (Triggers Reload)
```
1. sharp_add_member → BeginOperation, expects MyClass.cs
2. MyClass.cs changes (expected) → Mark as occurred
3. User edits MyClass.cs again → Same file, 2nd change → IsReloadNeeded = true
4. Next MCP request → Reload triggered ✓
```

### External Change While Idle (Triggers Reload)
```
1. No operations active
2. User edits MyClass.cs → No operation to check against → IsReloadNeeded = true
3. Next MCP request → Reload triggered ✓
```

## Performance Characteristics

- **Zero impact** when no files change
- **Minimal overhead** during operations (just registering expected files)
- **FileSystemWatcher efficiency** for change detection
- **Lazy loading** avoids unnecessary work
- **Request-time cost** only when reload actually needed

## Error Handling Strategy

- **FileSystemWatcher failures** → Restart monitoring, log errors
- **Operation tracking failures** → Fail safe by assuming external changes
- **Reload failures** → Log error, maintain current state, notify user
- **Unknown file changes** → Treat as potentially external (better safe than sorry)