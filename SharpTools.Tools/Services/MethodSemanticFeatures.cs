namespace SharpTools.Tools.Services;

public class MethodSemanticFeatures(
    string fullyQualifiedMethodName, // Changed from IMethodSymbol
    string filePath,
    int startLine,
    string methodName,
    string returnTypeName,
    List<string> parameterTypeNames,
    HashSet<string> invokedMethodSignatures,
    int basicBlockCount,
    int conditionalBranchCount,
    int loopCount,
    int cyclomaticComplexity,
    Dictionary<string, int> operationCounts,
    HashSet<string> distinctAccessedMemberTypes)
{
    // Store the fully qualified name instead of the IMethodSymbol object
    public string FullyQualifiedMethodName { get; } = fullyQualifiedMethodName;
    public string FilePath { get; } = filePath;
    public int StartLine { get; } = startLine;
    public string MethodName { get; } = methodName;

    // Signature Features
    public string ReturnTypeName { get; } = returnTypeName;
    public List<string> ParameterTypeNames { get; } = parameterTypeNames;

    // Invocation Features
    public HashSet<string> InvokedMethodSignatures { get; } = invokedMethodSignatures;

    // CFG Features
    public int BasicBlockCount { get; } = basicBlockCount;
    public int ConditionalBranchCount { get; } = conditionalBranchCount;
    public int LoopCount { get; } = loopCount;
    public int CyclomaticComplexity { get; } = cyclomaticComplexity;

    // IOperation Features
    public Dictionary<string, int> OperationCounts { get; } = operationCounts;
    public HashSet<string> DistinctAccessedMemberTypes { get; } = distinctAccessedMemberTypes;
}
