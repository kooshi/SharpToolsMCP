using System.Diagnostics;

namespace SharpTools.Tools.Services;

// MSBuildWorkspace never watches the disk, so an outside edit (editor, git, another tool) was invisible and the next write
// rewrote the file from the stale buffer. This re-checks on demand at the start of every tool call instead of using a
// FileSystemWatcher (missed/duplicate events, buffer overflows): stat every document, re-read only what changed, reload
// only when files were added/removed or a project file changed. Writes are rebased onto the workspace's own solution.
public sealed partial class SolutionManager {
    private static readonly StringComparer PathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", ".idea", "node_modules", "TestResults" };
    private static readonly string[] ProjectLevelFileNames = { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json" };

    private const int HistoryLength = 16;

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    // Recent CurrentSolution snapshots, newest first. A tool's modified solution descends from one of these; diffing
    // against that origin tells exactly which documents the tool touched and which merely drifted underneath it.
    private readonly List<Solution> _history = new();
    // Last seen (mtime, length) per document; a document is only re-read when this differs.
    private readonly Dictionary<DocumentId, DiskStamp> _diskStamps = new();
    // Documents whose workspace text is stale relative to disk; re-applied over workspace.CurrentSolution after every apply.
    private readonly Dictionary<DocumentId, SourceText> _diskOverrides = new();
    private Dictionary<string, DiskStamp?> _projectFileStamps = new(PathComparer);
    private HashSet<string> _knownSourceFiles = new(PathComparer);

    private readonly record struct DiskStamp(DateTime LastWriteUtc, long Length);

    public async Task<DiskSyncResult> SyncWithDiskAsync(CancellationToken cancellationToken) {
        await _stateLock.WaitAsync(cancellationToken);
        try {
            if (!IsSolutionLoaded) {
                return DiskSyncResult.None;
            }
            var stopwatch = Stopwatch.StartNew();

            var reloadReason = DetectStructuralChange(_currentSolution);
            if (reloadReason != null) {
                _logger.LogInformation("Reloading solution from disk: {Reason}", reloadReason);
                await ReloadSolutionFromDiskAsync(cancellationToken);
                return new DiskSyncResult(Array.Empty<string>(), true, reloadReason);
            }

            var solution = _currentSolution;
            var documents = solution.Projects
                .SelectMany(p => p.Documents)
                .Where(d => d.FilePath != null)
                .Select(d => (d.Id, Path: d.FilePath!))
                .ToList();
            var refreshed = new List<string>();

            foreach (var (id, path) in documents) {
                cancellationToken.ThrowIfCancellationRequested();
                var stamp = Stat(path);
                if (stamp is null) {
                    _logger.LogInformation("Reloading solution from disk: document deleted: {Path}", path);
                    await ReloadSolutionFromDiskAsync(cancellationToken);
                    return new DiskSyncResult(Array.Empty<string>(), true, $"file deleted: {path}");
                }
                if (_diskStamps.TryGetValue(id, out var known) && known == stamp.Value) {
                    continue;
                }

                var document = solution.GetDocument(id)!;
                var currentText = await document.GetTextAsync(cancellationToken);
                var diskText = ReadSourceText(path, currentText.Encoding);
                // Only trust the stamp if the file didn't change while we were reading it; otherwise retry next call.
                if (Stat(path) == stamp) {
                    _diskStamps[id] = stamp.Value;
                }
                if (diskText.ContentEquals(currentText)) {
                    continue;
                }

                solution = solution.WithDocumentText(id, diskText, PreservationMode.PreserveValue);
                _diskOverrides[id] = diskText;
                refreshed.Add(path);
            }

            if (refreshed.Count > 0) {
                SetCurrentSolution(solution);
                _logger.LogInformation("Re-read {Count} document(s) modified outside SharpTools: {Files}", refreshed.Count, string.Join(", ", refreshed));
            }
            _logger.LogDebug("Disk sync checked {Count} documents in {Elapsed} ms", documents.Count, stopwatch.ElapsedMilliseconds);
            return new DiskSyncResult(refreshed, false, null);
        } finally {
            _stateLock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ApplyChangesAsync(Solution modifiedSolution, CancellationToken cancellationToken, Solution? baseSolution = null) {
        await _stateLock.WaitAsync(cancellationToken);
        try {
            if (!IsSolutionLoaded) {
                throw new InvalidOperationException("No solution is loaded.");
            }
            var workspace = _workspace;
            var baseline = _currentSolution;

            // A base from a previous workspace (the solution was reloaded since it was read) can't be diffed against.
            var knownBase = baseSolution ?? OperationScope.ObservedBase;
            var origin = knownBase != null && ReferenceEquals(knownBase.Workspace, workspace)
                ? knownBase
                : await FindOriginAsync(modifiedSolution, cancellationToken) ?? baseline;
            var changes = modifiedSolution.GetChanges(origin);
            if (changes.GetAddedProjects().Any() || changes.GetRemovedProjects().Any()) {
                throw new NotSupportedException("Adding or removing projects is not supported.");
            }
            // Documents changed underneath the tool since it started (another operation, or an outside edit that was synced).
            var drifted = ReferenceEquals(origin, baseline)
                ? new HashSet<DocumentId>()
                : baseline.GetChanges(origin).GetProjectChanges().SelectMany(pc => pc.GetChangedDocuments().Concat(pc.GetAddedDocuments()).Concat(pc.GetRemovedDocuments())).ToHashSet();

            var target = workspace.CurrentSolution;
            var touched = new List<(DocumentId Id, string? Path)>();
            var removed = new List<(DocumentId Id, string? Path)>();

            foreach (var projectChange in changes.GetProjectChanges()) {
                foreach (var id in projectChange.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true)) {
                    var document = modifiedSolution.GetDocument(id)!;
                    if (drifted.Contains(id)) {
                        throw new InvalidOperationException($"'{document.FilePath}' was modified by another operation or outside SharpTools after this operation started. Re-read it and retry.");
                    }
                    var text = await document.GetTextAsync(cancellationToken);
                    target = target.WithDocumentText(id, text, PreservationMode.PreserveValue);
                    touched.Add((id, document.FilePath));
                }
                foreach (var id in projectChange.GetAddedDocuments()) {
                    var document = modifiedSolution.GetDocument(id)!;
                    var text = await document.GetTextAsync(cancellationToken);
                    var loader = TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), document.FilePath));
                    target = target.AddDocument(DocumentInfo.Create(id, document.Name, document.Folders, document.SourceCodeKind, loader, document.FilePath));
                    touched.Add((id, document.FilePath));
                }
                foreach (var id in projectChange.GetRemovedDocuments()) {
                    if (drifted.Contains(id)) {
                        throw new InvalidOperationException($"'{origin.GetDocument(id)?.FilePath}' was modified by another operation or outside SharpTools after this operation started. Re-read it and retry.");
                    }
                    removed.Add((id, origin.GetDocument(id)?.FilePath));
                    target = target.RemoveDocument(id);
                }
                foreach (var id in projectChange.GetChangedAdditionalDocuments()) {
                    var text = await modifiedSolution.GetAdditionalDocument(id)!.GetTextAsync(cancellationToken);
                    target = target.WithAdditionalDocumentText(id, text, PreservationMode.PreserveValue);
                }
                foreach (var id in projectChange.GetChangedAnalyzerConfigDocuments()) {
                    var text = await modifiedSolution.GetAnalyzerConfigDocument(id)!.GetTextAsync(cancellationToken);
                    target = target.WithAnalyzerConfigDocumentText(id, text, PreservationMode.PreserveValue);
                }
            }

            if (!workspace.TryApplyChanges(target)) {
                throw new InvalidOperationException("Failed to apply changes to the workspace.");
            }

            var written = new List<string>();
            foreach (var (id, path) in touched) {
                _diskOverrides.Remove(id);
                if (path == null) {
                    continue;
                }
                written.Add(path);
                if (Stat(path) is { } stamp) {
                    _diskStamps[id] = stamp;
                }
                if (IsSourceFile(path)) {
                    _knownSourceFiles.Add(path);
                }
            }
            foreach (var (id, path) in removed) {
                _diskOverrides.Remove(id);
                _diskStamps.Remove(id);
                if (path == null) {
                    continue;
                }
                written.Add(path);
                if (!File.Exists(path)) {
                    _knownSourceFiles.Remove(path);
                }
            }

            SetCurrentSolution(ApplyOverrides(workspace.CurrentSolution));
            return written;
        } finally {
            _stateLock.Release();
        }
    }

    // Fallback when no base was observed: the solution a caller started from is the history entry it differs from the
    // least (it descends from exactly one of them, so against that one only its own edits show up as changes). Candidates
    // tie when the caller edited the very document that drifted; then take the one whose text the result is closest to.
    private async Task<Solution?> FindOriginAsync(Solution modifiedSolution, CancellationToken cancellationToken) {
        if (_history.Count == 0) {
            return null;
        }
        var ranked = _history.Select(candidate => (Solution: candidate, Changed: CountChangedDocuments(modifiedSolution.GetChanges(candidate)))).ToList();
        var fewest = ranked.Min(x => x.Changed);
        var tied = ranked.Where(x => x.Changed == fewest).Select(x => x.Solution).ToList();
        if (tied.Count == 1) {
            return tied[0];
        }
        var closest = tied[0];
        var closestDistance = long.MaxValue;
        foreach (var candidate in tied) {
            var distance = await TextDistanceAsync(modifiedSolution, candidate, cancellationToken);
            if (distance < closestDistance) {
                closest = candidate;
                closestDistance = distance;
            }
        }
        return closest;
    }

    private static async Task<long> TextDistanceAsync(Solution modified, Solution candidate, CancellationToken cancellationToken) {
        long distance = 0;
        foreach (var projectChange in modified.GetChanges(candidate).GetProjectChanges()) {
            foreach (var id in projectChange.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true)) {
                var newText = await modified.GetDocument(id)!.GetTextAsync(cancellationToken);
                var oldText = await candidate.GetDocument(id)!.GetTextAsync(cancellationToken);
                distance += newText.GetTextChanges(oldText).Sum(change => Math.Max(change.Span.Length, change.NewText?.Length ?? 0));
            }
            foreach (var id in projectChange.GetAddedDocuments()) {
                distance += (await modified.GetDocument(id)!.GetTextAsync(cancellationToken)).Length;
            }
            foreach (var id in projectChange.GetRemovedDocuments()) {
                distance += (await candidate.GetDocument(id)!.GetTextAsync(cancellationToken)).Length;
            }
        }
        return distance;
    }

    private void SetCurrentSolution(Solution solution) {
        _currentSolution = solution;
        _history.Insert(0, solution);
        if (_history.Count > HistoryLength) {
            _history.RemoveRange(HistoryLength, _history.Count - HistoryLength);
        }
        _compilationCache.Clear();
        _semanticModelCache.Clear();
    }

    private static int CountChangedDocuments(SolutionChanges changes) =>
        changes.GetProjectChanges().Sum(pc => pc.GetChangedDocuments().Count() + pc.GetAddedDocuments().Count() + pc.GetRemovedDocuments().Count())
        + changes.GetAddedProjects().Count() + changes.GetRemovedProjects().Count();

    private async Task SnapshotDiskStateAsync(Solution solution, CancellationToken cancellationToken) {
        ResetDiskState();
        _history.Add(solution);
        foreach (var document in solution.Projects.SelectMany(p => p.Documents)) {
            if (document.FilePath is not { } path || Stat(path) is not { } stamp) {
                continue;
            }
            // Roslyn loads text lazily; materialize it now so the recorded stamp describes the text we actually hold and a
            // later outside edit is recognised as such instead of being read silently on first use.
            await document.GetTextAsync(cancellationToken);
            _diskStamps[document.Id] = stamp;
        }
        _projectFileStamps = CollectProjectFiles(solution).ToDictionary(p => p, Stat, PathComparer);
        _knownSourceFiles = ScanSourceFiles(solution);
        _logger.LogDebug("Disk state snapshot: {Documents} documents, {SourceFiles} source files, {ProjectFiles} project files",
            _diskStamps.Count, _knownSourceFiles.Count, _projectFileStamps.Count);
    }

    private void ResetDiskState() {
        OperationScope.Forget();
        _history.Clear();
        _diskStamps.Clear();
        _diskOverrides.Clear();
        _projectFileStamps = new Dictionary<string, DiskStamp?>(PathComparer);
        _knownSourceFiles = new HashSet<string>(PathComparer);
    }

    private Solution ApplyOverrides(Solution solution) {
        foreach (var (id, text) in _diskOverrides.ToList()) {
            var document = solution.GetDocument(id);
            if (document == null || (document.TryGetText(out var current) && current.ContentEquals(text))) {
                _diskOverrides.Remove(id);
                continue;
            }
            solution = solution.WithDocumentText(id, text, PreservationMode.PreserveValue);
        }
        return solution;
    }

    private string? DetectStructuralChange(Solution solution) {
        foreach (var (path, stamp) in _projectFileStamps) {
            if (Stat(path) != stamp) {
                return $"project file changed: {path}";
            }
        }
        var current = ScanSourceFiles(solution);
        if (current.SetEquals(_knownSourceFiles)) {
            return null;
        }
        var added = current.Except(_knownSourceFiles, PathComparer).ToList();
        var removed = _knownSourceFiles.Except(current, PathComparer).ToList();
        var parts = new List<string>();
        if (added.Count > 0) {
            parts.Add($"{added.Count} source file(s) added ({string.Join(", ", added.Take(3))})");
        }
        if (removed.Count > 0) {
            parts.Add($"{removed.Count} source file(s) removed ({string.Join(", ", removed.Take(3))})");
        }
        return string.Join("; ", parts);
    }

    private static IEnumerable<string> CollectProjectFiles(Solution solution) {
        var files = new HashSet<string>(PathComparer);
        if (solution.FilePath is { } solutionPath) {
            files.Add(solutionPath);
        }
        var solutionDir = solution.FilePath == null ? null : Path.GetDirectoryName(solution.FilePath);
        foreach (var project in solution.Projects) {
            if (project.FilePath is not { } projectPath) {
                continue;
            }
            files.Add(projectPath);
            for (var dir = Path.GetDirectoryName(projectPath); dir != null; dir = Path.GetDirectoryName(dir)) {
                foreach (var name in ProjectLevelFileNames) {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) {
                        files.Add(candidate);
                    }
                }
                if (solutionDir != null && PathComparer.Equals(dir, solutionDir)) {
                    break;
                }
            }
        }
        return files;
    }

    private static HashSet<string> ScanSourceFiles(Solution solution) {
        var roots = solution.Projects
            .Select(p => Path.GetDirectoryName(p.FilePath))
            .OfType<string>()
            .Distinct(PathComparer)
            .ToList();
        // A root nested under another root is already covered by the walk of the outer one.
        roots = roots.Where(root => !roots.Any(other => !PathComparer.Equals(other, root) && IsUnder(root, other))).ToList();

        var files = new HashSet<string>(PathComparer);
        foreach (var root in roots) {
            Walk(new DirectoryInfo(root), files);
        }
        return files;
    }

    private static void Walk(DirectoryInfo directory, HashSet<string> files) {
        IEnumerable<FileSystemInfo> entries;
        try {
            entries = directory.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint });
        } catch (IOException) {
            return;
        } catch (UnauthorizedAccessException) {
            return;
        }
        foreach (var entry in entries) {
            if (entry is DirectoryInfo subdirectory) {
                if (IgnoredDirectoryNames.Contains(subdirectory.Name) || subdirectory.Name.StartsWith('.')) {
                    continue;
                }
                Walk(subdirectory, files);
            } else if (IsSourceFile(entry.FullName)) {
                files.Add(entry.FullName);
            }
        }
    }

    private static bool IsUnder(string path, string ancestor) {
        var prefix = ancestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSourceFile(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static DiskStamp? Stat(string path) {
        var info = new FileInfo(path);
        return info.Exists ? new DiskStamp(info.LastWriteTimeUtc, info.Length) : null;
    }

    private static SourceText ReadSourceText(string path, Encoding? defaultEncoding) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SourceText.From(stream, defaultEncoding);
    }
}
