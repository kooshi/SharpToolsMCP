
using Microsoft.CodeAnalysis; // Keep for potential future use, but not strictly needed for current properties

namespace SharpTools.Tools.Services {
    public class MethodSemanticFeatures {
        // Store the fully qualified name instead of the IMethodSymbol object
        public string FullyQualifiedMethodName { get; }
        public string FilePath { get; }
        public int StartLine { get; }
        public string MethodName { get; }

        // Signature Features
        public string ReturnTypeName { get; }
        public List<string> ParameterTypeNames { get; }

        // Invocation Features
        public HashSet<string> InvokedMethodSignatures { get; }

        // CFG Features
        public int BasicBlockCount { get; }
        public int ConditionalBranchCount { get; }
        public int LoopCount { get; }
        public int CyclomaticComplexity { get; }

        // IOperation Features
        public Dictionary<string, int> OperationCounts { get; }
        public HashSet<string> DistinctAccessedMemberTypes { get; }


        public MethodSemanticFeatures(
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
            HashSet<string> distinctAccessedMemberTypes) {
            FullyQualifiedMethodName = fullyQualifiedMethodName;
            FilePath = filePath;
            StartLine = startLine;
            MethodName = methodName;
            ReturnTypeName = returnTypeName;
            ParameterTypeNames = parameterTypeNames;
            InvokedMethodSignatures = invokedMethodSignatures;
            BasicBlockCount = basicBlockCount;
            ConditionalBranchCount = conditionalBranchCount;
            LoopCount = loopCount;
            CyclomaticComplexity = cyclomaticComplexity;
            OperationCounts = operationCounts;
            DistinctAccessedMemberTypes = distinctAccessedMemberTypes;
        }
    }
}
