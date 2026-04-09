using System.Collections.Immutable;
using ModelContextProtocol;
using SharpTools.Tools.Extensions;

namespace SharpTools.Tools.Services;

/// <summary>
/// Service for analyzing code complexity metrics.
/// </summary>
public class ComplexityAnalysisService(
    ISolutionManager solutionManager,
    ILogger<ComplexityAnalysisService> logger) : IComplexityAnalysisService
{
    private readonly ISolutionManager _solutionManager = solutionManager;
    private readonly ILogger<ComplexityAnalysisService> _logger = logger;

    public async Task AnalyzeMethodAsync(
        IMethodSymbol methodSymbol,
        Dictionary<string, object> metrics,
        List<string> recommendations,
        CancellationToken cancellationToken)
    {
        SyntaxReference? syntaxRef = methodSymbol.DeclaringSyntaxReferences.FirstOrDefault();

        if (syntaxRef == null)
        {
            _logger.LogWarning("Method {Method} has no syntax reference", methodSymbol.Name);
            return;
        }

        MethodDeclarationSyntax? methodNode = await syntaxRef.GetSyntaxAsync(cancellationToken) as MethodDeclarationSyntax;

        if (methodNode == null)
        {
            _logger.LogWarning("Could not get method syntax for {Method}", methodSymbol.Name);
            return;
        }

        // Basic metrics
        int lineCount = methodNode.GetText().Lines.Count;
        int statementCount = methodNode.DescendantNodes().OfType<StatementSyntax>().Count();
        int parameterCount = methodSymbol.Parameters.Length;
        int localVarCount = methodNode.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().Count();

        metrics["lineCount"] = lineCount;
        metrics["statementCount"] = statementCount;
        metrics["parameterCount"] = parameterCount;
        metrics["localVariableCount"] = localVarCount;

        // Cyclomatic complexity
        int cyclomaticComplexity = 1; // Base complexity
        cyclomaticComplexity += methodNode.DescendantNodes().Count(n =>
        {
            switch (n)
            {
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
                    return bex.IsKind(SyntaxKind.LogicalAndExpression)
                        || bex.IsKind(SyntaxKind.LogicalOrExpression);
                default:
                    return false;
            }
        });

        metrics["cyclomaticComplexity"] = cyclomaticComplexity;

        // Cognitive complexity (simplified version)
        int cognitiveComplexity = 0;
        int nesting = 0;

        void AddCognitiveComplexity(int value) => cognitiveComplexity += value + nesting;

        foreach (SyntaxNode node in methodNode.DescendantNodes())
        {
            bool isNestingNode = false;

            switch (node)
            {
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
                    if (bex.IsKind(SyntaxKind.LogicalAndExpression)
                        || bex.IsKind(SyntaxKind.LogicalOrExpression))
                    {
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

            if (isNestingNode)
            {
                nesting++;
                // We'll decrement nesting when processing the block end
            }
        }

        metrics["cognitiveComplexity"] = cognitiveComplexity;

        // Outgoing dependencies (method calls)
        // Check if solution is available before using it
        int methodCallCount = 0;

        if (_solutionManager.CurrentSolution != null)
        {
            Compilation? compilation = await _solutionManager.GetCompilationAsync(
                methodNode.SyntaxTree.GetRequiredProject(_solutionManager.CurrentSolution).Id,
                cancellationToken);

            if (compilation != null)
            {
                SemanticModel semanticModel = compilation.GetSemanticModel(methodNode.SyntaxTree);
                List<string> methodCalls = [.. methodNode.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Select(i => semanticModel.GetSymbolInfo(i).Symbol)
                    .OfType<IMethodSymbol>()
                    .Where(m => SymbolEqualityComparer.Default.Equals(m.ContainingType, methodSymbol.ContainingType) == false)
                    .Select(m => m.ContainingType.ToDisplayString())
                    .Distinct()];

                methodCallCount = methodCalls.Count;
                metrics["externalMethodCalls"] = methodCallCount;
                metrics["externalDependencies"] = methodCalls;
            }
        }
        else
        {
            _logger.LogWarning("Cannot analyze method dependencies: No solution loaded");
        }

        // Add recommendations based on metrics
        if (lineCount > 50)
        {
            recommendations.Add(
                $"Method '{methodSymbol.Name}' is {lineCount} lines long. Consider breaking it into smaller methods.");
        }

        if (cyclomaticComplexity > 10)
        {
            recommendations.Add(
                $"Method '{methodSymbol.Name}' has high cyclomatic complexity ({cyclomaticComplexity}). Consider refactoring into smaller methods.");
        }

        if (cognitiveComplexity > 20)
        {
            recommendations.Add(
                $"Method '{methodSymbol.Name}' has high cognitive complexity ({cognitiveComplexity}). Consider simplifying the logic or breaking it down.");
        }

        if (parameterCount > 4)
        {
            recommendations.Add(
                $"Method '{methodSymbol.Name}' has {parameterCount} parameters. Consider grouping related parameters into a class.");
        }

        if (localVarCount > 10)
        {
            recommendations.Add(
                $"Method '{methodSymbol.Name}' has {localVarCount} local variables. Consider breaking some logic into helper methods.");
        }

        if (methodCallCount > 5)
        {
            recommendations.Add(
                $"Method '{methodSymbol.Name}' has {methodCallCount} external method calls. Consider reducing dependencies or breaking it into smaller methods.");
        }
    }

    public async Task AnalyzeTypeAsync(
        INamedTypeSymbol typeSymbol,
        Dictionary<string, object> metrics,
        List<string> recommendations,
        bool includeGeneratedCode,
        CancellationToken cancellationToken)
    {
        Dictionary<string, object> typeMetrics = new()
        {
            // Basic type metrics
            ["kind"] = typeSymbol.TypeKind.ToString(),
            ["isAbstract"] = typeSymbol.IsAbstract,
            ["isSealed"] = typeSymbol.IsSealed,
            ["isGeneric"] = typeSymbol.IsGenericType
        };

        // Member counts
        ImmutableArray<ISymbol> members = typeSymbol.GetMembers();
        typeMetrics["totalMemberCount"] = members.Length;
        typeMetrics["methodCount"] = members.Count(m => m is IMethodSymbol);
        typeMetrics["propertyCount"] = members.Count(m => m is IPropertySymbol);
        typeMetrics["fieldCount"] = members.Count(m => m is IFieldSymbol);
        typeMetrics["eventCount"] = members.Count(m => m is IEventSymbol);

        // Inheritance metrics
        List<string> baseTypes = [];
        int inheritanceDepth = 0;
        INamedTypeSymbol? currentType = typeSymbol.BaseType;

        while (currentType != null && currentType.SpecialType.Equals(SpecialType.System_Object) == false)
        {
            baseTypes.Add(currentType.ToDisplayString());
            inheritanceDepth++;
            currentType = currentType.BaseType;
        }

        typeMetrics["inheritanceDepth"] = inheritanceDepth;
        typeMetrics["baseTypes"] = baseTypes;
        typeMetrics["implementedInterfaces"] = typeSymbol.AllInterfaces.Select(i => i.ToDisplayString()).ToList();

        // Analyze methods
        List<Dictionary<string, object>> methodMetrics = [];
        int methodComplexitySum = 0;
        int methodCount = 0;

        foreach (IMethodSymbol member in members.OfType<IMethodSymbol>())
        {
            if (member.IsImplicitlyDeclared)
            {
                continue;
            }

            Dictionary<string, object> methodDict = [];
            await AnalyzeMethodAsync(member, methodDict, recommendations, cancellationToken);

            if (methodDict.TryGetValue("cyclomaticComplexity", out object? value))
            {
                methodComplexitySum += (int)value;
                methodCount++;
            }

            methodMetrics.Add(methodDict);
        }

        typeMetrics["methods"] = methodMetrics;
        typeMetrics["averageMethodComplexity"] = methodCount > 0 ? (double)methodComplexitySum / methodCount : 0;

        // Coupling analysis
        HashSet<string> dependencies = [];
        ImmutableArray<SyntaxReference> syntaxRefs = typeSymbol.DeclaringSyntaxReferences;

        // Check if solution is available before using it
        if (_solutionManager.CurrentSolution != null)
        {
            foreach (SyntaxReference syntaxRef in syntaxRefs)
            {
                SyntaxNode syntax = await syntaxRef.GetSyntaxAsync(cancellationToken);
                Project project = syntax.SyntaxTree.GetRequiredProject(_solutionManager.CurrentSolution);
                Compilation? compilation = await _solutionManager.GetCompilationAsync(
                    project.Id,
                    cancellationToken);

                if (compilation != null)
                {
                    SemanticModel semanticModel = compilation.GetSemanticModel(syntax.SyntaxTree);

                    // Find all type references in the class
                    foreach (SyntaxNode node in syntax.DescendantNodes())
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        ISymbol? symbolInfo = semanticModel.GetSymbolInfo(node).Symbol;

                        if (symbolInfo?.ContainingType != null
                            && SymbolEqualityComparer.Default.Equals(symbolInfo.ContainingType, typeSymbol) == false
                            && symbolInfo.ContainingType.SpecialType.Equals(SpecialType.System_Object) == false)
                        {
                            dependencies.Add(symbolInfo.ContainingType.ToDisplayString());
                        }
                    }
                }
            }
        }
        else
        {
            _logger.LogWarning("Cannot analyze type dependencies: No solution loaded");
        }

        typeMetrics["dependencyCount"] = dependencies.Count;
        typeMetrics["dependencies"] = dependencies.ToList();

        // Add type-level recommendations
        if (inheritanceDepth > 5)
        {
            recommendations.Add(
                $"Type '{typeSymbol.Name}' has deep inheritance ({inheritanceDepth} levels). Consider composition over inheritance.");
        }

        if (dependencies.Count > 20)
        {
            recommendations.Add(
                $"Type '{typeSymbol.Name}' has high coupling ({dependencies.Count} dependencies). Consider breaking it into smaller classes.");
        }

        if (members.Length > 50)
        {
            recommendations.Add(
                $"Type '{typeSymbol.Name}' has {members.Length} members. Consider breaking it into smaller, focused classes.");
        }

        if (typeMetrics["averageMethodComplexity"] is double avg && avg > 12)
        {
            recommendations.Add(
                $"Type '{typeSymbol.Name}' has high average method complexity ({avg:F1}). Consider refactoring complex methods.");
        }

        metrics["typeMetrics"] = typeMetrics;
    }

    public async Task AnalyzeProjectAsync(
        Project project,
        Dictionary<string, object> metrics,
        List<string> recommendations,
        bool includeGeneratedCode,
        CancellationToken cancellationToken)
    {
        Dictionary<string, object> projectMetrics = [];
        List<Dictionary<string, object>> typeMetrics = [];

        // Project-wide metrics
        Compilation? compilation = await project.GetCompilationAsync(cancellationToken) ?? throw new McpException($"Could not get compilation for project {project.Name}");
        IEnumerable<SyntaxTree> syntaxTrees = compilation.SyntaxTrees;

        if (includeGeneratedCode == false)
        {
            syntaxTrees = syntaxTrees.Where(tree =>
                tree.FilePath.Contains(".g.cs") == false
                && tree.FilePath.Contains(".Designer.cs") == false);
        }

        projectMetrics["fileCount"] = syntaxTrees.Count();

        // Calculate total lines manually to avoid async enumeration complexity
        int totalLines = 0;

        foreach (SyntaxTree tree in syntaxTrees)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            SourceText text = await tree.GetTextAsync(cancellationToken);
            totalLines += text.Lines.Count;
        }

        projectMetrics["totalLines"] = totalLines;

        Dictionary<string, object> globalComplexityMetrics = new()
        {
            ["totalCyclomaticComplexity"] = 0,
            ["totalCognitiveComplexity"] = 0,
            ["maxMethodComplexity"] = 0,
            ["complexMethodCount"] = 0,
            ["averageMethodComplexity"] = 0.0,
            ["methodCount"] = 0
        };

        foreach (SyntaxTree tree in syntaxTrees)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            SemanticModel semanticModel = compilation.GetSemanticModel(tree);
            SyntaxNode root = await tree.GetRootAsync(cancellationToken);

            // Analyze each type in the file
            foreach (TypeDeclarationSyntax typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                INamedTypeSymbol? typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;

                if (typeSymbol != null)
                {
                    Dictionary<string, object> typeDict = [];
                    await AnalyzeTypeAsync(typeSymbol, typeDict, recommendations, includeGeneratedCode, cancellationToken);
                    typeMetrics.Add(typeDict);

                    // Aggregate complexity metrics
                    if (typeDict.TryGetValue("typeMetrics", out object? typeMetricsObj)
                        && typeMetricsObj is Dictionary<string, object> tm
                        && tm.TryGetValue("methods", out object? methodsObj)
                        && methodsObj is List<Dictionary<string, object>> methods)
                    {
                        foreach (Dictionary<string, object> method in methods)
                        {
                            if (method.TryGetValue("cyclomaticComplexity", out object? ccObj)
                                && ccObj is int cc)
                            {
                                globalComplexityMetrics["totalCyclomaticComplexity"] =
                                    (int)globalComplexityMetrics["totalCyclomaticComplexity"] + cc;

                                globalComplexityMetrics["maxMethodComplexity"] =
                                    Math.Max((int)globalComplexityMetrics["maxMethodComplexity"], cc);

                                if (cc > 10)
                                {
                                    globalComplexityMetrics["complexMethodCount"] =
                                        (int)globalComplexityMetrics["complexMethodCount"] + 1;
                                }

                                globalComplexityMetrics["methodCount"] =
                                    (int)globalComplexityMetrics["methodCount"] + 1;
                            }

                            if (method.TryGetValue("cognitiveComplexity", out object? cogObj)
                                && cogObj is int cog)
                            {
                                globalComplexityMetrics["totalCognitiveComplexity"] =
                                    (int)globalComplexityMetrics["totalCognitiveComplexity"] + cog;
                            }
                        }
                    }
                }
            }
        }

        // Calculate averages
        if ((int)globalComplexityMetrics["methodCount"] > 0)
        {
            globalComplexityMetrics["averageMethodComplexity"] =
                (double)(int)globalComplexityMetrics["totalCyclomaticComplexity"]
                / (int)globalComplexityMetrics["methodCount"];
        }

        projectMetrics["complexityMetrics"] = globalComplexityMetrics;
        projectMetrics["typeMetrics"] = typeMetrics;

        // Project-wide recommendations
        double avgComplexity = (double)globalComplexityMetrics["averageMethodComplexity"];
        int complexMethodCount = (int)globalComplexityMetrics["complexMethodCount"];

        if (avgComplexity > 5)
        {
            recommendations.Add(
                $"Project has high average method complexity ({avgComplexity:F1}). Consider refactoring complex methods.");
        }

        if (complexMethodCount > 0)
        {
            recommendations.Add(
                $"Project has {complexMethodCount} methods with high cyclomatic complexity (>10). Consider refactoring these methods.");
        }

        int totalTypes = typeMetrics.Count;

        if (totalTypes > 50)
        {
            recommendations.Add(
                $"Project has {totalTypes} types. Consider breaking it into multiple projects if they serve different concerns.");
        }

        metrics["projectMetrics"] = projectMetrics;
    }
}
