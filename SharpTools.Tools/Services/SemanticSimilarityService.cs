using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using SharpTools.Tools.Mcp;

namespace SharpTools.Tools.Services;

public class SemanticSimilarityService(
    ISolutionManager solutionManager,
    ICodeAnalysisService codeAnalysisService,
    ILogger<SemanticSimilarityService> logger,
    IComplexityAnalysisService complexityAnalysisService) : ISemanticSimilarityService
{
    private static class Tuning
    {
        public static readonly int MaxDegreesOfParallelism = Math.Max(1, Environment.ProcessorCount / 2);

        public const int MethodLineCountFilter = 10;
        public const double DefaultSimilarityThreshold = 0.7;

        public static class Normalization
        {
            public const int MaxBasicBlockCount = 60;
            public const int MaxConditionalBranchCount = 25;
            public const int MaxLoopCount = 8;
            public const int MaxCyclomaticComplexity = 30;
        }

        public static class Weights
        {
            public enum Feature
            {
                ReturnType,
                ParamCount,
                ParamTypes,
                InvokedMethods,
                BasicBlocks,
                ConditionalBranches,
                Loops,
                CyclomaticComplexity,
                OperationCounts,
                AccessedMemberTypes
            }

            public static readonly Dictionary<Feature, double> FeatureWeights = new()
            {
                { Feature.InvokedMethods, 0.25 },
                { Feature.OperationCounts, 0.20 },
                { Feature.AccessedMemberTypes, 0.15 },
                { Feature.ParamTypes, 0.10 },
                { Feature.CyclomaticComplexity, 0.075 },
                { Feature.ReturnType, 0.05 },
                { Feature.BasicBlocks, 0.05 },
                { Feature.ConditionalBranches, 0.05 },
                { Feature.Loops, 0.05 },
                { Feature.ParamCount, 0.025 },
            };

            public static double ReturnType => FeatureWeights[Feature.ReturnType];
            public static double ParamCount => FeatureWeights[Feature.ParamCount];
            public static double ParamTypes => FeatureWeights[Feature.ParamTypes];
            public static double InvokedMethods => FeatureWeights[Feature.InvokedMethods];
            public static double BasicBlocks => FeatureWeights[Feature.BasicBlocks];
            public static double ConditionalBranches => FeatureWeights[Feature.ConditionalBranches];
            public static double Loops => FeatureWeights[Feature.Loops];
            public static double CyclomaticComplexity => FeatureWeights[Feature.CyclomaticComplexity];
            public static double OperationCounts => FeatureWeights[Feature.OperationCounts];
            public static double AccessedMemberTypes => FeatureWeights[Feature.AccessedMemberTypes];
            public static double TotalWeight => FeatureWeights.Values.Sum();
        }

        public const int ClassLineCountFilter = 20;

        public static class ClassNormalization
        {
            public const int MaxPropertyCount = 30;
            public const int MaxFieldCount = 50;
            public const int MaxMethodCount = 50;
            public const int MaxImplementedInterfaces = 10;
            public const int MaxReferencedExternalTypes = 75;
            public const int MaxUsedNamespaces = 20;
            public const double MaxAverageMethodComplexity = 15.0;
        }

        public static class ClassWeights
        {
            public enum Feature
            {
                BaseClassName,
                ImplementedInterfaceNames,
                PublicMethodCount,
                ProtectedMethodCount,
                PrivateMethodCount,
                StaticMethodCount,
                AbstractMethodCount,
                VirtualMethodCount,
                PropertyCount,
                ReadOnlyPropertyCount,
                StaticPropertyCount,
                FieldCount,
                StaticFieldCount,
                ReadonlyFieldCount,
                ConstFieldCount,
                EventCount,
                NestedClassCount,
                NestedStructCount,
                NestedEnumCount,
                NestedInterfaceCount,
                AverageMethodComplexity,
                DistinctReferencedExternalTypeFqns,
                DistinctUsedNamespaceFqns,
                TotalLinesOfCode,
                MethodMatchingSimilarity
            }

            public static readonly Dictionary<Feature, double> FeatureWeights = new()
            {
                { Feature.MethodMatchingSimilarity, 0.20 },
                { Feature.ImplementedInterfaceNames, 0.15 },
                { Feature.DistinctReferencedExternalTypeFqns, 0.15 },
                { Feature.BaseClassName, 0.07 },
                { Feature.AverageMethodComplexity, 0.05 },
                { Feature.PublicMethodCount, 0.03 },
                { Feature.PropertyCount, 0.03 },
                { Feature.FieldCount, 0.03 },
                { Feature.DistinctUsedNamespaceFqns, 0.03 },
                { Feature.TotalLinesOfCode, 0.02 },
                { Feature.ProtectedMethodCount, 0.02 },
                { Feature.PrivateMethodCount, 0.02 },
                { Feature.StaticMethodCount, 0.02 },
                { Feature.AbstractMethodCount, 0.02 },
                { Feature.VirtualMethodCount, 0.02 },
                { Feature.ReadOnlyPropertyCount, 0.01 },
                { Feature.StaticPropertyCount, 0.01 },
                { Feature.StaticFieldCount, 0.01 },
                { Feature.ReadonlyFieldCount, 0.01 },
                { Feature.ConstFieldCount, 0.01 },
                { Feature.EventCount, 0.01 },
                { Feature.NestedClassCount, 0.01 },
                { Feature.NestedStructCount, 0.01 },
                { Feature.NestedEnumCount, 0.01 },
                { Feature.NestedInterfaceCount, 0.01 }
            };

            public static double BaseClassName => FeatureWeights[Feature.BaseClassName];
            public static double ImplementedInterfaceNames => FeatureWeights[Feature.ImplementedInterfaceNames];
            public static double PublicMethodCount => FeatureWeights[Feature.PublicMethodCount];
            public static double ProtectedMethodCount => FeatureWeights[Feature.ProtectedMethodCount];
            public static double PrivateMethodCount => FeatureWeights[Feature.PrivateMethodCount];
            public static double StaticMethodCount => FeatureWeights[Feature.StaticMethodCount];
            public static double AbstractMethodCount => FeatureWeights[Feature.AbstractMethodCount];
            public static double VirtualMethodCount => FeatureWeights[Feature.VirtualMethodCount];
            public static double PropertyCount => FeatureWeights[Feature.PropertyCount];
            public static double ReadOnlyPropertyCount => FeatureWeights[Feature.ReadOnlyPropertyCount];
            public static double StaticPropertyCount => FeatureWeights[Feature.StaticPropertyCount];
            public static double FieldCount => FeatureWeights[Feature.FieldCount];
            public static double StaticFieldCount => FeatureWeights[Feature.StaticFieldCount];
            public static double ReadonlyFieldCount => FeatureWeights[Feature.ReadonlyFieldCount];
            public static double ConstFieldCount => FeatureWeights[Feature.ConstFieldCount];
            public static double EventCount => FeatureWeights[Feature.EventCount];
            public static double NestedClassCount => FeatureWeights[Feature.NestedClassCount];
            public static double NestedStructCount => FeatureWeights[Feature.NestedStructCount];
            public static double NestedEnumCount => FeatureWeights[Feature.NestedEnumCount];
            public static double NestedInterfaceCount => FeatureWeights[Feature.NestedInterfaceCount];
            public static double AverageMethodComplexity => FeatureWeights[Feature.AverageMethodComplexity];
            public static double DistinctReferencedExternalTypeFqns
                => FeatureWeights[Feature.DistinctReferencedExternalTypeFqns];
            public static double DistinctUsedNamespaceFqns => FeatureWeights[Feature.DistinctUsedNamespaceFqns];
            public static double TotalLinesOfCode => FeatureWeights[Feature.TotalLinesOfCode];
            public static double MethodMatchingSimilarity => FeatureWeights[Feature.MethodMatchingSimilarity];
            public static double TotalWeight => FeatureWeights.Values.Sum();
        }
    }

    private readonly ISolutionManager _solutionManager = solutionManager ?? throw new ArgumentNullException(nameof(solutionManager));
    private readonly ICodeAnalysisService _codeAnalysisService = codeAnalysisService ?? throw new ArgumentNullException(nameof(codeAnalysisService));
    private readonly ILogger<SemanticSimilarityService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IComplexityAnalysisService _complexityAnalysisService = complexityAnalysisService
            ?? throw new ArgumentNullException(nameof(complexityAnalysisService));

    public async Task<List<MethodSimilarityResult>> FindSimilarMethodsAsync(
        double similarityThreshold,
        CancellationToken cancellationToken)
    {
        ToolHelpers.EnsureSolutionLoadedWithDetails(_solutionManager, _logger, nameof(FindSimilarMethodsAsync));
        _logger.LogInformation(
            "Starting semantic similarity analysis with threshold {Threshold}, MaxDOP: {MaxDop}",
            similarityThreshold,
            Tuning.MaxDegreesOfParallelism);

        ConcurrentBag<MethodSemanticFeatures> allMethodFeatures = [];

        ParallelOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = Tuning.MaxDegreesOfParallelism,
            CancellationToken = cancellationToken
        };

        List<Project> projects = [.. _solutionManager.GetProjects()];

        await Parallel.ForEachAsync(projects, parallelOptions, async (project, ct) =>
        {
            if (ct.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Semantic similarity analysis cancelled during project iteration for {ProjectName}.",
                    project.Name);
                return;
            }

            _logger.LogDebug("Analyzing project: {ProjectName}", project.Name);
            Compilation? compilation = await project.GetCompilationAsync(ct);
            if (compilation == null)
            {
                _logger.LogWarning("Could not get compilation for project {ProjectName}", project.Name);
                return;
            }

            List<Document> documents = [.. project.Documents];

            await Parallel.ForEachAsync(documents, parallelOptions, async (document, docCt) =>
            {
                if (docCt.IsCancellationRequested)
                {
                    return;
                }

                if (document.SupportsSyntaxTree == false || document.SupportsSemanticModel == false)
                {
                    return;
                }

                _logger.LogTrace("Analyzing document: {DocumentFilePath}", document.FilePath);
                SyntaxTree? syntaxTree = await document.GetSyntaxTreeAsync(docCt);
                SemanticModel? semanticModel = await document.GetSemanticModelAsync(docCt);
                if (syntaxTree == null || semanticModel == null)
                {
                    return;
                }

                IEnumerable<MethodDeclarationSyntax> methodDeclarations =
                    syntaxTree.GetRoot(docCt).DescendantNodes().OfType<MethodDeclarationSyntax>();

                foreach (MethodDeclarationSyntax methodDecl in methodDeclarations)
                {
                    if (docCt.IsCancellationRequested)
                    {
                        break;
                    }

                    IMethodSymbol? methodSymbol =
                        semanticModel.GetDeclaredSymbol(methodDecl, docCt) as IMethodSymbol;
                    if (methodSymbol == null
                        || methodSymbol.IsAbstract
                        || methodSymbol.IsExtern
                        || ToolHelpers.IsPropertyAccessor(methodSymbol))
                    {
                        continue;
                    }

                    try
                    {
                        MethodSemanticFeatures? features =
                            await ExtractFeaturesAsync(methodSymbol, methodDecl, document, semanticModel, docCt);
                        if (features != null)
                        {
                            allMethodFeatures.Add(features);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation(
                            "Feature extraction cancelled for method {MethodName} in {FilePath}",
                            methodSymbol?.Name ?? "Unknown",
                            document.FilePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to extract features for method {MethodName} in {FilePath}",
                            methodSymbol?.Name ?? "Unknown",
                            document.FilePath);
                    }
                }
            });
        });

        if (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Semantic similarity analysis was cancelled before comparison.");
            throw new OperationCanceledException("Semantic similarity analysis was cancelled.");
        }

        _logger.LogInformation(
            "Extracted features for {MethodCount} methods. Starting similarity comparison.",
            allMethodFeatures.Count);
        return CompareFeatures([.. allMethodFeatures], similarityThreshold, cancellationToken);
    }

    private async Task<MethodSemanticFeatures?> ExtractFeaturesAsync(
        IMethodSymbol methodSymbol,
        MethodDeclarationSyntax methodDecl,
        Document document,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        string filePath = document.FilePath ?? "unknown";
        int startLine = methodDecl.GetLocation().GetLineSpan().StartLinePosition.Line;
        int endLine = methodDecl.GetLocation().GetLineSpan().EndLinePosition.Line;
        int lineCount = endLine - startLine + 1;

        if (lineCount < Tuning.MethodLineCountFilter)
        {
            _logger.LogDebug(
                "Method {MethodName} in {FilePath} has {LineCount} lines, "
                    + "which is less than the filter of {FilterCount}. Skipping.",
                methodSymbol.Name,
                filePath,
                lineCount,
                Tuning.MethodLineCountFilter);
            return null;
        }

        string methodName = methodSymbol.Name;
        string fullyQualifiedMethodName =
            methodSymbol.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal);
        string returnTypeName =
            methodSymbol.ReturnType.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal);
        List<string> parameterTypeNames = [.. methodSymbol.Parameters.Select(p => p.Type.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal))];

        HashSet<string> invokedMethodSignatures = [];
        Dictionary<string, int> operationCounts = [];
        HashSet<string> distinctAccessedMemberTypes = [];

        int basicBlockCount = 0;
        int conditionalBranchCount = 0;
        int loopCount = 0;

        Dictionary<string, object> methodMetrics = [];
        List<string> recommendations = [];
        await _complexityAnalysisService.AnalyzeMethodAsync(
            methodSymbol, methodMetrics, recommendations, cancellationToken);
        int cyclomaticComplexity =
            methodMetrics.TryGetValue("cyclomaticComplexity", out object? cc) && cc is int ccVal ? ccVal : 1;

        SyntaxNode? bodyOrExpressionBody =
            methodDecl.Body ?? (SyntaxNode?)methodDecl.ExpressionBody?.Expression;

        if (bodyOrExpressionBody != null)
        {
            try
            {
                ControlFlowGraph? controlFlowGraph =
                    ControlFlowGraph.Create(methodDecl, semanticModel, cancellationToken);
                if (controlFlowGraph != null && controlFlowGraph.Blocks.Any())
                {
                    basicBlockCount = controlFlowGraph.Blocks.Length;
                }

                _logger.LogDebug(
                    "ControlFlowGraph created for method {MethodName} in {FilePath}. "
                        + "BasicBlockCount: {BasicBlockCount}",
                    methodName,
                    filePath,
                    basicBlockCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to create ControlFlowGraph for method {MethodName} in {FilePath}. "
                        + "CFG-based features will be zero.",
                    methodName,
                    filePath);
            }

            IOperation? operation = semanticModel.GetOperation(bodyOrExpressionBody, cancellationToken);
            if (operation != null)
            {
                foreach (IOperation opNode in operation.DescendantsAndSelf())
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return null;
                    }

                    string opKindName = opNode.Kind.ToString();
                    operationCounts[opKindName] = operationCounts.GetValueOrDefault(opKindName, 0) + 1;

                    if (opNode is IInvocationOperation invocation)
                    {
                        invokedMethodSignatures.Add(
                            invocation.TargetMethod.OriginalDefinition.ToDisplayString(
                                SymbolDisplayFormat.CSharpErrorMessageFormat));
                    }
                    else if (opNode is IFieldReferenceOperation fieldRef)
                    {
                        distinctAccessedMemberTypes.Add(
                            fieldRef.Field.Type.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal));
                    }
                    else if (opNode is IPropertyReferenceOperation propRef)
                    {
                        distinctAccessedMemberTypes.Add(
                            propRef.Property.Type.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal));
                    }

                    if (opNode is ILoopOperation)
                    {
                        loopCount++;
                    }
                    else if (opNode is IConditionalOperation)
                    {
                        conditionalBranchCount++;
                    }
                }
            }
        }

        return new MethodSemanticFeatures(
            fullyQualifiedMethodName,
            filePath,
            startLine,
            methodName,
            returnTypeName,
            parameterTypeNames,
            invokedMethodSignatures,
            basicBlockCount,
            conditionalBranchCount,
            loopCount,
            cyclomaticComplexity,
            operationCounts,
            distinctAccessedMemberTypes);
    }

    private List<MethodSimilarityResult> CompareFeatures(
        List<MethodSemanticFeatures> allMethodFeatures,
        double similarityThreshold,
        CancellationToken cancellationToken)
    {
        List<MethodSimilarityResult> results = [];
        HashSet<int> processedIndices = [];
        _logger.LogInformation(
            "Starting similarity comparison for {MethodCount} methods.", allMethodFeatures.Count);

        for (int i = 0; i < allMethodFeatures.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Semantic similarity analysis was cancelled.");
            }

            if (processedIndices.Contains(i))
            {
                continue;
            }

            MethodSemanticFeatures currentMethod = allMethodFeatures[i];
            List<MethodSemanticFeatures> similarGroup = [currentMethod];
            processedIndices.Add(i);
            double groupTotalScore = 0;
            int comparisonsMade = 0;
            _logger.LogDebug(
                "Comparing method {MethodName} ({FQN}) with other methods.",
                currentMethod.MethodName,
                currentMethod.FullyQualifiedMethodName);

            for (int j = i + 1; j < allMethodFeatures.Count; j++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Semantic similarity analysis was cancelled.");
                }

                if (processedIndices.Contains(j))
                {
                    continue;
                }

                MethodSemanticFeatures otherMethod = allMethodFeatures[j];

                if (currentMethod.FullyQualifiedMethodName == otherMethod.FullyQualifiedMethodName
                    && currentMethod.ParameterTypeNames.SequenceEqual(otherMethod.ParameterTypeNames) == false)
                {
                    _logger.LogDebug(
                        "Skipping comparison between overloads: {Method1FQN} ({Params1}) "
                            + "and {Method2FQN} ({Params2})",
                        currentMethod.FullyQualifiedMethodName,
                        string.Join(", ", currentMethod.ParameterTypeNames),
                        otherMethod.FullyQualifiedMethodName,
                        string.Join(", ", otherMethod.ParameterTypeNames));
                    continue;
                }

                double similarity = CalculateSimilarity(currentMethod, otherMethod);

                if (similarity >= similarityThreshold)
                {
                    similarGroup.Add(otherMethod);
                    processedIndices.Add(j);
                    groupTotalScore += similarity;
                    comparisonsMade++;
                    _logger.LogDebug(
                        "Method {OtherMethodName} ({OtherFQN}) is similar to {CurrentMethodName} ({CurrentFQN}) "
                            + "with score {SimilarityScore}",
                        otherMethod.MethodName,
                        otherMethod.FullyQualifiedMethodName,
                        currentMethod.MethodName,
                        currentMethod.FullyQualifiedMethodName,
                        similarity);
                }
            }

            if (similarGroup.Count > 1)
            {
                double averageScore = comparisonsMade > 0 ? groupTotalScore / comparisonsMade : 1.0;
                results.Add(new MethodSimilarityResult(similarGroup, averageScore));
                _logger.LogInformation(
                    "Found similarity group of {GroupSize} methods, starting with {MethodName} ({FQN}), "
                        + "Avg Score: {Score:F2}",
                    similarGroup.Count,
                    currentMethod.MethodName,
                    currentMethod.FullyQualifiedMethodName,
                    averageScore);
            }
        }

        _logger.LogInformation(
            "Semantic similarity analysis complete. Found {GroupCount} groups.", results.Count);
        return [.. results.OrderByDescending(r => r.AverageSimilarityScore)];
    }

    private double CalculateSimilarity(MethodSemanticFeatures method1, MethodSemanticFeatures method2)
    {
        double returnTypeSimilarity = (method1.ReturnTypeName == method2.ReturnTypeName) ? 1.0 : 0.0;
        double paramCountSimilarity =
            (method1.ParameterTypeNames.Count == method2.ParameterTypeNames.Count) ? 1.0 : 0.0;
        double paramTypeSimilarity = 0.0;
        if (method1.ParameterTypeNames.Count == method2.ParameterTypeNames.Count
            && method1.ParameterTypeNames.Any())
        {
            int matchingParams = 0;
            for (int k = 0; k < method1.ParameterTypeNames.Count; k++)
            {
                if (method1.ParameterTypeNames[k] == method2.ParameterTypeNames[k])
                {
                    matchingParams++;
                }
            }

            paramTypeSimilarity = (double)matchingParams / method1.ParameterTypeNames.Count;
        }
        else if (method1.ParameterTypeNames.Count == 0 && method2.ParameterTypeNames.Count == 0)
        {
            paramTypeSimilarity = 1.0;
        }

        double invokedSimilarity = 0.0;
        if (method1.InvokedMethodSignatures.Any() || method2.InvokedMethodSignatures.Any())
        {
            int intersection = method1.InvokedMethodSignatures.Intersect(method2.InvokedMethodSignatures).Count();
            int union = method1.InvokedMethodSignatures.Union(method2.InvokedMethodSignatures).Count();
            invokedSimilarity = union > 0 ? (double)intersection / union : 1.0;
        }
        else
        {
            invokedSimilarity = 1.0;
        }

        double basicBlockSimilarity = 1.0 - CalculateNormalizedDifference(
            method1.BasicBlockCount, method2.BasicBlockCount, Tuning.Normalization.MaxBasicBlockCount);
        double conditionalBranchSimilarity = 1.0 - CalculateNormalizedDifference(
            method1.ConditionalBranchCount,
            method2.ConditionalBranchCount,
            Tuning.Normalization.MaxConditionalBranchCount);
        double loopSimilarity = 1.0 - CalculateNormalizedDifference(
            method1.LoopCount, method2.LoopCount, Tuning.Normalization.MaxLoopCount);
        double cyclomaticComplexitySimilarity = 1.0 - CalculateNormalizedDifference(
            method1.CyclomaticComplexity,
            method2.CyclomaticComplexity,
            Tuning.Normalization.MaxCyclomaticComplexity);
        double operationCountsSimilarity =
            CalculateCosineSimilarity(method1.OperationCounts, method2.OperationCounts);
        double accessedTypesSimilarity = 0.0;

        if (method1.DistinctAccessedMemberTypes.Any() || method2.DistinctAccessedMemberTypes.Any())
        {
            int intersectionTypes =
                method1.DistinctAccessedMemberTypes.Intersect(method2.DistinctAccessedMemberTypes).Count();
            int unionTypes =
                method1.DistinctAccessedMemberTypes.Union(method2.DistinctAccessedMemberTypes).Count();
            accessedTypesSimilarity = unionTypes > 0 ? (double)intersectionTypes / unionTypes : 1.0;
        }
        else
        {
            accessedTypesSimilarity = 1.0;
        }

        double totalWeightedScore =
            returnTypeSimilarity * Tuning.Weights.ReturnType +
            paramCountSimilarity * Tuning.Weights.ParamCount +
            paramTypeSimilarity * Tuning.Weights.ParamTypes +
            invokedSimilarity * Tuning.Weights.InvokedMethods +
            basicBlockSimilarity * Tuning.Weights.BasicBlocks +
            conditionalBranchSimilarity * Tuning.Weights.ConditionalBranches +
            loopSimilarity * Tuning.Weights.Loops +
            cyclomaticComplexitySimilarity * Tuning.Weights.CyclomaticComplexity +
            operationCountsSimilarity * Tuning.Weights.OperationCounts +
            accessedTypesSimilarity * Tuning.Weights.AccessedMemberTypes;

        return totalWeightedScore / Tuning.Weights.TotalWeight;
    }

    private double CalculateNormalizedDifference(int val1, int val2, int maxValue)
    {
        if (maxValue == 0)
        {
            return (val1 == val2) ? 0.0 : 1.0;
        }

        double diff = Math.Abs(val1 - val2);
        return diff / maxValue;
    }

    private double CalculateCosineSimilarity(Dictionary<string, int> vec1, Dictionary<string, int> vec2)
    {
        if (vec1.Any() == false && vec2.Any() == false)
        {
            return 1.0;
        }

        if (vec1.Any() == false || vec2.Any() == false)
        {
            return 0.0;
        }

        List<string> allKeys = [.. vec1.Keys.Union(vec2.Keys)];
        double dotProduct = 0.0;
        double magnitude1 = 0.0;
        double magnitude2 = 0.0;

        foreach (string key in allKeys)
        {
            int val1 = vec1.GetValueOrDefault(key, 0);
            int val2 = vec2.GetValueOrDefault(key, 0);

            dotProduct += val1 * val2;
            magnitude1 += val1 * val1;
            magnitude2 += val2 * val2;
        }

        magnitude1 = Math.Sqrt(magnitude1);
        magnitude2 = Math.Sqrt(magnitude2);

        if (magnitude1 == 0 || magnitude2 == 0)
        {
            return 0.0;
        }

        return dotProduct / (magnitude1 * magnitude2);
    }

    public async Task<List<ClassSimilarityResult>> FindSimilarClassesAsync(
        double similarityThreshold,
        CancellationToken cancellationToken)
    {
        ToolHelpers.EnsureSolutionLoadedWithDetails(_solutionManager, _logger, nameof(FindSimilarClassesAsync));
        _logger.LogInformation(
            "Starting class semantic similarity analysis with threshold {Threshold}, MaxDOP: {MaxDop}",
            similarityThreshold,
            Tuning.MaxDegreesOfParallelism);

        ConcurrentBag<ClassSemanticFeatures> allClassFeatures = [];

        ParallelOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = Tuning.MaxDegreesOfParallelism,
            CancellationToken = cancellationToken
        };

        List<Project> projects = [.. _solutionManager.GetProjects()];

        await Parallel.ForEachAsync(projects, parallelOptions, async (project, ct) =>
        {
            if (ct.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Class semantic similarity analysis cancelled during project iteration for {ProjectName}.",
                    project.Name);
                return;
            }

            _logger.LogDebug("Analyzing project for classes: {ProjectName}", project.Name);
            Compilation? compilation = await project.GetCompilationAsync(ct);
            if (compilation == null)
            {
                _logger.LogWarning("Could not get compilation for project {ProjectName}", project.Name);
                return;
            }

            List<Document> documents = [.. project.Documents];

            await Parallel.ForEachAsync(documents, parallelOptions, async (document, docCt) =>
            {
                if (docCt.IsCancellationRequested)
                {
                    return;
                }

                if (document.SupportsSyntaxTree == false || document.SupportsSemanticModel == false)
                {
                    return;
                }

                _logger.LogTrace("Analyzing document for classes: {DocumentFilePath}", document.FilePath);
                SyntaxTree? syntaxTree = await document.GetSyntaxTreeAsync(docCt);
                SemanticModel? semanticModel = await document.GetSemanticModelAsync(docCt);
                if (syntaxTree == null || semanticModel == null)
                {
                    return;
                }

                IEnumerable<TypeDeclarationSyntax> classDeclarations =
                    syntaxTree.GetRoot(docCt).DescendantNodes()
                        .OfType<TypeDeclarationSyntax>()
                        .Where(tds =>
                            tds.Kind() == Microsoft.CodeAnalysis.CSharp.SyntaxKind.ClassDeclaration
                            || tds.Kind() == Microsoft.CodeAnalysis.CSharp.SyntaxKind.RecordDeclaration);

                foreach (TypeDeclarationSyntax classDecl in classDeclarations)
                {
                    if (docCt.IsCancellationRequested)
                    {
                        break;
                    }

                    INamedTypeSymbol? classSymbol =
                        semanticModel.GetDeclaredSymbol(classDecl, docCt) as INamedTypeSymbol;
                    if (classSymbol == null || classSymbol.IsAbstract || classSymbol.IsStatic)
                    {
                        continue;
                    }

                    try
                    {
                        ClassSemanticFeatures? features = await ExtractClassFeaturesAsync(
                            classSymbol, classDecl, document, semanticModel, docCt);
                        if (features != null)
                        {
                            allClassFeatures.Add(features);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation(
                            "Feature extraction cancelled for class {ClassName} in {FilePath}",
                            classSymbol?.Name ?? "Unknown",
                            document.FilePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to extract features for class {ClassName} in {FilePath}",
                            classSymbol?.Name ?? "Unknown",
                            document.FilePath);
                    }
                }
            });
        });

        if (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Class semantic similarity analysis was cancelled before comparison.");
            throw new OperationCanceledException("Class semantic similarity analysis was cancelled.");
        }

        _logger.LogInformation(
            "Extracted features for {ClassCount} classes. Starting similarity comparison.",
            allClassFeatures.Count);
        return CompareClassFeatures([.. allClassFeatures], similarityThreshold, cancellationToken);
    }

    private async Task<ClassSemanticFeatures?> ExtractClassFeaturesAsync(
        INamedTypeSymbol classSymbol,
        TypeDeclarationSyntax classDecl,
        Document document,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        string filePath = document.FilePath ?? "unknown";
        int startLine = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line;
        int endLine = classDecl.GetLocation().GetLineSpan().EndLinePosition.Line;
        int totalLinesOfCode = endLine - startLine + 1;

        if (totalLinesOfCode < Tuning.ClassLineCountFilter)
        {
            _logger.LogDebug(
                "Class {ClassName} in {FilePath} has {LineCount} lines, less than filter {FilterCount}. Skipping.",
                classSymbol.Name,
                filePath,
                totalLinesOfCode,
                Tuning.ClassLineCountFilter);
            return null;
        }

        string className = classSymbol.Name;
        string fullyQualifiedClassName =
            classSymbol.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal);

        HashSet<string> distinctReferencedExternalTypeFqns = [];
        HashSet<string> distinctUsedNamespaceFqns = [];

        AddTypeAndNamespaceIfExternal(
            classSymbol.BaseType, classSymbol, distinctReferencedExternalTypeFqns, distinctUsedNamespaceFqns);
        string? baseClassName =
            classSymbol.BaseType?.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal);

        List<string> implementedInterfaceNames = [];
        foreach (INamedTypeSymbol iface in classSymbol.AllInterfaces)
        {
            AddTypeAndNamespaceIfExternal(
                iface, classSymbol, distinctReferencedExternalTypeFqns, distinctUsedNamespaceFqns);
            implementedInterfaceNames.Add(
                iface.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal));
        }

        int publicMethodCount = 0, protectedMethodCount = 0, privateMethodCount = 0,
            staticMethodCount = 0, abstractMethodCount = 0, virtualMethodCount = 0;
        int propertyCount = 0, readOnlyPropertyCount = 0, staticPropertyCount = 0;
        int fieldCount = 0, staticFieldCount = 0, readonlyFieldCount = 0, constFieldCount = 0;
        int eventCount = 0;
        int nestedClassCount = 0, nestedStructCount = 0, nestedEnumCount = 0, nestedInterfaceCount = 0;
        double totalMethodComplexity = 0;
        int analyzedMethodCount = 0;
        List<MethodSemanticFeatures> classMethodFeatures = [];

        if (classDecl.SyntaxTree.GetRoot(cancellationToken) is CompilationUnitSyntax compilationUnit)
        {
            foreach (UsingDirectiveSyntax usingDirective in compilationUnit.Usings)
            {
                if (usingDirective.Name != null)
                {
                    INamespaceSymbol? namespaceSymbol =
                        semanticModel.GetSymbolInfo(usingDirective.Name, cancellationToken).Symbol
                            as INamespaceSymbol;
                    if (namespaceSymbol != null && namespaceSymbol.IsGlobalNamespace == false)
                    {
                        distinctUsedNamespaceFqns.Add(
                            namespaceSymbol.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal));
                    }
                }
            }
        }

        foreach (ISymbol memberSymbol in classSymbol.GetMembers())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (memberSymbol is IMethodSymbol methodMember)
            {
                if (ToolHelpers.IsPropertyAccessor(methodMember) || methodMember.IsImplicitlyDeclared)
                {
                    continue;
                }

                if (methodMember.DeclaredAccessibility == Accessibility.Public)
                {
                    publicMethodCount++;
                }
                else if (methodMember.DeclaredAccessibility == Accessibility.Protected)
                {
                    protectedMethodCount++;
                }
                else if (methodMember.DeclaredAccessibility == Accessibility.Private)
                {
                    privateMethodCount++;
                }

                if (methodMember.IsStatic)
                {
                    staticMethodCount++;
                }

                if (methodMember.IsAbstract)
                {
                    abstractMethodCount++;
                }

                if (methodMember.IsVirtual)
                {
                    virtualMethodCount++;
                }

                AddTypeAndNamespaceIfExternal(
                    methodMember.ReturnType,
                    classSymbol,
                    distinctReferencedExternalTypeFqns,
                    distinctUsedNamespaceFqns);
                foreach (IParameterSymbol param in methodMember.Parameters)
                {
                    AddTypeAndNamespaceIfExternal(
                        param.Type,
                        classSymbol,
                        distinctReferencedExternalTypeFqns,
                        distinctUsedNamespaceFqns);
                }

                if (memberSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken)
                    is MethodDeclarationSyntax methodDeclSyntax)
                {
                    MethodSemanticFeatures? features = await ExtractFeaturesAsync(
                        methodMember, methodDeclSyntax, document, semanticModel, cancellationToken);
                    if (features != null)
                    {
                        classMethodFeatures.Add(features);
                        totalMethodComplexity += features.CyclomaticComplexity;
                        analyzedMethodCount++;
                    }
                }
            }
            else if (memberSymbol is IPropertySymbol propertyMember)
            {
                propertyCount++;
                if (propertyMember.IsReadOnly)
                {
                    readOnlyPropertyCount++;
                }

                if (propertyMember.IsStatic)
                {
                    staticPropertyCount++;
                }

                AddTypeAndNamespaceIfExternal(
                    propertyMember.Type,
                    classSymbol,
                    distinctReferencedExternalTypeFqns,
                    distinctUsedNamespaceFqns);
            }
            else if (memberSymbol is IFieldSymbol fieldMember)
            {
                fieldCount++;
                if (fieldMember.IsStatic)
                {
                    staticFieldCount++;
                }

                if (fieldMember.IsReadOnly)
                {
                    readonlyFieldCount++;
                }

                if (fieldMember.IsConst)
                {
                    constFieldCount++;
                }

                AddTypeAndNamespaceIfExternal(
                    fieldMember.Type,
                    classSymbol,
                    distinctReferencedExternalTypeFqns,
                    distinctUsedNamespaceFqns);
            }
            else if (memberSymbol is IEventSymbol eventMember)
            {
                eventCount++;
                AddTypeAndNamespaceIfExternal(
                    eventMember.Type,
                    classSymbol,
                    distinctReferencedExternalTypeFqns,
                    distinctUsedNamespaceFqns);
            }
            else if (memberSymbol is INamedTypeSymbol nestedTypeMember)
            {
                if (nestedTypeMember.TypeKind == TypeKind.Class)
                {
                    nestedClassCount++;
                }
                else if (nestedTypeMember.TypeKind == TypeKind.Struct)
                {
                    nestedStructCount++;
                }
                else if (nestedTypeMember.TypeKind == TypeKind.Enum)
                {
                    nestedEnumCount++;
                }
                else if (nestedTypeMember.TypeKind == TypeKind.Interface)
                {
                    nestedInterfaceCount++;
                }
            }
        }

        foreach (SyntaxNode node in classDecl.DescendantNodes(
            descendIntoChildren: n => n is not TypeDeclarationSyntax || n == classDecl))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            ISymbol? referencedSymbol = null;
            if (node is IdentifierNameSyntax identifierName)
            {
                referencedSymbol = semanticModel.GetSymbolInfo(identifierName, cancellationToken).Symbol;
            }
            else if (node is MemberAccessExpressionSyntax memberAccess)
            {
                referencedSymbol = semanticModel.GetSymbolInfo(memberAccess.Name, cancellationToken).Symbol;
            }
            else if (node is ObjectCreationExpressionSyntax objectCreation)
            {
                referencedSymbol = semanticModel.GetSymbolInfo(objectCreation.Type, cancellationToken).Symbol;
            }
            else if (node is InvocationExpressionSyntax invocation
                && invocation.Expression is MemberAccessExpressionSyntax maes)
            {
                referencedSymbol = semanticModel.GetSymbolInfo(maes.Name, cancellationToken).Symbol;
            }

            if (referencedSymbol is ITypeSymbol typeSym)
            {
                AddTypeAndNamespaceIfExternal(
                    typeSym, classSymbol, distinctReferencedExternalTypeFqns, distinctUsedNamespaceFqns);
            }
            else if (referencedSymbol is IMethodSymbol methodSym)
            {
                AddTypeAndNamespaceIfExternal(
                    methodSym.ReturnType,
                    classSymbol,
                    distinctReferencedExternalTypeFqns,
                    distinctUsedNamespaceFqns);
                foreach (IParameterSymbol param in methodSym.Parameters)
                {
                    AddTypeAndNamespaceIfExternal(
                        param.Type,
                        classSymbol,
                        distinctReferencedExternalTypeFqns,
                        distinctUsedNamespaceFqns);
                }
            }
            else if (referencedSymbol is IPropertySymbol propSym)
            {
                AddTypeAndNamespaceIfExternal(
                    propSym.Type, classSymbol, distinctReferencedExternalTypeFqns, distinctUsedNamespaceFqns);
            }
            else if (referencedSymbol is IFieldSymbol fieldSym)
            {
                AddTypeAndNamespaceIfExternal(
                    fieldSym.Type, classSymbol, distinctReferencedExternalTypeFqns, distinctUsedNamespaceFqns);
            }
            else if (referencedSymbol is IEventSymbol eventSym)
            {
                AddTypeAndNamespaceIfExternal(
                    eventSym.Type, classSymbol, distinctReferencedExternalTypeFqns, distinctUsedNamespaceFqns);
            }
            else if (referencedSymbol is INamespaceSymbol nsSym && nsSym.IsGlobalNamespace == false)
            {
                if (nsSym.ContainingAssembly != null
                    && classSymbol.ContainingAssembly != null
                    && SymbolEqualityComparer.Default.Equals(
                        nsSym.ContainingAssembly, classSymbol.ContainingAssembly) == false)
                {
                    distinctUsedNamespaceFqns.Add(
                        nsSym.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal));
                }
                else if (nsSym.ContainingAssembly == null && classSymbol.ContainingAssembly != null)
                {
                    // Namespace is likely global or part of the current compilation but not tied to a
                    // specific assembly in the same way types are.
                }
            }
        }

        double averageMethodComplexity =
            analyzedMethodCount > 0 ? totalMethodComplexity / analyzedMethodCount : 0;

        return new ClassSemanticFeatures(
            fullyQualifiedClassName,
            filePath,
            startLine,
            className,
            baseClassName,
            [.. implementedInterfaceNames.Distinct()],
            publicMethodCount,
            protectedMethodCount,
            privateMethodCount,
            staticMethodCount,
            abstractMethodCount,
            virtualMethodCount,
            propertyCount,
            readOnlyPropertyCount,
            staticPropertyCount,
            fieldCount,
            staticFieldCount,
            readonlyFieldCount,
            constFieldCount,
            eventCount,
            nestedClassCount,
            nestedStructCount,
            nestedEnumCount,
            nestedInterfaceCount,
            averageMethodComplexity,
            distinctReferencedExternalTypeFqns,
            distinctUsedNamespaceFqns,
            totalLinesOfCode,
            classMethodFeatures);
    }

    private List<ClassSimilarityResult> CompareClassFeatures(
        List<ClassSemanticFeatures> allClassFeatures,
        double similarityThreshold,
        CancellationToken cancellationToken)
    {
        List<ClassSimilarityResult> results = [];
        HashSet<int> processedIndices = [];
        _logger.LogInformation(
            "Starting class similarity comparison for {ClassCount} classes.", allClassFeatures.Count);

        for (int i = 0; i < allClassFeatures.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Class similarity analysis was cancelled.");
            }

            if (processedIndices.Contains(i))
            {
                continue;
            }

            ClassSemanticFeatures currentClass = allClassFeatures[i];
            List<ClassSemanticFeatures> similarGroup = [currentClass];
            processedIndices.Add(i);
            double groupTotalScore = 0;
            int comparisonsMade = 0;

            for (int j = i + 1; j < allClassFeatures.Count; j++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Class similarity analysis was cancelled.");
                }

                if (processedIndices.Contains(j))
                {
                    continue;
                }

                ClassSemanticFeatures otherClass = allClassFeatures[j];
                double similarity = CalculateClassSimilarity(currentClass, otherClass);

                if (similarity >= similarityThreshold)
                {
                    similarGroup.Add(otherClass);
                    processedIndices.Add(j);
                    groupTotalScore += similarity;
                    comparisonsMade++;
                }
            }

            if (similarGroup.Count > 1)
            {
                double averageScore = comparisonsMade > 0 ? groupTotalScore / comparisonsMade : 1.0;
                results.Add(new ClassSimilarityResult(similarGroup, averageScore));
                _logger.LogInformation(
                    "Found class similarity group of {GroupSize}, starting with {ClassName}, "
                        + "Avg Score: {Score:F2}",
                    similarGroup.Count,
                    currentClass.ClassName,
                    averageScore);
            }
        }

        _logger.LogInformation(
            "Class semantic similarity analysis complete. Found {GroupCount} groups.", results.Count);
        return [.. results.OrderByDescending(r => r.AverageSimilarityScore)];
    }

    private double CalculateClassSimilarity(ClassSemanticFeatures class1, ClassSemanticFeatures class2)
    {
        double baseClassSimilarity = (class1.BaseClassName == class2.BaseClassName) ? 1.0 :
            (string.IsNullOrEmpty(class1.BaseClassName) && string.IsNullOrEmpty(class2.BaseClassName)
                ? 1.0 : 0.0);

        double interfaceSimilarity =
            CalculateJaccardSimilarity(class1.ImplementedInterfaceNames, class2.ImplementedInterfaceNames);
        double referencedTypesSimilarity = CalculateJaccardSimilarity(
            class1.DistinctReferencedExternalTypeFqns, class2.DistinctReferencedExternalTypeFqns);
        double usedNamespacesSimilarity =
            CalculateJaccardSimilarity(class1.DistinctUsedNamespaceFqns, class2.DistinctUsedNamespaceFqns);

        double methodMatchingSimilarity = 0.0;
        if (class1.MethodFeatures.Any() && class2.MethodFeatures.Any())
        {
            List<MethodSemanticFeatures> smallerList =
                class1.MethodFeatures.Count < class2.MethodFeatures.Count
                    ? class1.MethodFeatures
                    : class2.MethodFeatures;
            List<MethodSemanticFeatures> largerList =
                class1.MethodFeatures.Count < class2.MethodFeatures.Count
                    ? class2.MethodFeatures
                    : class1.MethodFeatures;
            double totalMaxSimilarity = 0.0;
            HashSet<int> usedLargerListIndices = [];

            foreach (MethodSemanticFeatures method1Feat in smallerList)
            {
                double maxSimForMethod1 = 0.0;
                int bestMatchIndex = -1;
                for (int k = 0; k < largerList.Count; k++)
                {
                    if (usedLargerListIndices.Contains(k))
                    {
                        continue;
                    }

                    double sim = CalculateSimilarity(method1Feat, largerList[k]);
                    if (sim > maxSimForMethod1)
                    {
                        maxSimForMethod1 = sim;
                        bestMatchIndex = k;
                    }
                }

                if (bestMatchIndex != -1)
                {
                    totalMaxSimilarity += maxSimForMethod1;
                    usedLargerListIndices.Add(bestMatchIndex);
                }
            }

            methodMatchingSimilarity = smallerList.Any() ? totalMaxSimilarity / smallerList.Count : 1.0;
        }
        else if (class1.MethodFeatures.Any() == false && class2.MethodFeatures.Any() == false)
        {
            methodMatchingSimilarity = 1.0;
        }

        double totalWeightedScore =
            baseClassSimilarity * Tuning.ClassWeights.BaseClassName +
            interfaceSimilarity * Tuning.ClassWeights.ImplementedInterfaceNames +
            referencedTypesSimilarity * Tuning.ClassWeights.DistinctReferencedExternalTypeFqns +
            usedNamespacesSimilarity * Tuning.ClassWeights.DistinctUsedNamespaceFqns +
            methodMatchingSimilarity * Tuning.ClassWeights.MethodMatchingSimilarity +
            (1.0 - CalculateNormalizedDifference(
                class1.PublicMethodCount,
                class2.PublicMethodCount,
                Tuning.ClassNormalization.MaxMethodCount)) * Tuning.ClassWeights.PublicMethodCount +
            (1.0 - CalculateNormalizedDifference(
                class1.ProtectedMethodCount,
                class2.ProtectedMethodCount,
                Tuning.ClassNormalization.MaxMethodCount)) * Tuning.ClassWeights.ProtectedMethodCount +
            (1.0 - CalculateNormalizedDifference(
                class1.PrivateMethodCount,
                class2.PrivateMethodCount,
                Tuning.ClassNormalization.MaxMethodCount)) * Tuning.ClassWeights.PrivateMethodCount +
            (1.0 - CalculateNormalizedDifference(
                class1.StaticMethodCount,
                class2.StaticMethodCount,
                Tuning.ClassNormalization.MaxMethodCount)) * Tuning.ClassWeights.StaticMethodCount +
            (1.0 - CalculateNormalizedDifference(
                class1.AbstractMethodCount,
                class2.AbstractMethodCount,
                Tuning.ClassNormalization.MaxMethodCount)) * Tuning.ClassWeights.AbstractMethodCount +
            (1.0 - CalculateNormalizedDifference(
                class1.VirtualMethodCount,
                class2.VirtualMethodCount,
                Tuning.ClassNormalization.MaxMethodCount)) * Tuning.ClassWeights.VirtualMethodCount +
            (1.0 - CalculateNormalizedDifference(
                class1.PropertyCount,
                class2.PropertyCount,
                Tuning.ClassNormalization.MaxPropertyCount)) * Tuning.ClassWeights.PropertyCount +
            (1.0 - CalculateNormalizedDifference(
                class1.ReadOnlyPropertyCount,
                class2.ReadOnlyPropertyCount,
                Tuning.ClassNormalization.MaxPropertyCount)) * Tuning.ClassWeights.ReadOnlyPropertyCount +
            (1.0 - CalculateNormalizedDifference(
                class1.StaticPropertyCount,
                class2.StaticPropertyCount,
                Tuning.ClassNormalization.MaxPropertyCount)) * Tuning.ClassWeights.StaticPropertyCount +
            (1.0 - CalculateNormalizedDifference(
                class1.FieldCount,
                class2.FieldCount,
                Tuning.ClassNormalization.MaxFieldCount)) * Tuning.ClassWeights.FieldCount +
            (1.0 - CalculateNormalizedDifference(
                class1.StaticFieldCount,
                class2.StaticFieldCount,
                Tuning.ClassNormalization.MaxFieldCount)) * Tuning.ClassWeights.StaticFieldCount +
            (1.0 - CalculateNormalizedDifference(
                class1.ReadonlyFieldCount,
                class2.ReadonlyFieldCount,
                Tuning.ClassNormalization.MaxFieldCount)) * Tuning.ClassWeights.ReadonlyFieldCount +
            (1.0 - CalculateNormalizedDifference(
                class1.ConstFieldCount,
                class2.ConstFieldCount,
                Tuning.ClassNormalization.MaxFieldCount)) * Tuning.ClassWeights.ConstFieldCount +
            (1.0 - CalculateNormalizedDifference(class1.EventCount, class2.EventCount, 20))
                * Tuning.ClassWeights.EventCount +
            (1.0 - CalculateNormalizedDifference(class1.NestedClassCount, class2.NestedClassCount, 10))
                * Tuning.ClassWeights.NestedClassCount +
            (1.0 - CalculateNormalizedDifference(class1.NestedStructCount, class2.NestedStructCount, 10))
                * Tuning.ClassWeights.NestedStructCount +
            (1.0 - CalculateNormalizedDifference(class1.NestedEnumCount, class2.NestedEnumCount, 10))
                * Tuning.ClassWeights.NestedEnumCount +
            (1.0 - CalculateNormalizedDifference(
                class1.NestedInterfaceCount, class2.NestedInterfaceCount, 10))
                * Tuning.ClassWeights.NestedInterfaceCount +
            (1.0 - CalculateNormalizedDifference(
                class1.AverageMethodComplexity,
                class2.AverageMethodComplexity,
                Tuning.ClassNormalization.MaxAverageMethodComplexity))
                * Tuning.ClassWeights.AverageMethodComplexity +
            (1.0 - CalculateNormalizedDifference(class1.TotalLinesOfCode, class2.TotalLinesOfCode, 2000))
                * Tuning.ClassWeights.TotalLinesOfCode;

        double totalWeight = Tuning.ClassWeights.TotalWeight;
        return totalWeight > 0 ? totalWeightedScore / totalWeight : 0.0;
    }

    private double CalculateJaccardSimilarity<T>(ICollection<T> set1, ICollection<T> set2)
    {
        if (set1.Any() == false && set2.Any() == false)
        {
            return 1.0;
        }

        if (set1.Any() == false || set2.Any() == false)
        {
            return 0.0;
        }

        int intersection = set1.Intersect(set2).Count();
        int union = set1.Union(set2).Count();
        return union > 0 ? (double)intersection / union : 0.0;
    }

    private double CalculateNormalizedDifference(double val1, double val2, double maxValue)
    {
        if (maxValue == 0.0)
        {
            return (val1 == val2) ? 0.0 : 1.0;
        }

        double diff = Math.Abs(val1 - val2);
        return diff / maxValue;
    }

    private void AddTypeAndNamespaceIfExternal(
        ITypeSymbol? typeSymbol,
        INamedTypeSymbol containingClassSymbol,
        HashSet<string> externalTypeFqns,
        HashSet<string> usedNamespaceFqns)
    {
        if (typeSymbol == null
            || typeSymbol.TypeKind == TypeKind.Error
            || typeSymbol.SpecialType == SpecialType.System_Void)
        {
            return;
        }

        if (typeSymbol.ContainingNamespace != null
            && typeSymbol.ContainingNamespace.IsGlobalNamespace == false)
        {
            usedNamespaceFqns.Add(
                typeSymbol.ContainingNamespace.ToDisplayString(
                    ToolHelpers.FullyQualifiedFormatWithoutGlobal));
        }

        if (typeSymbol.ContainingAssembly != null
            && containingClassSymbol.ContainingAssembly != null
            && SymbolEqualityComparer.Default.Equals(
                typeSymbol.ContainingAssembly, containingClassSymbol.ContainingAssembly) == false)
        {
            externalTypeFqns.Add(
                typeSymbol.OriginalDefinition.ToDisplayString(
                    ToolHelpers.FullyQualifiedFormatWithoutGlobal));
        }

        if (typeSymbol is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.IsGenericType)
        {
            foreach (ITypeSymbol typeArg in namedTypeSymbol.TypeArguments)
            {
                AddTypeAndNamespaceIfExternal(
                    typeArg, containingClassSymbol, externalTypeFqns, usedNamespaceFqns);
            }
        }

        if (typeSymbol is IArrayTypeSymbol arrayTypeSymbol)
        {
            AddTypeAndNamespaceIfExternal(
                arrayTypeSymbol.ElementType, containingClassSymbol, externalTypeFqns, usedNamespaceFqns);
        }

        if (typeSymbol is IPointerTypeSymbol pointerTypeSymbol)
        {
            AddTypeAndNamespaceIfExternal(
                pointerTypeSymbol.PointedAtType, containingClassSymbol, externalTypeFqns, usedNamespaceFqns);
        }
    }
}
