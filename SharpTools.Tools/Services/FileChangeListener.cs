using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SharpTools.Tools.Services
{
    public class FileChangeListener : IDisposable {
        private static readonly char[] pathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
        private readonly ILogger _logger;
        private readonly object _lock = new();
        private readonly FileSystemWatcher _watcher;
        private bool _enabled = true;

        private readonly List<string> _knownFilePaths = new ();
        private readonly List<string> _changedFiles = new();
        private readonly Dictionary<string, string> _expectedChanges = new(StringComparer.OrdinalIgnoreCase);
        private bool _reloadIsNecessary;

        public FileChangeListener(ILogger logger, string directory)
        {
            _logger = logger;
            _watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Error += OnError;
        }

        public void SetKnownFilePaths(ISet<string> filePathsToWatch)
        {
            lock (_lock) {
                if (_knownFilePaths.Any())
                    throw new InvalidOperationException($"{nameof(SetKnownFilePaths)} should only be called once.");
                
                _logger.LogInformation("Setting known file paths: {FileCount} files", filePathsToWatch.Count);
                _knownFilePaths.AddRange(filePathsToWatch);

                if (_changedFiles.Any(changedFile => _knownFilePaths.Contains(changedFile)))
                {
                    _reloadIsNecessary = true;
                    _logger.LogWarning("File changed during solution load.");
                }
                
                _changedFiles.Clear();
            }
        }

        public async Task<bool> AssessIfReloadNecessary() {

            Dictionary<string, string>? expectedChanges = null;
            HashSet<string>? knownFilePaths = null;
            List<string>?  changedFiles = null;
            
            bool needReload;
            
            lock (_lock) {
                
                needReload = _reloadIsNecessary;

                if (!needReload) {
                    expectedChanges = _expectedChanges.ToDictionary();
                    knownFilePaths = new HashSet<string>(_knownFilePaths);
                    changedFiles = _changedFiles.ToList();   
                }
            }

            if (!needReload) {
                needReload = await AssessIfReloadNecessaryInternal(_logger, expectedChanges!, knownFilePaths!, changedFiles!);
            }

            if (needReload) {
                // Disable this listener as it no longer needs to monitor files.
                Disable();
            }
            
            return needReload;
        }
        
        private static async Task<bool> AssessIfReloadNecessaryInternal(
            ILogger logger, 
            IReadOnlyDictionary<string, string> expectedChanges, 
            IReadOnlyCollection<string> knownFilePaths, 
            IReadOnlyCollection<string> changedFiles)
        {
            foreach (var changedFile in changedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var isKnown = knownFilePaths.Contains(changedFile);;

                if (isKnown)
                {
                    if (!File.Exists(changedFile))
                    {
                        logger.LogWarning("Known file {File} was deleted.", changedFile);
                        return true;
                    }

                    var hasExpectedChange = expectedChanges.TryGetValue(changedFile, out var expectedContent);

                    if (hasExpectedChange)
                    {
                        try
                        {
                            var actualContent = await File.ReadAllTextAsync(changedFile);
                            if (actualContent != expectedContent)
                            {
                                logger.LogWarning("File content mismatch for {File}", changedFile);
                                return true;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Error reading file {File} for comparison", changedFile);
                            return true;
                        }
                    }
                    else
                    {
                        logger.LogWarning("Unexpected file change for {File}", changedFile);
                        return true;
                    }
                }
            }

            return false;
        }

        public void RegisterExpectedChange(string filePath, string fileContents)
        {
            lock (_lock)
            {
                _logger.LogTrace("Registering expected change to {FilePath}", filePath);
                _expectedChanges[filePath] = fileContents;
            }
        }

        public static bool IsPathIgnored(string basePath, string path, char[] pathSeparators)
        {
            var relativePath = Path.GetRelativePath(basePath, path);
            var pathParts = relativePath.Split(pathSeparators);
            if (pathParts.Length > 0)
            {
                
                if (pathParts.Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            return false;
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            lock (_lock)
            {
                if (!_enabled) return;
                if (IsPathIgnored(_watcher.Path, e.FullPath, pathSeparators))
                {
                    _logger.LogTrace("Ignoring change in {File} because it is in an ignored directory.", e.FullPath);
                    return;
                }
                _logger.LogTrace("File change event: {ChangeType} for {File}", e.ChangeType, e.FullPath);
                _changedFiles.Add(e.FullPath);
            }
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            lock (_lock)
            {
                if (!_enabled) return;
                _logger.LogTrace("File rename event: {OldFile} to {NewFile}", e.OldFullPath, e.FullPath);

                var oldPath = e.OldFullPath;

                if (_knownFilePaths.Any(p => p.StartsWith(oldPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogInformation("Directory with known files renamed from {OldPath}. Forcing reload.", oldPath);
                    _reloadIsNecessary = true;
                    return;
                }

                if (!IsPathIgnored(_watcher.Path, e.OldFullPath, pathSeparators))
                {
                    _changedFiles.Add(e.OldFullPath);
                }
                if (!IsPathIgnored(_watcher.Path, e.FullPath, pathSeparators))
                {
                    _changedFiles.Add(e.FullPath);
                }
            }
        }

        private void OnError(object sender, ErrorEventArgs e)
        { 
            lock (_lock)
            {
                if (!_enabled) return;
                _logger.LogError(e.GetException(), "File system watcher error.");
                _reloadIsNecessary = true;
            }
        }

        public void Disable()
        {
            _watcher.EnableRaisingEvents = false;
            _enabled = false;
            _reloadIsNecessary = true;
            lock (_lock)
            {
                // Wait for any in-progress events to complete
            }
        }

        public void Dispose()
        {
            _watcher.Dispose();
        }
    }
}