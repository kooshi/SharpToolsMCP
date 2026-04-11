namespace SharpTools.Tools.Interfaces;

/// <summary>
/// Represents a result from a fuzzy FQN lookup
/// </summary>
/// <param name="CanonicalFqn">The canonical fully qualified name</param>
/// <param name="Symbol">The matched symbol</param>
/// <param name="Score">The match score (higher is better, 1.0 is perfect)</param>
/// <param name="MatchReason">Description of why this was considered a match</param>
public record FuzzyMatchResult(
    string CanonicalFqn,
    ISymbol Symbol,
    double Score,
    string MatchReason);
