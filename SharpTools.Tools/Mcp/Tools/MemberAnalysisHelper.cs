using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using SharpTools.Tools.Interfaces;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SharpTools.Tools.Mcp.Tools {
    public static class MemberAnalysisHelper {
        /// <summary>
        /// Analyzes a newly added member for complexity and similarity.
        /// </summary>
        /// <returns>A formatted string with analysis results.</returns>
        public static async Task<string> AnalyzeAddedMemberAsync(
            ISymbol addedSymbol,
            IComplexityAnalysisService complexityAnalysisService,
            ISemanticSimilarityService semanticSimilarityService,
            ILogger logger,
            CancellationToken cancellationToken) {

            if (addedSymbol == null) {
                logger.LogWarning("Cannot analyze null symbol");
                return string.Empty;
            }

            var results = new List<string>();

            // Get complexity recommendations
            var complexityResults = await AnalyzeComplexityAsync(addedSymbol, complexityAnalysisService, logger, cancellationToken);
            if (!string.IsNullOrEmpty(complexityResults)) {
                results.Add(complexityResults);
            }

            // Check for similar members
            var similarityResults = await AnalyzeSimilarityAsync(addedSymbol, semanticSimilarityService, logger, cancellationToken);
            if (!string.IsNullOrEmpty(similarityResults)) {
                results.Add(similarityResults);
            }

            if (results.Count == 0) {
                return string.Empty;
            }

            return $"\n<analysisResults>\n{string.Join("\n\n", results)}\n</analysisResults>";
        }

        private static async Task<string> AnalyzeComplexityAsync(
            ISymbol symbol,
            IComplexityAnalysisService complexityAnalysisService,
            ILogger logger,
            CancellationToken cancellationToken) {

            var recommendations = new List<string>();
            var metrics = new Dictionary<string, object>();

            try {
                if (symbol is IMethodSymbol methodSymbol) {
                    await complexityAnalysisService.AnalyzeMethodAsync(methodSymbol, metrics, recommendations, cancellationToken);
                } else if (symbol is INamedTypeSymbol typeSymbol) {
                    await complexityAnalysisService.AnalyzeTypeAsync(typeSymbol, metrics, recommendations, false, cancellationToken);
                } else {
                    // No complexity analysis for other symbol types
                    return string.Empty;
                }

                if (recommendations.Count == 0) {
                    return string.Empty;
                }

                return $"<complexity>\n{string.Join("\n", recommendations)}\n</complexity>";
            } catch (System.Exception ex) {
                logger.LogError(ex, "Error analyzing complexity for {SymbolType} {SymbolName}",
                    symbol.GetType().Name, symbol.ToDisplayString());
                return string.Empty;
            }
        }

        private static async Task<string> AnalyzeSimilarityAsync(
            ISymbol symbol,
            ISemanticSimilarityService semanticSimilarityService,
            ILogger logger,
            CancellationToken cancellationToken) {

            const double similarityThreshold = 0.85;

            try {
                if (symbol is IMethodSymbol methodSymbol) {
                    var similarMethods = await semanticSimilarityService.FindSimilarMethodsAsync(similarityThreshold, cancellationToken);

                    var matchingGroup = similarMethods.FirstOrDefault(group =>
                        group.SimilarMethods.Any(m => m.FullyQualifiedMethodName == methodSymbol.ToDisplayString()));

                    if (matchingGroup != null) {
                        var similarMethod = matchingGroup.SimilarMethods
                            .Where(m => m.FullyQualifiedMethodName != methodSymbol.ToDisplayString())
                            .OrderByDescending(m => m.MethodName)
                            .FirstOrDefault();

                        if (similarMethod != null) {
                            return $"<similarity>\nFound similar method: {similarMethod.FullyQualifiedMethodName}\nSimilarity score: {matchingGroup.AverageSimilarityScore:F2}\nPlease analyze for potential duplication.\n</similarity>";
                        }
                    }
                } else if (symbol is INamedTypeSymbol typeSymbol) {
                    var similarClasses = await semanticSimilarityService.FindSimilarClassesAsync(similarityThreshold, cancellationToken);

                    var matchingGroup = similarClasses.FirstOrDefault(group =>
                        group.SimilarClasses.Any(c => c.FullyQualifiedClassName == typeSymbol.ToDisplayString()));

                    if (matchingGroup != null) {
                        var similarClass = matchingGroup.SimilarClasses
                            .Where(c => c.FullyQualifiedClassName != typeSymbol.ToDisplayString())
                            .OrderByDescending(c => c.ClassName)
                            .FirstOrDefault();

                        if (similarClass != null) {
                            return $"<similarity>\nFound similar type: {similarClass.FullyQualifiedClassName}\nSimilarity score: {matchingGroup.AverageSimilarityScore:F2}\nPlease analyze for potential duplication.\n</similarity>";
                        }
                    }
                }

                return string.Empty;
            } catch (System.Exception ex) {
                logger.LogError(ex, "Error analyzing similarity for {SymbolType} {SymbolName}",
                    symbol.GetType().Name, symbol.ToDisplayString());
                return string.Empty;
            }
        }
    }
}