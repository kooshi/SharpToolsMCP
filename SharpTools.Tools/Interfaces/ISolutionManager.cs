namespace SharpTools.Tools.Interfaces;

public interface ISolutionManager : IDisposable {
    [MemberNotNullWhen(true, nameof(CurrentWorkspace), nameof(CurrentSolution))]
    bool IsSolutionLoaded { get; }
    MSBuildWorkspace? CurrentWorkspace { get; }
    Solution? CurrentSolution { get; }
    

    Task LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken);
    void UnloadSolution();

    Task<ISymbol?> FindRoslynSymbolAsync(string fullyQualifiedName, CancellationToken cancellationToken);
    Task<INamedTypeSymbol?> FindRoslynNamedTypeSymbolAsync(string fullyQualifiedTypeName, CancellationToken cancellationToken);
    Task<Type?> FindReflectionTypeAsync(string fullyQualifiedTypeName, CancellationToken cancellationToken);
    Task<IEnumerable<Type>> SearchReflectionTypesAsync(string regexPattern, CancellationToken cancellationToken);

    IEnumerable<Project> GetProjects();
    Project? GetProjectByName(string projectName); Task<SemanticModel?> GetSemanticModelAsync(DocumentId documentId, CancellationToken cancellationToken);
    Task<Compilation?> GetCompilationAsync(ProjectId projectId, CancellationToken cancellationToken);
    Task ReloadSolutionFromDiskAsync(CancellationToken cancellationToken);
    void RefreshCurrentSolution();
    /// <summary>Re-reads documents modified on disk outside SharpTools; reloads the solution when files were added/removed or a project file changed.</summary>
    Task<DiskSyncResult> SyncWithDiskAsync(CancellationToken cancellationToken);
    /// <summary>Writes the documents <paramref name="modifiedSolution"/> changed relative to the solution it was derived from (<paramref name="baseSolution"/>, else the first <see cref="CurrentSolution"/> read in the current operation) to the workspace and disk. Fails if any of them changed underneath in the meantime. Returns the affected file paths.</summary>
    Task<IReadOnlyList<string>> ApplyChangesAsync(Solution modifiedSolution, CancellationToken cancellationToken, Solution? baseSolution = null);
}