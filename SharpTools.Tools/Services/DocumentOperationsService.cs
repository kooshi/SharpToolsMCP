using System.Xml;

namespace SharpTools.Tools.Services;

public class DocumentOperationsService : IDocumentOperationsService {
    private readonly ISolutionManager _solutionManager;
    private readonly ICodeModificationService _modificationService;
    private readonly IGitService _gitService;
    private readonly ILogger<DocumentOperationsService> _logger;

    // Extensions for common code file types that can be formatted
    private static readonly HashSet<string> CodeFileExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".cs", ".csproj", ".sln", ".css", ".js", ".ts", ".jsx", ".tsx", ".html", ".cshtml", ".razor", ".yml", ".yaml",
        ".json", ".xml", ".config", ".md", ".fs", ".fsx", ".fsi", ".vb"
    };

    private static readonly HashSet<string> UnsafeDirectories = new(StringComparer.OrdinalIgnoreCase) {
        ".git", ".vs", "bin", "obj", "node_modules"
    };

    public DocumentOperationsService(
        ISolutionManager solutionManager,
        ICodeModificationService modificationService,
        IGitService gitService,
        ILogger<DocumentOperationsService> logger) {
        _solutionManager = solutionManager;
        _modificationService = modificationService;
        _gitService = gitService;
        _logger = logger;
    }

    public async Task<(string contents, int lines)> ReadFileAsync(string filePath, bool omitLeadingSpaces, CancellationToken cancellationToken) {
        if (!File.Exists(filePath)) {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        if (!IsPathReadable(filePath)) {
            throw new UnauthorizedAccessException($"Reading from this path is not allowed: {filePath}");
        }

        string content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

        if (omitLeadingSpaces) {

            for (int i = 0; i < lines.Length; i++) {
                lines[i] = TrimLeadingSpaces(lines[i]);
            }

            content = string.Join(Environment.NewLine, lines);
        }

        return (content, lines.Length);
    }
    public async Task<bool> WriteFileAsync(string filePath, string content, bool overwriteIfExists, CancellationToken cancellationToken, string commitMessage) {
        var pathInfo = GetPathInfo(filePath);

        if (!pathInfo.IsWritable) {
            _logger.LogWarning("Path is not writable: {FilePath}. Reason: {Reason}",
                filePath, pathInfo.WriteRestrictionReason);
            throw new UnauthorizedAccessException($"Writing to this path is not allowed: {filePath}. {pathInfo.WriteRestrictionReason}");
        }

        if (File.Exists(filePath) && !overwriteIfExists) {
            _logger.LogWarning("File already exists and overwrite not allowed: {FilePath}", filePath);
            return false;
        }

        // Ensure directory exists
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) {
            Directory.CreateDirectory(directory);
        }

        // Write the content to the file
        await File.WriteAllTextAsync(filePath, content, cancellationToken);
        _logger.LogInformation("File {Operation} at {FilePath}",
            File.Exists(filePath) ? "overwritten" : "created", filePath);


        // Find the most appropriate project for this file path
        var bestProject = FindMostAppropriateProject(filePath);
        if (!pathInfo.IsFormattable || bestProject is null || string.IsNullOrWhiteSpace(bestProject.FilePath)) {
            _logger.LogWarning("Added non-code file: {FilePath}", filePath);
            if (string.IsNullOrEmpty(commitMessage)) {
                return true; // No commit message provided, don't commit, just return
            }
            //just commit the file
            await ProcessGitOperationsAsync([filePath], cancellationToken, commitMessage);
            return true;
        }

        Project? legacyProject = null;
        bool isSdkStyleProject = await IsSDKStyleProjectAsync(bestProject.FilePath, cancellationToken);
        if (isSdkStyleProject) {
            _logger.LogInformation("File added to SDK-style project: {ProjectPath}. Reloading Solution to pick up changes.", bestProject.FilePath);
            await _solutionManager.ReloadSolutionFromDiskAsync(cancellationToken);
        } else {
            legacyProject = await TryAddFileToLegacyProjectAsync(filePath, bestProject, cancellationToken);
        }
        var newSolution = legacyProject?.Solution ?? _solutionManager.CurrentSolution;
        var documentId = newSolution?.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
        if (documentId is null) {
            _logger.LogWarning("Mystery file was not added to any project: {FilePath}", filePath);
            return false;
        }
        var document = newSolution?.GetDocument(documentId);
        if (document is null) {
            _logger.LogWarning("Document not found in solution: {FilePath}", filePath);
            return false;
        }
        // If it's a code file, try to format it, which will also commit it
        if (await TryFormatAndCommitFileAsync(document, cancellationToken, commitMessage)) {
            _logger.LogInformation("File formatted and committed: {FilePath}", filePath);
            return true;
        } else {
            _logger.LogWarning("Failed to format file: {FilePath}", filePath);
        }
        return true;
    }

    private async Task<Project?> TryAddFileToLegacyProjectAsync(string filePath, Project project, CancellationToken cancellationToken) {
        if (!_solutionManager.IsSolutionLoaded || !File.Exists(filePath)) {
            return null;
        }

        try {
            // Get the document ID if the file is already in the solution
            var documentId = _solutionManager.CurrentSolution!.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();

            // If the document is already in the solution, no need to add it again
            if (documentId != null) {
                _logger.LogInformation("File is already part of project: {FilePath}", filePath);
                return null;
            }

            // The file exists on disk but is not part of the project yet - add it to the solution in memory
            var fileName = Path.GetFileName(filePath);

            // Determine appropriate folder path relative to the project
            var projectDir = Path.GetDirectoryName(project.FilePath);
            var relativePath = string.Empty;
            var folders = Array.Empty<string>();

            if (!string.IsNullOrEmpty(projectDir)) {
                relativePath = Path.GetRelativePath(projectDir, filePath);
                var folderPath = Path.GetDirectoryName(relativePath);

                if (!string.IsNullOrEmpty(folderPath) && folderPath != ".") {
                    folders = folderPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            }

            _logger.LogInformation("Adding file to {ProjectName}: {FilePath}", project.Name, filePath);

            // Create SourceText from file content
            var fileContent = await File.ReadAllTextAsync(filePath, cancellationToken);
            var sourceText = SourceText.From(fileContent);

            // Add the document to the project in memory
            return project.AddDocument(fileName, sourceText, folders, filePath).Project;
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to add file {FilePath} to project", filePath);
            return null;
        }
    }

    private async Task<bool> IsSDKStyleProjectAsync(string projectFilePath, CancellationToken cancellationToken) {
        try {
            var content = await File.ReadAllTextAsync(projectFilePath, cancellationToken);

            // Use XmlDocument for proper parsing
            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(content);

            var projectNode = xmlDoc.DocumentElement;

            // Primary check - Look for Sdk attribute on Project element
            if (projectNode?.Attributes?["Sdk"] != null) {
                _logger.LogDebug("Project {ProjectPath} is SDK-style (has Sdk attribute)", projectFilePath);
                return true;
            }

            // Secondary check - Look for TargetFramework instead of TargetFrameworkVersion
            var targetFrameworkNode = xmlDoc.SelectSingleNode("//TargetFramework");
            if (targetFrameworkNode != null) {
                _logger.LogDebug("Project {ProjectPath} is SDK-style (uses TargetFramework)", projectFilePath);
                return true;
            }

            _logger.LogDebug("Project {ProjectPath} is classic-style (no SDK indicators found)", projectFilePath);
            return false;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Error determining project style for {ProjectPath}, assuming classic format", projectFilePath);
            return false;
        }
    }

    private Microsoft.CodeAnalysis.Project? FindMostAppropriateProject(string filePath) {
        if (!_solutionManager.IsSolutionLoaded) {
            return null;
        }

        var projects = _solutionManager.GetProjects().ToList();
        if (!projects.Any()) {
            return null;
        }

        // Find projects where the file path is under the project directory
        var projectsWithPath = new List<(Microsoft.CodeAnalysis.Project Project, int DirectoryLevel)>();

        foreach (var project in projects) {
            if (string.IsNullOrEmpty(project.FilePath)) {
                continue;
            }

            var projectDir = Path.GetDirectoryName(project.FilePath);
            if (string.IsNullOrEmpty(projectDir)) {
                continue;
            }

            if (filePath.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase)) {
                // Calculate how many directories deep this file is from the project root
                var relativePath = filePath.Substring(projectDir.Length).TrimStart(Path.DirectorySeparatorChar);
                var directoryLevel = relativePath.Count(c => c == Path.DirectorySeparatorChar);

                projectsWithPath.Add((project, directoryLevel));
            }
        }

        // Return the project where the file is closest to the root
        // (smallest directory level means closer to project root)
        return projectsWithPath.OrderBy(p => p.DirectoryLevel).FirstOrDefault().Project;
    }

    public bool FileExists(string filePath) {
        return File.Exists(filePath);
    }

    public bool IsPathReadable(string filePath) {
        var pathInfo = GetPathInfo(filePath);
        return pathInfo.IsReadable;
    }

    public bool IsPathWritable(string filePath) {
        var pathInfo = GetPathInfo(filePath);
        return pathInfo.IsWritable;
    }
    public bool IsCodeFile(string filePath) {
        if (string.IsNullOrEmpty(filePath)) {
            return false;
        }

        // First check if file exists but is not part of the solution
        if (File.Exists(filePath) && !IsReferencedBySolution(filePath)) {
            return false;
        }

        // Check by extension
        var extension = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(extension) && CodeFileExtensions.Contains(extension);
    }
    public PathInfo GetPathInfo(string filePath) {
        if (string.IsNullOrEmpty(filePath)) {
            return new PathInfo {
                FilePath = filePath,
                Exists = false,
                IsWithinSolutionDirectory = false,
                IsReferencedBySolution = false,
                IsFormattable = false,
                WriteRestrictionReason = "Path is empty or null"
            };
        }

        bool exists = File.Exists(filePath);
        bool isWithinSolution = IsPathWithinSolutionDirectory(filePath);
        bool isReferenced = IsReferencedBySolution(filePath);
        bool isFormattable = IsCodeFile(filePath);
        string? projectId = FindMostAppropriateProject(filePath)?.Id.Id.ToString();

        string? writeRestrictionReason = null;

        // Check for unsafe directories
        if (ContainsUnsafeDirectory(filePath)) {
            writeRestrictionReason = "Path contains a protected directory (bin, obj, .git, etc.)";
        }

        // Check if file is outside solution
        if (!isWithinSolution) {
            writeRestrictionReason = "Path is outside the solution directory";
        }

        // Check if directory is read-only
        try {
            var directoryPath = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directoryPath) && Directory.Exists(directoryPath)) {
                var dirInfo = new DirectoryInfo(directoryPath);
                if ((dirInfo.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly) {
                    writeRestrictionReason = "Directory is read-only";
                }
            }
        } catch {
            writeRestrictionReason = "Cannot determine directory permissions";
        }

        return new PathInfo {
            FilePath = filePath,
            Exists = exists,
            IsWithinSolutionDirectory = isWithinSolution,
            IsReferencedBySolution = isReferenced,
            IsFormattable = isFormattable,
            ProjectId = projectId,
            WriteRestrictionReason = writeRestrictionReason
        };
    }

    private bool IsPathWithinSolutionDirectory(string filePath) {
        if (!_solutionManager.IsSolutionLoaded) {
            return false;
        }

        string? solutionDirectory = Path.GetDirectoryName(_solutionManager.CurrentSolution?.FilePath);

        if (string.IsNullOrEmpty(solutionDirectory)) {
            return false;
        }

        return filePath.StartsWith(solutionDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsReferencedBySolution(string filePath) {
        if (!_solutionManager.IsSolutionLoaded || !File.Exists(filePath)) {
            return false;
        }

        // Check if the file is directly referenced by a document in the solution
        if (_solutionManager.CurrentSolution!.GetDocumentIdsWithFilePath(filePath).Any()) {
            return true;
        }

        // TODO: Implement proper reference checking for assemblies, resources, etc.
        // This would require deeper MSBuild integration

        return false;
    }

    private bool ContainsUnsafeDirectory(string filePath) {
        // Check if the path contains any unsafe directory segments
        var normalizedPath = filePath.Replace('\\', '/');
        var pathSegments = normalizedPath.Split('/');

        return pathSegments.Any(segment => UnsafeDirectories.Contains(segment));
    }
    private async Task<bool> TryFormatAndCommitFileAsync(Document document, CancellationToken cancellationToken, string commitMessage) {
        try {
            var formattedDocument = await _modificationService.FormatDocumentAsync(document, cancellationToken);
            // Apply the formatting changes with the commit message
            var newSolution = formattedDocument.Project.Solution;
            await _modificationService.ApplyChangesAsync(newSolution, cancellationToken, commitMessage);

            _logger.LogInformation("Document {FilePath} formatted successfully", document.FilePath);
            return true;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to format file {FilePath}", document.FilePath);
            return false;
        }
    }

    private static string TrimLeadingSpaces(string line) {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) {
            i++;
        }

        return i > 0 ? line.Substring(i) : line;
    }
    public async Task ProcessGitOperationsAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken, string commitMessage) {
        var filesList = filePaths.Where(f => !string.IsNullOrEmpty(f) && File.Exists(f)).ToList();
        if (!filesList.Any()) {
            return;
        }

        try {
            // Get solution path
            var solutionPath = _solutionManager.CurrentSolution?.FilePath;
            if (string.IsNullOrEmpty(solutionPath)) {
                _logger.LogDebug("Solution path is not available, skipping Git operations");
                return;
            }

            // Check if solution is in a git repo
            if (!await _gitService.IsRepositoryAsync(solutionPath, cancellationToken)) {
                _logger.LogDebug("Solution is not in a Git repository, skipping Git operations");
                return;
            }

            _logger.LogDebug("Solution is in a Git repository, processing Git operations for {Count} files", filesList.Count);

            // Check if already on sharptools branch
            if (!await _gitService.IsOnSharpToolsBranchAsync(solutionPath, cancellationToken)) {
                _logger.LogInformation("Not on a SharpTools branch, creating one");
                await _gitService.EnsureSharpToolsBranchAsync(solutionPath, cancellationToken);
            }

            // Commit changes with the provided commit message
            await _gitService.CommitChangesAsync(solutionPath, filesList, commitMessage, cancellationToken);
            _logger.LogInformation("Git operations completed successfully for {Count} files with commit message: {CommitMessage}", filesList.Count, commitMessage);
        } catch (Exception ex) {
            // Log but don't fail the operation if Git operations fail
            _logger.LogWarning(ex, "Git operations failed for {Count} files but file operations were still applied", filesList.Count);
        }
    }
}