namespace SharpTools.Tools.Services;

/// <summary>
/// Represents information about a path's relationship to a solution
/// </summary>
public readonly record struct PathInfo
{
    /// <summary>
    /// The absolute file path
    /// </summary>
    public string FilePath { get; init; }

    /// <summary>
    /// Whether the path exists on disk
    /// </summary>
    public bool Exists { get; init; }

    /// <summary>
    /// Whether the path is within a solution directory
    /// </summary>
    public bool IsWithinSolutionDirectory { get; init; }

    /// <summary>
    /// Whether the path is referenced by a project in the solution
    /// (either directly or through referenced projects)
    /// </summary>
    public bool IsReferencedBySolution { get; init; }

    /// <summary>
    /// Whether the path is a source file that can be formatted
    /// </summary>
    public bool IsFormattable { get; init; }

    /// <summary>
    /// The project id that contains this path, if any
    /// </summary>
    public string? ProjectId { get; init; }

    /// <summary>
    /// The reason if the path is not writable
    /// </summary>
    public string? WriteRestrictionReason { get; init; }

    /// <summary>
    /// Whether the path is safe to read from based on its relationship to the solution
    /// </summary>
    public bool IsReadable => Exists && (IsWithinSolutionDirectory || IsReferencedBySolution);

    /// <summary>
    /// Whether the path is safe to write to based on its relationship to the solution
    /// </summary>
    public bool IsWritable => IsWithinSolutionDirectory && string.IsNullOrEmpty(WriteRestrictionReason);
}
