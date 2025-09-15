using System.Runtime.InteropServices;
using System.Xml.Linq;
using ModelContextProtocol;
using SharpTools.Tools.Mcp.Tools;
namespace SharpTools.Tools.Services;

public sealed class SolutionManager : ISolutionManager {
    private readonly ILogger<SolutionManager> _logger;
    private readonly IFuzzyFqnLookupService _fuzzyFqnLookupService;
    private MSBuildWorkspace? _workspace;
    private Solution? _currentSolution;
    private MetadataLoadContext? _metadataLoadContext;
    private PathAssemblyResolver? _pathAssemblyResolver;
    private HashSet<string> _assemblyPathsForReflection = new();
    private readonly ConcurrentDictionary<ProjectId, Compilation> _compilationCache = new();
    private readonly ConcurrentDictionary<DocumentId, SemanticModel> _semanticModelCache = new();
    private readonly ConcurrentDictionary<string, Type> _allLoadedReflectionTypesCache = new();
    [MemberNotNullWhen(true, nameof(_workspace), nameof(_currentSolution))]
    public bool IsSolutionLoaded => _workspace != null && _currentSolution != null;
    public MSBuildWorkspace? CurrentWorkspace => _workspace;
    public Solution? CurrentSolution => _currentSolution;
    private readonly string? _buildConfiguration;

    public SolutionManager(ILogger<SolutionManager> logger, IFuzzyFqnLookupService fuzzyFqnLookupService, string? buildConfiguration = null) {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fuzzyFqnLookupService = fuzzyFqnLookupService ?? throw new ArgumentNullException(nameof(fuzzyFqnLookupService));
        _buildConfiguration = buildConfiguration;
    }
    public async Task LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken) {
        if (!File.Exists(solutionPath)) {
            _logger.LogError("Solution file not found: {SolutionPath}", solutionPath);
            throw new FileNotFoundException("Solution file not found.", solutionPath);
        }
        UnloadSolution(); // Clears previous state including _allLoadedReflectionTypesCache
        try {
            _logger.LogInformation("Creating MSBuildWorkspace...");
            var properties = new Dictionary<string, string> {
                { "DesignTimeBuild", "true" }
            };

            if (!string.IsNullOrEmpty(_buildConfiguration)) {
                properties.Add("Configuration", _buildConfiguration);
            }
            
            _workspace = MSBuildWorkspace.Create(properties, MefHostServices.DefaultHost);
            _workspace.WorkspaceFailed += OnWorkspaceFailed;
            _logger.LogInformation("Loading solution: {SolutionPath}", solutionPath);
            _currentSolution = await _workspace.OpenSolutionAsync(solutionPath, new ProgressReporter(_logger), cancellationToken);
            _logger.LogInformation("Solution loaded successfully with {ProjectCount} projects.", _currentSolution.Projects.Count());
            InitializeMetadataContextAndReflectionCache(_currentSolution, cancellationToken);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to load solution: {SolutionPath}", solutionPath);
            UnloadSolution();
            throw;
        }
    }
    private void InitializeMetadataContextAndReflectionCache(Solution solution, CancellationToken cancellationToken = default) {
        // Check cancellation at entry point
        cancellationToken.ThrowIfCancellationRequested();

        _assemblyPathsForReflection.Clear();

        // Add runtime assemblies
        string[] runtimeAssemblies = Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll");
        foreach (var assemblyPath in runtimeAssemblies) {
            // Check cancellation periodically
            cancellationToken.ThrowIfCancellationRequested();
            if (!_assemblyPathsForReflection.Contains(assemblyPath)) {
                _assemblyPathsForReflection.Add(assemblyPath);
            }
        }

        // Load NuGet package assemblies from global cache instead of output directories
        var nugetAssemblies = GetNuGetAssemblyPaths(solution, cancellationToken);
        foreach (var assemblyPath in nugetAssemblies) {
            cancellationToken.ThrowIfCancellationRequested();
            _assemblyPathsForReflection.Add(assemblyPath);
        }

        // Check cancellation before cleanup operations
        cancellationToken.ThrowIfCancellationRequested();

        // Remove mscorlib.dll from the list of assemblies as it is loaded by default
        _assemblyPathsForReflection.RemoveWhere(p => p.EndsWith("mscorlib.dll", StringComparison.OrdinalIgnoreCase));

        // Remove duplicate files regardless of path
        _assemblyPathsForReflection = _assemblyPathsForReflection
            .GroupBy(Path.GetFileName)
            .Select(g => g.First())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Check cancellation before creating context
        cancellationToken.ThrowIfCancellationRequested();

        _pathAssemblyResolver = new PathAssemblyResolver(_assemblyPathsForReflection);
        _metadataLoadContext = new MetadataLoadContext(_pathAssemblyResolver);
        _logger.LogInformation("MetadataLoadContext initialized with {PathCount} distinct search paths.", _assemblyPathsForReflection.Count);

        // Check cancellation before populating cache
        cancellationToken.ThrowIfCancellationRequested();

        PopulateReflectionCache(_assemblyPathsForReflection, cancellationToken);
    }
    private void PopulateReflectionCache(IEnumerable<string> assemblyPathsToInspect, CancellationToken cancellationToken = default) {
        // Check cancellation at entry point
        cancellationToken.ThrowIfCancellationRequested();

        if (_metadataLoadContext == null) {
            _logger.LogWarning("Cannot populate reflection cache: MetadataLoadContext not initialized.");
            return;
        }
        // _allLoadedReflectionTypesCache is cleared in UnloadSolution
        _logger.LogInformation("Starting population of reflection type cache...");
        int typesCachedCount = 0;

        // Convert to list to avoid multiple enumeration and enable progress tracking
        var pathsList = assemblyPathsToInspect.ToList();
        int totalPaths = pathsList.Count;
        int processedPaths = 0;
        const int progressCheckInterval = 10; // Report progress and check cancellation every 10 assemblies

        foreach (var assemblyPath in pathsList) {
            // Check cancellation and log progress periodically
            if (++processedPaths % progressCheckInterval == 0) {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogTrace("Reflection cache population progress: {Progress}% ({Current}/{Total})",
                (int)((float)processedPaths / totalPaths * 100), processedPaths, totalPaths);
            }

            LoadTypesFromAssembly(assemblyPath, ref typesCachedCount, cancellationToken);
        }

        _logger.LogInformation("Reflection type cache population complete. Cached {Count} types from {AssemblyCount} unique assembly paths processed.", typesCachedCount, pathsList.Count);
    }
    private void LoadTypesFromAssembly(string assemblyPath, ref int typesCachedCount, CancellationToken cancellationToken = default) {
        // Check cancellation at entry point
        cancellationToken.ThrowIfCancellationRequested();

        if (_metadataLoadContext == null || string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath)) {
            if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath)) {
                _logger.LogTrace("Assembly path is invalid or file does not exist, skipping for reflection cache: {Path}", assemblyPath);
            }
            return;
        }
        try {
            var assembly = _metadataLoadContext.LoadFromAssemblyPath(assemblyPath);

            // For large assemblies, check cancellation periodically during type collection
            // We can't check during GetTypes() directly since it's atomic, but we can after
            cancellationToken.ThrowIfCancellationRequested();

            var types = assembly.GetTypes();
            int processedTypes = 0;
            const int typeCheckInterval = 50; // Check cancellation every 50 types

            foreach (var type in types) {
                // Check cancellation periodically when processing many types
                if (++processedTypes % typeCheckInterval == 0) {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (type?.FullName != null && !_allLoadedReflectionTypesCache.ContainsKey(type.FullName)) {
                    _allLoadedReflectionTypesCache.TryAdd(type.FullName, type);
                    typesCachedCount++;
                }
            }
        } catch (ReflectionTypeLoadException rtlex) {
            _logger.LogWarning("Could not load all types from assembly {Path} for reflection cache. LoaderExceptions: {Count}", assemblyPath, rtlex.LoaderExceptions.Length);
            foreach (var loaderEx in rtlex.LoaderExceptions.Where(e => e != null)) {
                _logger.LogTrace("LoaderException: {Message}", loaderEx!.Message);
            }

            // For partial load errors, still process the types that did load
            int processedTypes = 0;
            const int typeCheckInterval = 20; // Check cancellation more frequently when dealing with problematic assemblies

            foreach (var type in rtlex.Types.Where(t => t != null)) {
                // Check cancellation periodically
                if (++processedTypes % typeCheckInterval == 0) {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (type!.FullName != null && !_allLoadedReflectionTypesCache.ContainsKey(type.FullName)) {
                    _allLoadedReflectionTypesCache.TryAdd(type.FullName, type);
                    typesCachedCount++;
                }
            }
        } catch (FileNotFoundException) { // Should be rare due to File.Exists check, but MLC might have its own resolution logic
            _logger.LogTrace("Assembly file not found by MetadataLoadContext: {Path}", assemblyPath);
        } catch (BadImageFormatException) {
            _logger.LogTrace("Bad image format for assembly file: {Path}", assemblyPath);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Error loading types from assembly {Path} for reflection cache.", assemblyPath);
        }
    }
    public void UnloadSolution() {
        _logger.LogInformation("Unloading current solution and workspace.");
        _compilationCache.Clear();
        _semanticModelCache.Clear();
        _allLoadedReflectionTypesCache.Clear();
        if (_workspace != null) {
            _workspace.WorkspaceFailed -= OnWorkspaceFailed;
            _workspace.CloseSolution();
            _workspace.Dispose();
            _workspace = null;
        }
        _metadataLoadContext?.Dispose();
        _metadataLoadContext = null;
        _pathAssemblyResolver = null; // PathAssemblyResolver doesn't implement IDisposable
        _assemblyPathsForReflection.Clear();
    }
    public void RefreshCurrentSolution() {
        if (_workspace == null) {
            _logger.LogWarning("Cannot refresh solution: Workspace is null.");
            return;
        }
        if (_workspace.CurrentSolution == null) {
            _logger.LogWarning("Cannot refresh solution: No solution loaded.");
            return;
        }
        _currentSolution = _workspace.CurrentSolution;
        _compilationCache.Clear();
        _semanticModelCache.Clear();
        _logger.LogDebug("Current solution state has been refreshed from workspace.");
    }
    public async Task ReloadSolutionFromDiskAsync(CancellationToken cancellationToken) {
        if (_workspace == null) {
            _logger.LogWarning("Cannot reload solution: Workspace is null.");
            return;
        }
        if (_workspace.CurrentSolution == null) {
            _logger.LogWarning("Cannot reload solution: No solution loaded.");
            return;
        }
        await LoadSolutionAsync(_workspace.CurrentSolution.FilePath!, cancellationToken);
        _logger.LogDebug("Current solution state has been refreshed from workspace.");
    }
    private void OnWorkspaceFailed(object? sender, WorkspaceDiagnosticEventArgs e) {
        var diagnostic = e.Diagnostic;
        var level = diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? LogLevel.Error : LogLevel.Warning;
        _logger.Log(level, "Workspace diagnostic ({Kind}): {Message}", diagnostic.Kind, diagnostic.Message);
    }
    public async Task<INamedTypeSymbol?> FindRoslynNamedTypeSymbolAsync(string fullyQualifiedTypeName, CancellationToken cancellationToken) {
        if (!IsSolutionLoaded) {
            _logger.LogWarning("Cannot find Roslyn symbol: No solution loaded.");
            return null;
        }
        // Check cancellation before starting lookup
        cancellationToken.ThrowIfCancellationRequested();
        // Use fuzzy FQN lookup service
        var matches = await _fuzzyFqnLookupService.FindMatchesAsync(fullyQualifiedTypeName, this, cancellationToken);
        var matchList = matches.Where(m => m.Symbol is INamedTypeSymbol).ToList();
        // Check cancellation after initial matching
        cancellationToken.ThrowIfCancellationRequested();
        if (matchList.Count == 1) {
            var match = matchList.First();
            _logger.LogDebug("Roslyn named type symbol found: {FullyQualifiedTypeName} (score: {Score}, reason: {Reason})",
            match.CanonicalFqn, match.Score, match.MatchReason);
            return (INamedTypeSymbol)match.Symbol;
        }
        if (matchList.Count > 1) {
            _logger.LogWarning("Multiple matches found for {FullyQualifiedTypeName}", fullyQualifiedTypeName);
            throw new McpException($"FQN was ambiguous, did you mean one of these?\n{string.Join("\n", matchList.Select(m => m.CanonicalFqn))}");
        }
        // Direct lookup as fallback
        foreach (var project in CurrentSolution.Projects) {
            // Check cancellation before each project
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await GetCompilationAsync(project.Id, cancellationToken);
            if (compilation == null) {
                continue;
            }
            var symbol = compilation.GetTypeByMetadataName(fullyQualifiedTypeName);
            if (symbol != null) {
                _logger.LogDebug("Roslyn named type symbol found via direct lookup: {FullyQualifiedTypeName} in project {ProjectName}",
                fullyQualifiedTypeName, project.Name);
                return symbol;
            }
        }
        // Check cancellation before nested type check
        cancellationToken.ThrowIfCancellationRequested();
        // Check for nested type with dot notation as last resort
        var lastDotIndex = fullyQualifiedTypeName.LastIndexOf('.');
        if (lastDotIndex > 0) {
            var parentTypeName = fullyQualifiedTypeName.Substring(0, lastDotIndex);
            var nestedTypeName = fullyQualifiedTypeName.Substring(lastDotIndex + 1);
            foreach (var project in CurrentSolution.Projects) {
                // Check cancellation before each project
                cancellationToken.ThrowIfCancellationRequested();
                var compilation = await GetCompilationAsync(project.Id, cancellationToken);
                if (compilation == null) {
                    continue;
                }
                var parentSymbol = compilation.GetTypeByMetadataName(parentTypeName);
                if (parentSymbol != null) {
                    // Check if there's a nested type with this name
                    var nestedType = parentSymbol.GetTypeMembers(nestedTypeName).FirstOrDefault();
                    if (nestedType != null) {
                        var correctName = $"{parentTypeName}+{nestedTypeName}";
                        _logger.LogWarning("Type not found: '{FullyQualifiedTypeName}'. This appears to be a nested type - use '{CorrectName}' instead (use + instead of . for nested types)",
                        fullyQualifiedTypeName, correctName);
                        throw new McpException(
                        $"Type not found: '{fullyQualifiedTypeName}'. This appears to be a nested type - use '{correctName}' instead (use + instead of . for nested types)");
                    }
                }
            }
        }
        _logger.LogDebug("Roslyn named type symbol not found: {FullyQualifiedTypeName}", fullyQualifiedTypeName);
        return null;
    }
    public async Task<ISymbol?> FindRoslynSymbolAsync(string fullyQualifiedName, CancellationToken cancellationToken) {
        if (!IsSolutionLoaded) {
            _logger.LogWarning("Cannot find Roslyn symbol: No solution loaded.");
            return null;
        }

        // Check cancellation before starting lookup
        cancellationToken.ThrowIfCancellationRequested();

        // Use fuzzy FQN lookup service
        var matches = await _fuzzyFqnLookupService.FindMatchesAsync(fullyQualifiedName, this, cancellationToken);
        var matchList = matches.ToList();

        // Check cancellation after initial matching
        cancellationToken.ThrowIfCancellationRequested();

        if (matchList.Count == 1) {
            var match = matchList.First();
            _logger.LogDebug("Roslyn symbol found: {FullyQualifiedName} (score: {Score}, reason: {Reason})",
            match.CanonicalFqn, match.Score, match.MatchReason);
            return match.Symbol;
        }

        if (matchList.Count > 1) {
            _logger.LogWarning("Multiple matches found for {FullyQualifiedName}", fullyQualifiedName);
            throw new McpException($"FQN was ambiguous, did you mean one of these?\n{string.Join("\n", matchList.Select(m => m.CanonicalFqn))}");
        }

        // Check cancellation before fallback lookup
        cancellationToken.ThrowIfCancellationRequested();

        // Fall back to type lookup
        var typeSymbol = await FindRoslynNamedTypeSymbolAsync(fullyQualifiedName, cancellationToken);
        if (typeSymbol != null) {
            return typeSymbol;
        }

        // Check cancellation before member lookup
        cancellationToken.ThrowIfCancellationRequested();

        // Check for member of a type as fallback
        var lastDotIndex = fullyQualifiedName.LastIndexOf('.');
        if (lastDotIndex > 0 && lastDotIndex < fullyQualifiedName.Length - 1) {
            var typeName = fullyQualifiedName.Substring(0, lastDotIndex);
            var memberName = fullyQualifiedName.Substring(lastDotIndex + 1);

            var parentTypeSymbol = await FindRoslynNamedTypeSymbolAsync(typeName, cancellationToken);
            if (parentTypeSymbol != null) {
                // Check cancellation before final lookup step
                cancellationToken.ThrowIfCancellationRequested();

                var members = parentTypeSymbol.GetMembers(memberName);
                if (members.Any()) {
                    // TODO: Handle overloads if necessary, for now, take the first.
                    var memberSymbol = members.First();
                    _logger.LogDebug("Roslyn member symbol found: {FullyQualifiedName}", fullyQualifiedName);
                    return memberSymbol;
                }
            }
        }

        _logger.LogDebug("Roslyn symbol not found: {FullyQualifiedName}", fullyQualifiedName);
        return null;
    }
    public Task<Type?> FindReflectionTypeAsync(string fullyQualifiedTypeName, CancellationToken cancellationToken) {
        // Check cancellation at the beginning of the method
        cancellationToken.ThrowIfCancellationRequested();

        if (_metadataLoadContext == null) {
            _logger.LogWarning("Cannot find reflection type: MetadataLoadContext not initialized.");
            return Task.FromResult<Type?>(null);
        }
        if (_allLoadedReflectionTypesCache.TryGetValue(fullyQualifiedTypeName, out var type)) {
            _logger.LogDebug("Reflection type found in cache: {Name}", fullyQualifiedTypeName);
            return Task.FromResult<Type?>(type);
        }
        _logger.LogDebug("Reflection type '{FullyQualifiedTypeName}' not found in cache. It might not exist in the loaded solution's dependencies or was not loadable.", fullyQualifiedTypeName);
        return Task.FromResult<Type?>(null);
    }
    public Task<IEnumerable<Type>> SearchReflectionTypesAsync(string regexPattern, CancellationToken cancellationToken) {
        // Check cancellation at the method entry point
        cancellationToken.ThrowIfCancellationRequested();

        if (_metadataLoadContext == null) {
            _logger.LogWarning("Cannot search reflection types: MetadataLoadContext not initialized.");
            return Task.FromResult(Enumerable.Empty<Type>());
        }
        if (!_allLoadedReflectionTypesCache.Any()) {
            _logger.LogInformation("Reflection type cache is empty. Search will yield no results.");
            return Task.FromResult(Enumerable.Empty<Type>());
        }

        // Check cancellation before regex compilation
        cancellationToken.ThrowIfCancellationRequested();

        var regex = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var matchedTypes = new List<Type>();

        // Consider batching in chunks to check cancellation more frequently on large type caches
        int processedCount = 0;
        const int batchSize = 100; // Check cancellation every 100 types

        foreach (var typeEntry in _allLoadedReflectionTypesCache) { // Iterate KeyValuePair to access FQN directly
            if (++processedCount % batchSize == 0) {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Key is type.FullName which should not be null for cached types
            if (regex.IsMatch(typeEntry.Key)) { // Search FQN
                matchedTypes.Add(typeEntry.Value);
            } else if (regex.IsMatch(typeEntry.Value.Name)) { // Search simple name
                matchedTypes.Add(typeEntry.Value);
            }
        }

        // Check cancellation before returning results
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug("Found {Count} reflection types matching pattern '{Pattern}'.", matchedTypes.Count, regexPattern);
        return Task.FromResult<IEnumerable<Type>>(matchedTypes.Distinct());
    }
    public IEnumerable<Project> GetProjects() {
        return CurrentSolution?.Projects ?? Enumerable.Empty<Project>();
    }
    public Project? GetProjectByName(string projectName) {
        if (!IsSolutionLoaded) {
            _logger.LogWarning("Cannot get project by name: No solution loaded.");
            return null;
        }
        var project = CurrentSolution?.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
        if (project == null) {
            _logger.LogWarning("Project not found: {ProjectName}", projectName);
        }
        return project;
    }
    public async Task<SemanticModel?> GetSemanticModelAsync(DocumentId documentId, CancellationToken cancellationToken) {
        // Check cancellation at entry point
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSolutionLoaded) {
            _logger.LogWarning("Cannot get semantic model: No solution loaded.");
            return null;
        }

        // Fast path: check cache first
        if (_semanticModelCache.TryGetValue(documentId, out var cachedModel)) {
            _logger.LogTrace("Returning cached semantic model for document ID: {DocumentId}", documentId);
            return cachedModel;
        }

        // Check cancellation before document lookup
        cancellationToken.ThrowIfCancellationRequested();

        var document = CurrentSolution.GetDocument(documentId);
        if (document == null) {
            _logger.LogWarning("Document not found for ID: {DocumentId}", documentId);
            return null;
        }

        _logger.LogTrace("Requesting semantic model for document: {DocumentFilePath}", document.FilePath);

        // Check cancellation before expensive GetSemanticModelAsync call
        cancellationToken.ThrowIfCancellationRequested();

        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model != null) {
            _semanticModelCache.TryAdd(documentId, model);
        } else {
            _logger.LogWarning("Failed to get semantic model for document: {DocumentFilePath}", document.FilePath);
        }
        return model;
    }
    public async Task<Compilation?> GetCompilationAsync(ProjectId projectId, CancellationToken cancellationToken) {
        // Check cancellation at entry point
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSolutionLoaded) {
            _logger.LogWarning("Cannot get compilation: No solution loaded.");
            return null;
        }

        // Fast path: check cache first
        if (_compilationCache.TryGetValue(projectId, out var cachedCompilation)) {
            _logger.LogTrace("Returning cached compilation for project ID: {ProjectId}", projectId);
            return cachedCompilation;
        }

        // Check cancellation before project lookup
        cancellationToken.ThrowIfCancellationRequested();

        var project = CurrentSolution.GetProject(projectId);
        if (project == null) {
            _logger.LogWarning("Project not found for ID: {ProjectId}", projectId);
            return null;
        }

        _logger.LogTrace("Requesting compilation for project: {ProjectName}", project.Name);

        // Check cancellation before expensive GetCompilationAsync call
        cancellationToken.ThrowIfCancellationRequested();

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation != null) {
            _compilationCache.TryAdd(projectId, compilation);
        } else {
            _logger.LogWarning("Failed to get compilation for project: {ProjectName}", project.Name);
        }
        return compilation;
    }
    public void Dispose() {
        UnloadSolution();
        GC.SuppressFinalize(this);
    }
    private class ProgressReporter : IProgress<ProjectLoadProgress> {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        public ProgressReporter(Microsoft.Extensions.Logging.ILogger logger) {
            _logger = logger;
        }
        public void Report(ProjectLoadProgress loadProgress) {
            var projectDisplay = Path.GetFileName(loadProgress.FilePath);
            _logger.LogTrace("Project Load Progress: {ProjectDisplayName}, Operation: {Operation}, Time: {TimeElapsed}",
            projectDisplay, loadProgress.Operation, loadProgress.ElapsedTime);
        }
    }
    private HashSet<string> GetNuGetAssemblyPaths(Solution solution, CancellationToken cancellationToken = default) {
        var nugetAssemblyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nugetCacheDir = GetNuGetGlobalPackagesFolder();

        if (string.IsNullOrEmpty(nugetCacheDir) || !Directory.Exists(nugetCacheDir)) {
            _logger.LogWarning("NuGet global packages folder not found or inaccessible: {NuGetCacheDir}", nugetCacheDir);
            return nugetAssemblyPaths;
        }

        foreach (var project in solution.Projects) {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(project.FilePath)) {
                continue;
            }

            var packageReferences = LegacyNuGetPackageReader.GetAllPackageReferences(project.FilePath);
            var projectTargetFramework = SolutionTools.ExtractTargetFrameworkFromProjectFile(project.FilePath);

            foreach (var package in packageReferences) {
                cancellationToken.ThrowIfCancellationRequested();

                var packageDir = Path.Combine(nugetCacheDir, package.PackageId.ToLowerInvariant(), package.Version);
                if (!Directory.Exists(packageDir)) {
                    _logger.LogTrace("Package directory not found: {PackageDir}", packageDir);
                    continue;
                }

                var libDir = Path.Combine(packageDir, "lib");
                if (!Directory.Exists(libDir)) {
                    _logger.LogTrace("No lib directory found for package {PackageId} {Version}", package.PackageId, package.Version);
                    continue;
                }

                // Find assemblies using the project's target framework
                var assemblyPaths = GetAssembliesForTargetFramework(libDir, package.TargetFramework ?? projectTargetFramework, package.PackageId, package.Version);
                foreach (var assemblyPath in assemblyPaths) {
                    nugetAssemblyPaths.Add(assemblyPath);
                }
            }
        }

        _logger.LogInformation("Found {AssemblyCount} NuGet assemblies from global packages cache", nugetAssemblyPaths.Count);
        return nugetAssemblyPaths;
    }
    private static string GetNuGetGlobalPackagesFolder() {
        // Check environment variable first
        var globalPackagesPath = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(globalPackagesPath)) {
            return globalPackagesPath;
        }

        // Default location based on OS
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".nuget", "packages");
    }
    private List<string> GetAssembliesForTargetFramework(string libDir, string targetFramework, string packageId, string version) {
        var assemblies = new List<string>();

        if (!Directory.Exists(libDir)) {
            return assemblies;
        }

        // First try exact target framework match
        var exactFrameworkDir = Path.Combine(libDir, targetFramework);
        if (Directory.Exists(exactFrameworkDir)) {
            var exactAssemblies = Directory.GetFiles(exactFrameworkDir, "*.dll", SearchOption.TopDirectoryOnly);
            assemblies.AddRange(exactAssemblies);
            _logger.LogTrace("Found {AssemblyCount} assemblies in exact framework match {Framework} for {PackageId} {Version}",
                exactAssemblies.Length, targetFramework, packageId, version);
            return assemblies;
        }

        // Try compatible frameworks in order of preference
        var compatibleFrameworks = GetCompatibleFrameworks(targetFramework);

        foreach (var framework in compatibleFrameworks) {
            var frameworkDir = Path.Combine(libDir, framework);
            if (Directory.Exists(frameworkDir)) {
                var frameworkAssemblies = Directory.GetFiles(frameworkDir, "*.dll", SearchOption.TopDirectoryOnly);
                assemblies.AddRange(frameworkAssemblies);
                _logger.LogTrace("Found {AssemblyCount} assemblies in compatible framework {Framework} for {PackageId} {Version}",
                    frameworkAssemblies.Length, framework, packageId, version);
                return assemblies; // Take the first compatible framework found
            }
        }

        // Fallback: check if there are any DLLs directly in lib directory
        if (assemblies.Count == 0) {
            var libAssemblies = Directory.GetFiles(libDir, "*.dll", SearchOption.TopDirectoryOnly);
            assemblies.AddRange(libAssemblies);
            if (libAssemblies.Length > 0) {
                _logger.LogTrace("Found {AssemblyCount} assemblies in lib root for {PackageId} {Version}",
                    libAssemblies.Length, packageId, version);
            }
        }

        return assemblies;
    }
    private static string[] GetCompatibleFrameworks(string targetFramework) {
        // Return frameworks in order of compatibility preference
        return targetFramework.ToLowerInvariant() switch {
            "net10.0" => new[] { "net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "net9.0" => new[] { "net9.0", "net8.0", "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "net8.0" => new[] { "net8.0", "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "net7.0" => new[] { "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "net6.0" => new[] { "net6.0", "net5.0", "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "net5.0" => new[] { "net5.0", "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "netcoreapp3.1" => new[] { "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "netcoreapp3.0" => new[] { "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "netcoreapp2.1" => new[] { "netcoreapp2.1", "netstandard2.0", "netstandard1.6" },
            "netstandard2.1" => new[] { "netstandard2.1", "netstandard2.0", "netstandard1.6" },
            "netstandard2.0" => new[] { "netstandard2.0", "netstandard1.6" },
            _ => new[] { "net8.0", "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1", "netstandard2.1", "netstandard2.0", "netstandard1.6" }
        };
    }
}