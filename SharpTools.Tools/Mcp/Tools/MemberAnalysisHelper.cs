namespace SharpTools.Tools.Mcp.Tools;

public static class MemberAnalysisHelper
{
    /// <summary>
    /// Analyzes a newly added member for complexity and similarity.
    /// </summary>
    /// <returns>A formatted string with analysis results.</returns>
    public static async Task<string> AnalyzeAddedMemberAsync(
        ISymbol addedSymbol,
        IComplexityAnalysisService complexityAnalysisService,
        ISemanticSimilarityService semanticSimilarityService,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (addedSymbol == null)
        {
            logger.LogWarning("Cannot analyze null symbol");
            return string.Empty;
        }

        List<string> results = new();

        // Get complexity recommendations
        string complexityResults = await AnalyzeComplexityAsync(addedSymbol, complexityAnalysisService, logger, cancellationToken);
        if (string.IsNullOrEmpty(complexityResults) == false)
        {
            results.Add(complexityResults);
        }

        // Check for similar members
        string similarityResults = await AnalyzeSimilarityAsync(addedSymbol, semanticSimilarityService, logger, cancellationToken);
        if (string.IsNullOrEmpty(similarityResults) == false)
        {
            results.Add(similarityResults);
        }

        if (results.Count == 0)
        {
            return string.Empty;
        }

        return $"\n<analysisResults>\n{string.Join("\n\n", results)}\n</analysisResults>";
    }

    private static async Task<string> AnalyzeComplexityAsync(
        ISymbol symbol,
        IComplexityAnalysisService complexityAnalysisService,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        List<string> recommendations = new();
        Dictionary<string, object> metrics = new();

        try
        {
            if (symbol is IMethodSymbol methodSymbol)
            {
                await complexityAnalysisService.AnalyzeMethodAsync(methodSymbol, metrics, recommendations, cancellationToken);
            }
            else if (symbol is INamedTypeSymbol typeSymbol)
            {
                await complexityAnalysisService.AnalyzeTypeAsync(typeSymbol, metrics, recommendations, false, cancellationToken);
            }
            else
            {
                // No complexity analysis for other symbol types
                return string.Empty;
            }

            if (recommendations.Count == 0)
            {
                return string.Empty;
            }

            return $"<complexity>\n{string.Join("\n", recommendations)}\n</complexity>";
        }
        catch (System.Exception ex)
        {
            logger.LogError(ex, "Error analyzing complexity for {SymbolType} {SymbolName}",
                symbol.GetType().Name, symbol.ToDisplayString());
            return string.Empty;
        }
    }

    private static async Task<string> AnalyzeSimilarityAsync(
        ISymbol symbol,
        ISemanticSimilarityService semanticSimilarityService,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        const double similarityThreshold = 0.85;

        try
        {
            if (symbol is IMethodSymbol methodSymbol)
            {
                List<MethodSimilarityResult> similarMethods = await semanticSimilarityService.FindSimilarMethodsAsync(similarityThreshold, cancellationToken);

                MethodSimilarityResult? matchingGroup = similarMethods.FirstOrDefault(group =>
                    group.SimilarMethods.Any(m => m.FullyQualifiedMethodName == methodSymbol.ToDisplayString()));

                if (matchingGroup != null)
                {
                    MethodSemanticFeatures? similarMethod = matchingGroup.SimilarMethods
                        .Where(m => m.FullyQualifiedMethodName != methodSymbol.ToDisplayString())
                        .OrderByDescending(m => m.MethodName)
                        .FirstOrDefault();

                    if (similarMethod != null)
                    {
                        return $"<similarity>\nFound similar method: {similarMethod.FullyQualifiedMethodName}\n" +
                               $"Similarity score: {matchingGroup.AverageSimilarityScore:F2}\n" +
                               $"Please analyze for potential duplication.\n</similarity>";
                    }
                }
            }
            else if (symbol is INamedTypeSymbol typeSymbol)
            {
                List<ClassSimilarityResult> similarClasses = await semanticSimilarityService.FindSimilarClassesAsync(similarityThreshold, cancellationToken);

                ClassSimilarityResult? matchingGroup = similarClasses.FirstOrDefault(group =>
                    group.SimilarClasses.Any(c => c.FullyQualifiedClassName == typeSymbol.ToDisplayString()));

                if (matchingGroup != null)
                {
                    ClassSemanticFeatures? similarClass = matchingGroup.SimilarClasses
                        .Where(c => c.FullyQualifiedClassName != typeSymbol.ToDisplayString())
                        .OrderByDescending(c => c.ClassName)
                        .FirstOrDefault();

                    if (similarClass != null)
                    {
                        return $"<similarity>\nFound similar type: {similarClass.FullyQualifiedClassName}\n" +
                               $"Similarity score: {matchingGroup.AverageSimilarityScore:F2}\n" +
                               $"Please analyze for potential duplication.\n</similarity>";
                    }
                }
            }

            return string.Empty;
        }
        catch (System.Exception ex)
        {
            logger.LogError(ex, "Error analyzing similarity for {SymbolType} {SymbolName}",
                symbol.GetType().Name, symbol.ToDisplayString());
            return string.Empty;
        }
    }
}
