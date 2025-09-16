using System.Text;
using DiffPlex.DiffBuilder;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using SharpTools.Tools.Interfaces;
using SharpTools.Tools.Mcp.Tools;
namespace SharpTools.Tools.Mcp;
/// <summary>
/// Provides reusable context injection methods for checking compilation errors and generating diffs.
/// These methods are used across various tools to provide consistent feedback.
/// </summary>
internal static class ContextInjectors {
    /// <summary>
    /// Checks for compilation errors in a document after code has been modified.
    /// </summary>
    /// <param name="solutionManager">The solution manager</param>
    /// <param name="document">The document to check for errors</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A tuple containing (hasErrors, errorMessages)</returns>
    public static async Task<(bool HasErrors, string ErrorMessages)> CheckCompilationErrorsAsync<TLogCategory>(
    ISolutionManager solutionManager,
    Document document,
    ILogger<TLogCategory> logger,
    CancellationToken cancellationToken) {
        if (document == null) {
            logger.LogWarning("Cannot check for compilation errors: Document is null");
            return (false, string.Empty);
        }
        try {
            // Get the project containing this document
            var project = document.Project;
            if (project == null) {
                logger.LogWarning("Cannot check for compilation errors: Project not found for document {FilePath}",
                document.FilePath ?? "unknown");
                return (false, string.Empty);
            }
            // Get compilation for the project
            var compilation = await solutionManager.GetCompilationAsync(project.Id, cancellationToken);
            if (compilation == null) {
                logger.LogWarning("Cannot check for compilation errors: Compilation not available for project {ProjectName}",
                project.Name);
                return (false, string.Empty);
            }
            // Get syntax tree for the document
            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (syntaxTree == null) {
                logger.LogWarning("Cannot check for compilation errors: Syntax tree not available for document {FilePath}",
                document.FilePath ?? "unknown");
                return (false, string.Empty);
            }
            // Get semantic model
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            // Get all diagnostics for the specific syntax tree
            var diagnostics = semanticModel.GetDiagnostics(cancellationToken: cancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error || d.Severity == DiagnosticSeverity.Warning)
            .OrderByDescending(d => d.Severity)  // Errors first, then warnings
            .ThenBy(d => d.Location.SourceSpan.Start)
            .ToList();
            if (!diagnostics.Any())
                return (false, string.Empty);
            // Focus specifically on member access errors
            var memberAccessErrors = diagnostics
            .Where(d => d.Id == "CS0103" || d.Id == "CS1061" || d.Id == "CS0117" || d.Id == "CS0246")
            .ToList();
            // Build error message
            var sb = new StringBuilder();
            sb.AppendLine($"<compilationErrors note=\"If the fixes for these errors are simple, use `{ToolHelpers.SharpToolPrefix}{nameof(ModificationTools.FindAndReplace)}`\">");
            // First add member access errors (highest priority as this is what we're focusing on)
            foreach (var error in memberAccessErrors) {
                var lineSpan = error.Location.GetLineSpan();
                sb.AppendLine($"  {error.Severity}: {error.Id} - {error.GetMessage()} at line {lineSpan.StartLinePosition.Line + 1}, column {lineSpan.StartLinePosition.Character + 1}");
            }
            // Then add other errors and warnings
            foreach (var diag in diagnostics.Except(memberAccessErrors)) {
                var lineSpan = diag.Location.GetLineSpan();
                sb.AppendLine($"  {diag.Severity}: {diag.Id} - {diag.GetMessage()} at line {lineSpan.StartLinePosition.Line + 1}, column {lineSpan.StartLinePosition.Character + 1}");
            }
            sb.AppendLine("</compilationErrors>");
            logger.LogWarning("Compilation issues found in {FilePath}:\n{Errors}",
            document.FilePath ?? "unknown", sb.ToString());
            return (true, sb.ToString());
        } catch (Exception ex) when (!(ex is OperationCanceledException)) {
            logger.LogError(ex, "Error checking for compilation errors in document {FilePath}",
            document.FilePath ?? "unknown");
            return (false, $"Error checking for compilation errors: {ex.Message}");
        }
    }
    /// <summary>
    /// Creates a pretty diff between old and new code, with whitespace and formatting normalized
    /// </summary>
    /// <param name="oldCode">The original code</param>
    /// <param name="newCode">The updated code</param>
    /// <param name="includeContextMessage">Whether to include context message about diff being applied</param>
    /// <returns>Formatted diff as a string</returns>
    public static string CreateCodeDiff(string oldCode, string newCode) {
        // Helper function to trim lines for cleaner diff
        static string trimLines(string code) =>
        string.Join("\n", code.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .Where(line => !string.IsNullOrWhiteSpace(line)));
        string strippedOldCode = trimLines(oldCode);
        string strippedNewCode = trimLines(newCode);
        var diff = InlineDiffBuilder.Diff(strippedOldCode, strippedNewCode);
        var diffBuilder = new StringBuilder();
        bool inUnchangedSection = false;
        foreach (var line in diff.Lines) {
            switch (line.Type) {
                case DiffPlex.DiffBuilder.Model.ChangeType.Inserted:
                    diffBuilder.AppendLine($"+ {line.Text}");
                    inUnchangedSection = false;
                    break;
                case DiffPlex.DiffBuilder.Model.ChangeType.Deleted:
                    diffBuilder.AppendLine($"- {line.Text}");
                    inUnchangedSection = false;
                    break;
                case DiffPlex.DiffBuilder.Model.ChangeType.Unchanged:
                    if (!inUnchangedSection) {
                        diffBuilder.AppendLine("// ...existing code unchanged...");
                        inUnchangedSection = true;
                    }
                    break;
            }
        }
        var diffResult = diffBuilder.ToString();
        if (string.IsNullOrWhiteSpace(diffResult) || diff.Lines.All(l => l.Type == DiffPlex.DiffBuilder.Model.ChangeType.Unchanged)) {
            diffResult = "<diff>\n// No changes detected.\n</diff>";
        } else {
            diffResult = $"<diff>\n{diffResult}\n</diff>\nNote: This diff has been applied. You must base all future changes on the updated code.";
        }
        return diffResult;
    }
    /// <summary>
    /// Creates a diff between old and new document text
    /// </summary>
    /// <param name="oldDocument">The original document</param>
    /// <param name="newDocument">The updated document</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Formatted diff as a string</returns>
    public static async Task<string> CreateDocumentDiff(Document oldDocument, Document newDocument, CancellationToken cancellationToken) {
        if (oldDocument == null || newDocument == null) {
            return "// Could not generate diff: One or both documents are null.";
        }
        var oldText = await oldDocument.GetTextAsync(cancellationToken);
        var newText = await newDocument.GetTextAsync(cancellationToken);
        return CreateCodeDiff(oldText.ToString(), newText.ToString());
    }
    /// <summary>
    /// Creates a multi-document diff for a collection of changed documents
    /// </summary>
    /// <param name="originalSolution">The original solution</param>
    /// <param name="newSolution">The updated solution</param>
    /// <param name="changedDocuments">List of document IDs that were changed</param>
    /// <param name="maxDocuments">Maximum number of documents to include in the diff</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Formatted diff as a string, including file names</returns>
    public static async Task<string> CreateMultiDocumentDiff(
    Solution originalSolution,
    Solution newSolution,
    IReadOnlyList<DocumentId> changedDocuments,
    int maxDocuments = 5,
    CancellationToken cancellationToken = default) {
        if (changedDocuments.Count == 0) {
            return "No documents changed.";
        }
        var sb = new StringBuilder();
        sb.AppendLine($"Changes in {Math.Min(changedDocuments.Count, maxDocuments)} documents:");
        int count = 0;
        foreach (var docId in changedDocuments) {
            if (count >= maxDocuments) {
                sb.AppendLine($"...and {changedDocuments.Count - maxDocuments} more documents");
                break;
            }
            var oldDoc = originalSolution.GetDocument(docId);
            var newDoc = newSolution.GetDocument(docId);
            if (oldDoc == null || newDoc == null) {
                continue;
            }
            sb.AppendLine();
            sb.AppendLine($"Document: {oldDoc.FilePath}");
            sb.AppendLine(await CreateDocumentDiff(oldDoc, newDoc, cancellationToken));
            count++;
        }
        return sb.ToString();
    }
    public static async Task<string> CreateCallGraphContextAsync<TLogCategory>(
        ICodeAnalysisService codeAnalysisService,
        ILogger<TLogCategory> logger,
        IMethodSymbol methodSymbol,
        CancellationToken cancellationToken) {
        if (methodSymbol == null) {
            return "Method symbol is null.";
        }

        var callers = new HashSet<string>();
        var callees = new HashSet<string>();

        try {
            // Get incoming calls (callers)
            var callerInfos = await codeAnalysisService.FindCallersAsync(methodSymbol, cancellationToken);
            foreach (var callerInfo in callerInfos) {
                cancellationToken.ThrowIfCancellationRequested();
                if (callerInfo.CallingSymbol is IMethodSymbol callingMethodSymbol) {
                    // We still show all callers, since this is important for analysis
                    string callerFqn = FuzzyFqnLookupService.GetSearchableString(callingMethodSymbol);
                    callers.Add(callerFqn);
                }
            }

            // Get outgoing calls (callees)
            var outgoingSymbols = await codeAnalysisService.FindOutgoingCallsAsync(methodSymbol, cancellationToken);
            foreach (var callee in outgoingSymbols) {
                cancellationToken.ThrowIfCancellationRequested();
                if (callee is IMethodSymbol calleeMethodSymbol) {
                    // Only include callees that are defined within the solution
                    if (IsSymbolInSolution(calleeMethodSymbol)) {
                        string calleeFqn = FuzzyFqnLookupService.GetSearchableString(calleeMethodSymbol);
                        callees.Add(calleeFqn);
                    }
                }
            }
        } catch (Exception ex) when (!(ex is OperationCanceledException)) {
            logger.LogWarning(ex, "Error creating call graph context for method {MethodName}", methodSymbol.Name);
            return $"Error creating call graph: {ex.Message}";
        }

        // Format results in XML format
        var random = new Random();
        var result = new StringBuilder();
        result.AppendLine("<callers>");
        var randomizedCallers = callers.OrderBy(_ => random.Next()).Take(20);
        foreach (var caller in randomizedCallers) {
            result.AppendLine(caller);
        }
        if (callers.Count > 20) {
            result.AppendLine($"<!-- {callers.Count - 20} more callers not shown -->");
        }
        result.AppendLine("</callers>");
        result.AppendLine("<callees>");
        foreach (var callee in callees.OrderBy(_ => random.Next()).Take(20)) {
            result.AppendLine(callee);
        }
        if (callees.Count > 20) {
            result.AppendLine($"<!-- {callees.Count - 20} more callees not shown -->");
        }
        result.AppendLine("</callees>");

        return result.ToString();
    }
    public static async Task<string> CreateTypeReferenceContextAsync<TLogCategory>(
        ICodeAnalysisService codeAnalysisService,
        ILogger<TLogCategory> logger,
        INamedTypeSymbol typeSymbol,
        CancellationToken cancellationToken) {
        if (typeSymbol == null) {
            return "Type symbol is null.";
        }

        var referencingTypes = new HashSet<string>();
        var referencedTypes = new HashSet<string>(StringComparer.Ordinal);

        try {
            // Get referencing types (types that reference this type)
            var references = await codeAnalysisService.FindReferencesAsync(typeSymbol, cancellationToken);
            foreach (var reference in references) {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var location in reference.Locations) {
                    if (location.Document == null || location.Location == null) {
                        continue;
                    }

                    var semanticModel = await location.Document.GetSemanticModelAsync(cancellationToken);
                    if (semanticModel == null) {
                        continue;
                    }

                    var symbol = semanticModel.GetEnclosingSymbol(location.Location.SourceSpan.Start, cancellationToken);
                    while (symbol != null && !(symbol is INamedTypeSymbol)) {
                        symbol = symbol.ContainingSymbol;
                    }

                    if (symbol is INamedTypeSymbol referencingType &&
                        !SymbolEqualityComparer.Default.Equals(referencingType, typeSymbol)) {
                        // We still include all referencing types, since this is important for analysis
                        string referencingTypeFqn = FuzzyFqnLookupService.GetSearchableString(referencingType);
                        referencingTypes.Add(referencingTypeFqn);
                    }
                }
            }

            // Get referenced types (types this type references in implementations)
            // This was moved to CodeAnalysisService.FindReferencedTypesAsync
            referencedTypes = await codeAnalysisService.FindReferencedTypesAsync(typeSymbol, cancellationToken);
        } catch (Exception ex) when (!(ex is OperationCanceledException)) {
            logger.LogWarning(ex, "Error creating type reference context for type {TypeName}", typeSymbol.Name);
            return $"Error creating type reference context: {ex.Message}";
        }

        // Format results in XML format
        var random = new Random();
        var result = new StringBuilder();
        result.AppendLine("<referencingTypes>");
        foreach (var referencingType in referencingTypes.OrderBy(t => random.Next()).Take(20)) {
            result.AppendLine(referencingType);
        }
        if (referencingTypes.Count > 20) {
            result.AppendLine($"<!-- {referencingTypes.Count - 20} more referencing types not shown -->");
        }
        result.AppendLine("</referencingTypes>");
        result.AppendLine("<referencedTypes>");
        foreach (var referencedType in referencedTypes.OrderBy(t => random.Next()).Take(20)) {
            result.AppendLine(referencedType);
        }
        if (referencedTypes.Count > 20) {
            result.AppendLine($"<!-- {referencedTypes.Count - 20} more referenced types not shown -->");
        }
        result.AppendLine("</referencedTypes>");

        return result.ToString();
    }/// <summary>
     /// Determines if a symbol is defined within the current solution.
     /// </summary>
     /// <param name="symbol">The symbol to check</param>
     /// <returns>True if the symbol is defined within the solution, false otherwise</returns>
    private static bool IsSymbolInSolution(ISymbol symbol) {
        if (symbol == null) {
            return false;
        }

        // Get the containing assembly of the symbol
        var assembly = symbol.ContainingAssembly;
        if (assembly == null) {
            return false;
        }

        // Check if the assembly is from source code (part of the solution)
        // Assemblies in the solution have source code locations, while referenced assemblies don't
        return assembly.Locations.Any(loc => loc.IsInSource);
    }
}