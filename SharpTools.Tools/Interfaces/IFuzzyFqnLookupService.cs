namespace SharpTools.Tools.Interfaces;

/// <summary>
/// Service for performing fuzzy lookups of fully qualified names in the solution
/// </summary>
public interface IFuzzyFqnLookupService
{
    /// <summary>
    /// Finds symbols matching the provided fuzzy FQN input
    /// </summary>
    /// <param name="fuzzyFqnInput">The fuzzy fully qualified name to search for</param>
    /// <returns>A collection of match results ordered by relevance</returns>
    Task<IEnumerable<FuzzyMatchResult>> FindMatchesAsync(
        string fuzzyFqnInput,
        ISolutionManager solutionManager,
        CancellationToken cancellationToken);
}
