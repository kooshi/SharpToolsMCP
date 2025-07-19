
namespace SharpTools.Tools.Services;

public record ClassSemanticFeatures(
    string FullyQualifiedClassName,
    string FilePath,
    int StartLine,
    string ClassName,
    string? BaseClassName,
    List<string> ImplementedInterfaceNames,
    int PublicMethodCount,
    int ProtectedMethodCount,
    int PrivateMethodCount,
    int StaticMethodCount,
    int AbstractMethodCount,
    int VirtualMethodCount,
    int PropertyCount,
    int ReadOnlyPropertyCount,
    int StaticPropertyCount,
    int FieldCount,
    int StaticFieldCount,
    int ReadonlyFieldCount,
    int ConstFieldCount,
    int EventCount,
    int NestedClassCount,
    int NestedStructCount,
    int NestedEnumCount,
    int NestedInterfaceCount,
    double AverageMethodComplexity,
    HashSet<string> DistinctReferencedExternalTypeFqns,
    HashSet<string> DistinctUsedNamespaceFqns,
    int TotalLinesOfCode,
    List<MethodSemanticFeatures> MethodFeatures // Added for inter-class method similarity
);
