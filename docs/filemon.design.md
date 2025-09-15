# File Monitoring Design for SharpTools MCP Server

## Overview

The file monitoring system provides intelligent detection of external file changes to keep the MCP server's Roslyn workspace synchronized with the file system. It uses a **visitor pattern** to distinguish between self-generated changes (from MCP operations) and external changes (from IDE editing), triggering lazy reloads only when necessary.

## Core Concepts

### File Change Visitor Pattern

Operations use an `IFileChangeVisitor` to register files they expect to modify as they discover them during execution. This solves the problem of predicting file changes upfront, which is difficult with Roslyn's dynamic solution modification process.

```csharp
// Operation gets visitor and registers expected changes as discovered
var fileChangeVisitor = _fileMonitoring.BeginOperation(operationId);
fileChangeVisitor.ExpectChange("C:\Solution\MyClass.cs");  // As we discover changes
fileChangeVisitor.ExpectChanges(additionalFiles);         // Batch registration
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
    void EndOperation(string operationId);
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
    int ExpectedChangeCount { get; }
    int ActualChangeCount { get; }
}
```

### Integration Points

**SolutionManager**: Checks `IsReloadNeeded` in `ToolHelpers.EnsureSolutionLoaded()` which all MCP tools use

**CodeModificationService**: Uses visitor to register Roslyn solution changes as they're discovered:
- Changed documents
- Added documents
- Removed documents
- Git metadata files

**DocumentOperationsService**: Registers non-Roslyn file operations (like `.editorconfig` edits)

**MCP Tools**: Can register additional expected changes for complex operations

## File Discovery

The system monitors files discovered through Roslyn APIs:

- **Solution file** (`.sln`) - Main solution file
- **Project files** (`.csproj`, `.vbproj`, `.fsproj`) - All project files in solution
- **Source files** (`.cs`, `.vb`, `.fs`) - All documents in all projects
- **Configuration files** (`.editorconfig`) - Used by EditorConfigProvider

## Change Detection Logic

```
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
4. EndOperation → No reload needed ✓
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