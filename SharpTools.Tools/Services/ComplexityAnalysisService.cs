using SharpTools.Tools.Extensions;
using ModelContextProtocol;

namespace SharpTools.Tools.Services;

/// <summary>
/// Service for analyzing code complexity metrics.
/// </summary>
public class ComplexityAnalysisService : IComplexityAnalysisService {
    private readonly ISolutionManager _solutionManager;
    private readonly ILogger<ComplexityAnalysisService> _logger;

    public ComplexityAnalysisService(ISolutionManager solutionManager, ILogger<ComplexityAnalysisService> logger) {
        _solutionManager = solutionManager;
        _logger = logger;
    }
    public async Task AnalyzeMethodAsync(
        IMethodSymbol methodSymbol,
        Dictionary<string, object> metrics,
        List<string> recommendations,
        CancellationToken cancellationToken) {
        var syntaxRef = methodSymbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) {
            _logger.LogWarning("Method {Method} has no syntax reference", methodSymbol.Name);
            return;
        }

        var methodNode = await syntaxRef.GetSyntaxAsync(cancellationToken) as MethodDeclarationSyntax;
        if (methodNode == null) {
            _logger.LogWarning("Could not get method syntax for {Method}", methodSymbol.Name);
            return;
        }

        // Basic metrics
        var lineCount = methodNode.GetText().Lines.Count;
        var statementCount = methodNode.DescendantNodes().OfType<StatementSyntax>().Count();
        var parameterCount = methodSymbol.Parameters.Length;
        var localVarCount = methodNode.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().Count();

        metrics["lineCount"] = lineCount;
        metrics["statementCount"] = statementCount;
        metrics["parameterCount"] = parameterCount;
        metrics["localVariableCount"] = localVarCount;

        // Cyclomatic complexity
        int cyclomaticComplexity = 1; // Base complexity
        cyclomaticComplexity += methodNode.DescendantNodes().Count(n => {
            switch (n) {
                case IfStatementSyntax:
                case SwitchSectionSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case CatchClauseSyntax:
                case ConditionalExpressionSyntax:
                    return true;
                case BinaryExpressionSyntax bex:
                    return bex.IsKind(SyntaxKind.LogicalAndExpression) ||
                           bex.IsKind(SyntaxKind.LogicalOrExpression);
                default:
                    return false;
            }
        });

        metrics["cyclomaticComplexity"] = cyclomaticComplexity;

        // Cognitive complexity (simplified version)
        int cognitiveComplexity = 0;
        int nesting = 0;

        void AddCognitiveComplexity(int value) => cognitiveComplexity += value + nesting;

        foreach (var node in methodNode.DescendantNodes()) {
            bool isNestingNode = false;

            switch (node) {
                case IfStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case CatchClauseSyntax:
                    AddCognitiveComplexity(1);
                    isNestingNode = true;
                    break;
                case SwitchStatementSyntax:
                    AddCognitiveComplexity(1);
                    break;
                case BinaryExpressionSyntax bex:
                    if (bex.IsKind(SyntaxKind.LogicalAndExpression) ||
                        bex.IsKind(SyntaxKind.LogicalOrExpression)) {
                        AddCognitiveComplexity(1);
                    }
                    break;
                case LambdaExpressionSyntax:
                    AddCognitiveComplexity(1);
                    isNestingNode = true;
                    break;
                case RecursivePatternSyntax:
                    AddCognitiveComplexity(1);
                    break;
            }

            if (isNestingNode) {
                nesting++;
                // We'll decrement nesting when processing the block end
            }
        }

        metrics["cognitiveComplexity"] = cognitiveComplexity;

        // Outgoing dependencies (method calls)
        // Check if solution is available before using it
        int methodCallCount = 0;
        if (_solutionManager.CurrentSolution != null) {
            var compilation = await _solutionManager.GetCompilationAsync(
                methodNode.SyntaxTree.GetRequiredProject(_solutionManager.CurrentSolution).Id,
                cancellationToken);

            if (compilation != null) {
                var semanticModel = compilation.GetSemanticModel(methodNode.SyntaxTree);
                var methodCalls = methodNode.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Select(i => semanticModel.GetSymbolInfo(i).Symbol)
                    .OfType<IMethodSymbol>()
                    .Where(m => !SymbolEqualityComparer.Default.Equals(m.ContainingType, methodSymbol.ContainingType))
                    .Select(m => m.ContainingType.ToDisplayString())
                    .Distinct()
                    .ToList();
                methodCallCount = methodCalls.Count;
                metrics["externalMethodCalls"] = methodCallCount;
                metrics["externalDependencies"] = methodCalls;
            }
        } else {
            _logger.LogWarning("Cannot analyze method dependencies: No solution loaded");
        }

        // Add recommendations based on metrics
        if (lineCount > 50)
            recommendations.Add($"Method '{methodSymbol.Name}' is {lineCount} lines long. Consider breaking it into smaller methods.");

        if (cyclomaticComplexity > 10)
            recommendations.Add($"Method '{methodSymbol.Name}' has high cyclomatic complexity ({cyclomaticComplexity}). Consider refactoring into smaller methods.");

        if (cognitiveComplexity > 20)
            recommendations.Add($"Method '{methodSymbol.Name}' has high cognitive complexity ({cognitiveComplexity}). Consider simplifying the logic or breaking it down.");

        if (parameterCount > 4)
            recommendations.Add($"Method '{methodSymbol.Name}' has {parameterCount} parameters. Consider grouping related parameters into a class.");

        if (localVarCount > 10)
            recommendations.Add($"Method '{methodSymbol.Name}' has {localVarCount} local variables. Consider breaking some logic into helper methods.");

        if (methodCallCount > 5)
            recommendations.Add($"Method '{methodSymbol.Name}' has {methodCallCount} external method calls. Consider reducing dependencies or breaking it into smaller methods.");
    }
    public async Task AnalyzeTypeAsync(
        INamedTypeSymbol typeSymbol,
        Dictionary<string, object> metrics,
        List<string> recommendations,
        bool includeGeneratedCode,
        CancellationToken cancellationToken) {
        var typeMetrics = new Dictionary<string, object>();

        // Basic type metrics
        typeMetrics["kind"] = typeSymbol.TypeKind.ToString();
        typeMetrics["isAbstract"] = typeSymbol.IsAbstract;
        typeMetrics["isSealed"] = typeSymbol.IsSealed;
        typeMetrics["isGeneric"] = typeSymbol.IsGenericType;

        // Member counts
        var members = typeSymbol.GetMembers();
        typeMetrics["totalMemberCount"] = members.Length;
        typeMetrics["methodCount"] = members.Count(m => m is IMethodSymbol);
        typeMetrics["propertyCount"] = members.Count(m => m is IPropertySymbol);
        typeMetrics["fieldCount"] = members.Count(m => m is IFieldSymbol);
        typeMetrics["eventCount"] = members.Count(m => m is IEventSymbol);

        // Inheritance metrics
        var baseTypes = new List<string>();
        var inheritanceDepth = 0;
        var currentType = typeSymbol.BaseType;

        while (currentType != null && !currentType.SpecialType.Equals(SpecialType.System_Object)) {
            baseTypes.Add(currentType.ToDisplayString());
            inheritanceDepth++;
            currentType = currentType.BaseType;
        }

        typeMetrics["inheritanceDepth"] = inheritanceDepth;
        typeMetrics["baseTypes"] = baseTypes;
        typeMetrics["implementedInterfaces"] = typeSymbol.AllInterfaces.Select(i => i.ToDisplayString()).ToList();

        // Analyze methods
        var methodMetrics = new List<Dictionary<string, object>>();
        var methodComplexitySum = 0;
        var methodCount = 0;

        foreach (var member in members.OfType<IMethodSymbol>()) {
            if (member.IsImplicitlyDeclared) continue;

            var methodDict = new Dictionary<string, object>();
            await AnalyzeMethodAsync(member, methodDict, recommendations, cancellationToken);

            if (methodDict.ContainsKey("cyclomaticComplexity")) {
                methodComplexitySum += (int)methodDict["cyclomaticComplexity"];
                methodCount++;
            }

            methodMetrics.Add(methodDict);
        }

        typeMetrics["methods"] = methodMetrics;
        typeMetrics["averageMethodComplexity"] = methodCount > 0 ? (double)methodComplexitySum / methodCount : 0;

        // Coupling analysis
        var dependencies = new HashSet<string>();
        var syntaxRefs = typeSymbol.DeclaringSyntaxReferences;

        // Check if solution is available before using it
        if (_solutionManager.CurrentSolution != null) {
            foreach (var syntaxRef in syntaxRefs) {
                var syntax = await syntaxRef.GetSyntaxAsync(cancellationToken);
                var project = syntax.SyntaxTree.GetRequiredProject(_solutionManager.CurrentSolution);
                var compilation = await _solutionManager.GetCompilationAsync(project.Id, cancellationToken);

                if (compilation != null) {
                    var semanticModel = compilation.GetSemanticModel(syntax.SyntaxTree);

                    // Find all type references in the class
                    foreach (var node in syntax.DescendantNodes()) {
                        if (cancellationToken.IsCancellationRequested) break; var symbolInfo = semanticModel.GetSymbolInfo(node).Symbol;
                        if (symbolInfo?.ContainingType != null &&
                        !SymbolEqualityComparer.Default.Equals(symbolInfo.ContainingType, typeSymbol) &&
                        !symbolInfo.ContainingType.SpecialType.Equals(SpecialType.System_Object)) {
                            dependencies.Add(symbolInfo.ContainingType.ToDisplayString());
                        }
                    }
                }
            }
        } else {
            _logger.LogWarning("Cannot analyze type dependencies: No solution loaded");
        }

        typeMetrics["dependencyCount"] = dependencies.Count;
        typeMetrics["dependencies"] = dependencies.ToList();

        // Add type-level recommendations
        if (inheritanceDepth > 5)
            recommendations.Add($"Type '{typeSymbol.Name}' has deep inheritance ({inheritanceDepth} levels). Consider composition over inheritance.");

        if (dependencies.Count > 20)
            recommendations.Add($"Type '{typeSymbol.Name}' has high coupling ({dependencies.Count} dependencies). Consider breaking it into smaller classes.");

        if (members.Length > 50)
            recommendations.Add($"Type '{typeSymbol.Name}' has {members.Length} members. Consider breaking it into smaller, focused classes.");

        if (typeMetrics["averageMethodComplexity"] is double avg && avg > 12)
            recommendations.Add($"Type '{typeSymbol.Name}' has high average method complexity ({avg:F1}). Consider refactoring complex methods.");

        metrics["typeMetrics"] = typeMetrics;
    }
    public async Task AnalyzeProjectAsync(
        Project project,
        Dictionary<string, object> metrics,
        List<string> recommendations,
        bool includeGeneratedCode,
        CancellationToken cancellationToken) {
        var projectMetrics = new Dictionary<string, object>();
        var typeMetrics = new List<Dictionary<string, object>>();

        // Project-wide metrics
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation == null) {
            throw new McpException($"Could not get compilation for project {project.Name}");
        }

        var syntaxTrees = compilation.SyntaxTrees;
        if (!includeGeneratedCode) {
            syntaxTrees = syntaxTrees.Where(tree =>
                !tree.FilePath.Contains(".g.cs") &&
                !tree.FilePath.Contains(".Designer.cs"));
        }

        projectMetrics["fileCount"] = syntaxTrees.Count();

        // Calculate total lines manually to avoid async enumeration complexity
        var totalLines = 0;
        foreach (var tree in syntaxTrees) {
            if (cancellationToken.IsCancellationRequested) break;
            var text = await tree.GetTextAsync(cancellationToken);
            totalLines += text.Lines.Count;
        }
        projectMetrics["totalLines"] = totalLines;

        var globalComplexityMetrics = new Dictionary<string, object> {
            ["totalCyclomaticComplexity"] = 0,
            ["totalCognitiveComplexity"] = 0,
            ["maxMethodComplexity"] = 0,
            ["complexMethodCount"] = 0,
            ["averageMethodComplexity"] = 0.0,
            ["methodCount"] = 0
        };

        foreach (var tree in syntaxTrees) {
            if (cancellationToken.IsCancellationRequested) break;

            var semanticModel = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync(cancellationToken);

            // Analyze each type in the file
            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>()) {
                var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                if (typeSymbol != null) {
                    var typeDict = new Dictionary<string, object>();
                    await AnalyzeTypeAsync(typeSymbol, typeDict, recommendations, includeGeneratedCode, cancellationToken);
                    typeMetrics.Add(typeDict);

                    // Aggregate complexity metrics
                    if (typeDict.TryGetValue("typeMetrics", out var typeMetricsObj) &&
                        typeMetricsObj is Dictionary<string, object> tm &&
                        tm.TryGetValue("methods", out var methodsObj) &&
                        methodsObj is List<Dictionary<string, object>> methods) {
                        foreach (var method in methods) {
                            if (method.TryGetValue("cyclomaticComplexity", out var ccObj) &&
                                ccObj is int cc) {
                                globalComplexityMetrics["totalCyclomaticComplexity"] =
                                    (int)globalComplexityMetrics["totalCyclomaticComplexity"] + cc;

                                globalComplexityMetrics["maxMethodComplexity"] =
                                    Math.Max((int)globalComplexityMetrics["maxMethodComplexity"], cc);

                                if (cc > 10)
                                    globalComplexityMetrics["complexMethodCount"] =
                                        (int)globalComplexityMetrics["complexMethodCount"] + 1;

                                globalComplexityMetrics["methodCount"] =
                                    (int)globalComplexityMetrics["methodCount"] + 1;
                            }

                            if (method.TryGetValue("cognitiveComplexity", out var cogObj) &&
                                cogObj is int cog) {
                                globalComplexityMetrics["totalCognitiveComplexity"] =
                                    (int)globalComplexityMetrics["totalCognitiveComplexity"] + cog;
                            }
                        }
                    }
                }
            }
        }

        // Calculate averages
        if ((int)globalComplexityMetrics["methodCount"] > 0) {
            globalComplexityMetrics["averageMethodComplexity"] =
                (double)(int)globalComplexityMetrics["totalCyclomaticComplexity"] /
                (int)globalComplexityMetrics["methodCount"];
        }

        projectMetrics["complexityMetrics"] = globalComplexityMetrics;
        projectMetrics["typeMetrics"] = typeMetrics;

        // Project-wide recommendations
        var avgComplexity = (double)globalComplexityMetrics["averageMethodComplexity"];
        var complexMethodCount = (int)globalComplexityMetrics["complexMethodCount"];

        if (avgComplexity > 5)
            recommendations.Add($"Project has high average method complexity ({avgComplexity:F1}). Consider refactoring complex methods.");

        if (complexMethodCount > 0)
            recommendations.Add($"Project has {complexMethodCount} methods with high cyclomatic complexity (>10). Consider refactoring these methods.");

        var totalTypes = typeMetrics.Count;
        if (totalTypes > 50)
            recommendations.Add($"Project has {totalTypes} types. Consider breaking it into multiple projects if they serve different concerns.");

        metrics["projectMetrics"] = projectMetrics;
    }
}