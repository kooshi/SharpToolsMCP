# File Monitoring Design for SharpTools MCP Server

## Overview

The file monitoring system provides intelligent detection of external file changes to keep the MCP server's Roslyn workspace synchronized with the file system. It monitors all solution files for changes while ignoring expected changes.

## Core Concepts

### Open Concerns

- Is watching DocumentOperationService.WriteFileAsync sufficient for discovering all expected file changes? 
- Comparison of filenames given case insensitivity on Windows?

### Comprehensive file monitoring

The file monitoring service is starts watching the solution folder recursively before the solution build starts. Once the
build is complete information from Roslyn is used to control what files can actually trigger a reload. All MCP operations 
will check if a reload is needed before continuing their operation.

Monitoring always ignores certain directories altogether: ".git", "bin" and "obj".

Monitoring is halted when SolutionManager unloads the solution or when singleton is disposed by application close.

### Tracking Expected Changes To Avoid Reloading

Operations register files they expect to modify as they discover them during execution. This allows file monitoring to ignore expected changes and prevent unnecessary reloads.

### Implementation

A singleton implementation of IFileMonitoringService tracks changes. SolutionManager makes calls indicating when monitoring
should start and what files are known to be relevant. DocumentOperationService notifies when any expected changes are written.
ToolsHelper's EnsureSolutionLoaded* methods, which are called at the start of every MCP operation, notify SolutionManager to
reload the solution if appropriate.

## File Discovery and Monitoring Strategy

To minimze monitoring calls made to the operating system, the system watches the entire solution's root directory recursively. This automatically handles file additions and deletions without complex tracking.

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
   - Yes -> Set IsReloadNeeded = true and stop monitoring
   - No -> Continue.
2. Clear the backlog

### Real-time Change Detection (after initialization)
For each file change detected by FileSystemWatcher:
1. Was an expected change registered for this file?
   - No → Set IsReloadNeeded = true (external change) and stop monitoring
   - Yes → Remove file from set of expected changes

```

## Error Handling Strategy

- **FileSystemWatcher failures** → Stop monitoring and set IsReloadNeeded = true