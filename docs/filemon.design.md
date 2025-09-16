# File Monitoring Design for SharpTools MCP Server

## Overview

The file monitoring system provides intelligent detection of external file changes to keep the MCP server's Roslyn workspace synchronized with the file system. It monitors all solution files for unexpected changes so the solution can be reloaded
when commands are run after external changes to solution files.

## Core Concepts

### Open Concerns

- Is watching DocumentOperationService.WriteFileAsync sufficient for discovering all expected file changes? 
- Comparison of filenames given case insensitivity on Windows?

### Comprehensive file monitoring

The file monitoring service is starts watching the solution folder recursively before the solution is loaded. Once the
solution is loaded information from Roslyn is used to control what files can actually trigger a reload. All MCP operations 
will check if a reload is needed before continuing their operation.

Monitoring always ignores certain directories altogether: ".git", "bin" and "obj".

Monitoring is halted when SolutionManager unloads the solution or when the singleton is disposed by application close.

### Tracking Expected Changes To Avoid Reloading

Operations register files they expect to modify as they discover them during execution with the expected file contents. 
This allows file monitoring to ignore expected changes and prevent unnecessary reloads.

### Integration

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

## Change detection while loading the solution

Before the solution is loaded we do not know exactly what files might necessitate a reload. Once the solution is loaded we do know what files are relevant, and will have expected changes registered before we expect to assess if a reload is need. To support this, monitoring has phases:

1.  **Early Monitoring**: The `FileMonitoringService` starts watching the solution directory for all changes *immediately* on application startup, even before it knows which files are part of the solution. It records the paths of any changed files into a temporary backlog.

2.  **Transitioning to the next monitoring phase **: After the `SolutionManager` has finished loading the solution, it provides the `FileMonitoringService` with the definitive set of "known" solution file paths. Upon receiving the set of known files, the service reconciles its backlog of recorded changes. If any of the newly provided "known" files appear in the backlog, it means they were changed during the startup process. In this case, an internal flag hadChangesWhileLoadingSolution is set to true which will cause AssessIfReloadNecessary to return true until monitoring is restarted.

3. ** Regular Monitoring**  Once the set of known solution files known then a record is kept of which of those files receive change notifications. At the same time any expected changes that are registered are kept, storing the last expected content for each registered file. When AssessIfReloadNecessary is called the list of changed relevant files are considered. If any of them don't have an expected change registered then a reload is considered necessary. If the changed file has a registered expected change then a reload is considered necessary if the file's actual content doesn't match the last registered expected content.

## Error Handling Strategy

- **FileSystemWatcher failures** → Stop monitoring and set IsReloadNeeded = true