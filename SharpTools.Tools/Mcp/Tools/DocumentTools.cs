using ModelContextProtocol;

namespace SharpTools.Tools.Mcp.Tools;

// Marker class for ILogger<T> category specific to DocumentTools
public class DocumentToolsLogCategory { }

[McpServerToolType]
public static class DocumentTools {

    private static string previousFilePathWarned = string.Empty;

    [McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(ReadRawFromRoslynDocument), Idempotent = true, ReadOnly = true, Destructive = false, OpenWorld = false),
    Description("Reads the content of a file in the solution or referenced directories. Omits indentation to save tokens.")]
    public static async Task<string> ReadRawFromRoslynDocument(
        ISolutionManager solutionManager,
        IDocumentOperationsService documentOperations,
        ILogger<DocumentToolsLogCategory> logger,
        [Description("The absolute path to the file to read.")] string filePath,
        CancellationToken cancellationToken = default) {

        const int LineCountWarningThreshold = 1000;

        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () => {
            ErrorHandlingHelpers.ValidateStringParameter(filePath, "filePath", logger);
            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(ReadRawFromRoslynDocument));

            logger.LogInformation("Reading document at {FilePath}", filePath);

            if (!documentOperations.FileExists(filePath)) {
                throw new McpException($"File not found: {filePath}");
            }

            if (!documentOperations.IsPathReadable(filePath)) {
                throw new McpException($"File exists but cannot be read: {filePath}");
            }

            try {
                var (contents, lines) = await documentOperations.ReadFileAsync(filePath, true, cancellationToken);

                if (lines > LineCountWarningThreshold) {
                    if (previousFilePathWarned != filePath) {
                        previousFilePathWarned = filePath;

                        throw new McpException(
                            $"WARNING: '{filePath}' is very long (over {LineCountWarningThreshold} lines). " +
                            "Consider using more focused tools to accomplish your task, " +
                            "or call this tool again with the same arguments to override this warning.");
                    }
                    previousFilePathWarned = string.Empty;
                    logger.LogInformation("Proceeding with reading large file ({LineCount} lines) after warning acknowledgment", lines);
                }

                return contents;
            } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
                logger.LogError(ex, "File not found: {FilePath}", filePath);
                throw new McpException($"File not found: {filePath}");
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) {
                logger.LogError(ex, "Failed to read file due to access restrictions: {FilePath}", filePath);
                throw new McpException($"Failed to read file due to access restrictions: {ex.Message}");
            } catch (Exception ex) when (!(ex is McpException || ex is OperationCanceledException)) {
                logger.LogError(ex, "Unexpected error reading file: {FilePath}", filePath);
                throw new McpException($"Failed to read file: {ex.Message}");
            }
        }, logger, nameof(ReadRawFromRoslynDocument), cancellationToken);
    }
    [McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(CreateRoslynDocument), Idempotent = true, ReadOnly = false, Destructive = false, OpenWorld = false),
            Description("Creates a new document file with the specified content. Returns error if the file already exists.")]
    public static async Task<string> CreateRoslynDocument(
                ISolutionManager solutionManager,
                IDocumentOperationsService documentOperations,
                ICodeModificationService codeModificationService,
                ILogger<DocumentToolsLogCategory> logger,
                [Description("The absolute path where the file should be created.")] string filePath,
                [Description("The content to write to the file. For C#, omit indentation to save tokens. Code will be auto-formatted.")] string content,
                string commitMessage,
                CancellationToken cancellationToken = default) {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () => {
            ErrorHandlingHelpers.ValidateStringParameter(filePath, "filePath", logger);
            ErrorHandlingHelpers.ValidateStringParameter(content, "content", logger);
            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(CreateRoslynDocument));
            content = content.TrimBackslash();

            logger.LogInformation("Creating new document at {FilePath}", filePath);

            // Check if file exists
            if (documentOperations.FileExists(filePath)) {
                throw new McpException($"File already exists at {filePath}. Use '{ToolHelpers.SharpToolPrefix}{nameof(ReadRawFromRoslynDocument)}' to understand its contents. Then you can use '{ToolHelpers.SharpToolPrefix}{nameof(OverwriteRoslynDocument)}' if you decide to overwrite what exists.");
            }

            // Check if path is writable
            if (!documentOperations.IsPathWritable(filePath)) {
                throw new McpException($"Cannot create file at {filePath}. Path is not writable.");
            }

            string finalCommitMessage = $"Create {Path.GetFileName(filePath)}: " + commitMessage;
            try {
                // Determine if it's a code file that should be tracked by Roslyn
                bool isCodeFile = documentOperations.IsCodeFile(filePath);

                // Write the file content
                var success = await documentOperations.WriteFileAsync(filePath, content, false, cancellationToken, finalCommitMessage);
                if (!success) {
                    throw new McpException($"Failed to create file at {filePath} for unknown reasons.");
                }

                // Get the current solution path
                var solutionPath = solutionManager.CurrentSolution?.FilePath;
                if (string.IsNullOrEmpty(solutionPath)) {
                    return $"Created file {filePath} but no solution is loaded";
                }

                // If it's not a code file, just return success
                if (!isCodeFile) {
                    return $"Created non-code file {filePath}";
                }

                // For code files, check if it was added to the solution
                var documents = solutionManager.CurrentSolution?.Projects
                    .SelectMany(p => p.Documents)
                    .Where(d => d.FilePath != null &&
                        d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var projectStatus = "but was not detected by any project";

                if (documents?.Any() == true) {
                    var document = documents.First();
                    projectStatus = $"and was added to project {document.Project.Name}";

                    // Check for compilation errors
                    var (hasErrors, errorMessages) = await ContextInjectors.CheckCompilationErrorsAsync(
                        solutionManager, document, logger, cancellationToken);

                    if (hasErrors) {
                        return $"Created file {filePath} ({projectStatus}), but compilation issues were detected:\n\n{errorMessages}";
                    }
                }
                return $"Created file {filePath} {projectStatus}";
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) {
                logger.LogError(ex, "Failed to create file due to IO or access restrictions: {FilePath}", filePath);
                throw new McpException($"Failed to create file due to IO or access restrictions: {ex.Message}");
            } catch (Exception ex) when (!(ex is McpException || ex is OperationCanceledException)) {
                logger.LogError(ex, "Unexpected error creating file: {FilePath}", filePath);
                throw new McpException($"Failed to create file: {ex.Message}");
            }
        }, logger, nameof(CreateRoslynDocument), cancellationToken);
    }
    [McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(OverwriteRoslynDocument), Idempotent = true, ReadOnly = false, Destructive = true, OpenWorld = false),
    Description($"Overwrites an existing document file with the specified content. You must use {ToolHelpers.SharpToolPrefix}{nameof(ReadRawFromRoslynDocument)} first.")]
    public static async Task<string> OverwriteRoslynDocument(
        ISolutionManager solutionManager,
        IDocumentOperationsService documentOperations,
        ICodeModificationService codeModificationService,
        ILogger<DocumentToolsLogCategory> logger,
        [Description("The absolute path to the file to overwrite.")] string filePath,
        [Description("The content to write to the file. For C#, omit indentation to save tokens. Code will be auto-formatted.")] string content,
        string commitMessage,
        CancellationToken cancellationToken = default) {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () => {
            ErrorHandlingHelpers.ValidateStringParameter(filePath, "filePath", logger);
            ErrorHandlingHelpers.ValidateStringParameter(content, "content", logger);
            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(OverwriteRoslynDocument));
            content = content.TrimBackslash();
            logger.LogInformation("Overwriting document at {FilePath}", filePath);

            // Check if path is writable
            if (!documentOperations.IsPathWritable(filePath)) {
                throw new McpException($"Cannot write to file at {filePath}. Path is not writable.");
            }

            string finalCommitMessage = $"Update {Path.GetFileName(filePath)}: " + commitMessage;
            try {
                // Read the original content for diff
                string originalContent = "";
                if (documentOperations.FileExists(filePath)) {
                    (originalContent, _) = await documentOperations.ReadFileAsync(filePath, false, cancellationToken);
                }

                // Determine if it's a code file that should be handled by Roslyn
                bool isCodeFile = documentOperations.IsCodeFile(filePath);

                // Check if the file is already in the solution
                Document? existingDocument = null;
                if (isCodeFile) {
                    existingDocument = solutionManager.CurrentSolution?.Projects
                        .SelectMany(p => p.Documents)
                        .FirstOrDefault(d => d.FilePath != null &&
                            d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                }

                string resultMessage;
                if (existingDocument != null) {
                    logger.LogInformation("Document found in solution, updating through workspace API: {FilePath}", filePath);

                    // Create a new document text with the updated content
                    var newText = SourceText.From(content);
                    var newDocument = existingDocument.WithText(newText);
                    var solution = newDocument.Project.Solution;

                    // Apply the changes to the workspace using the code modification service with the commit message
                    await codeModificationService.ApplyChangesAsync(solution, cancellationToken, finalCommitMessage);

                    // Check for compilation errors
                    var (hasErrors, errorMessages) = await ContextInjectors.CheckCompilationErrorsAsync(
                        solutionManager, newDocument, logger, cancellationToken);

                    resultMessage = hasErrors ?
                        $"Overwrote file in project {existingDocument.Project.Name}, but compilation issues were detected:\n\n{errorMessages}" :
                        $"Overwrote file in project {existingDocument.Project.Name}";
                } else {
                    // For non-solution files or non-code files, use standard file writing
                    var success = await documentOperations.WriteFileAsync(filePath, content, true, cancellationToken, finalCommitMessage);
                    if (!success) {
                        throw new McpException($"Failed to overwrite file at {filePath} for unknown reasons.");
                    }

                    if (!isCodeFile) {
                        resultMessage = $"Overwrote non-code file {filePath}";
                    } else {
                        var projectStatus = "but was not detected by any project";

                        // Check if the file was added to a project after writing
                        var document = solutionManager.CurrentSolution?.Projects
                            .SelectMany(p => p.Documents)
                            .FirstOrDefault(d => d.FilePath != null &&
                                d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

                        if (document != null) {
                            projectStatus = $"and was detected in project {document.Project.Name}";

                            // Check for compilation errors for code files
                            var (hasErrors, errorMessages) = await ContextInjectors.CheckCompilationErrorsAsync(
                                solutionManager, document, logger, cancellationToken);

                            if (hasErrors) {
                                resultMessage = $"Overwrote file {filePath} ({projectStatus}), but compilation issues were detected:\n\n{errorMessages}";
                                return AddDiffToResult(resultMessage, originalContent, content);
                            }
                        }

                        resultMessage = $"Overwrote file {filePath} {projectStatus}";
                    }
                }

                // Generate and append the diff to the result
                return AddDiffToResult(resultMessage, originalContent, content);
            } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
                logger.LogError(ex, "File not found for overwriting: {FilePath}", filePath);
                throw new McpException($"File not found for overwriting: {filePath}");
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) {
                logger.LogError(ex, "Failed to overwrite file due to IO or access restrictions: {FilePath}", filePath);
                throw new McpException($"Failed to overwrite file due to IO or access restrictions: {ex.Message}");
            } catch (Exception ex) when (!(ex is McpException || ex is OperationCanceledException)) {
                logger.LogError(ex, "Unexpected error overwriting file: {FilePath}", filePath);
                throw new McpException($"Failed to overwrite file: {ex.Message}");
            }
        }, logger, nameof(OverwriteRoslynDocument), cancellationToken);
    }
    private static string AddDiffToResult(string resultMessage, string oldContent, string newContent) {
        // Use the centralized diff generation from ContextInjectors
        string diffResult = ContextInjectors.CreateCodeDiff(oldContent, newContent);
        return $"{resultMessage}\n{diffResult}";
    }
    // Helper method to determine if a file is a supported code file that should be checked for compilation errors
    private static bool IsSupportedCodeFile(string filePath) {
        if (string.IsNullOrEmpty(filePath)) {
            return false;
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch {
            ".cs" => true,    // C# files
            ".csx" => true,   // C# script files
            ".vb" => true,    // Visual Basic files
            ".fs" => true,    // F# files
            ".fsx" => true,   // F# script files
            ".fsi" => true,   // F# signature files
            _ => false
        };
    }
    [McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(ReadTypesFromRoslynDocument), Idempotent = true, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Returns a comprehensive tree of types (classes, interfaces, structs, etc.) and their members from a specified file. Use this to enter the more powerful 'type' domain from the 'file' domain.")]
    public static async Task<object> ReadTypesFromRoslynDocument(
                ISolutionManager solutionManager,
                IDocumentOperationsService documentOperations,
                ICodeAnalysisService codeAnalysisService,
                ILogger<DocumentToolsLogCategory> logger,
                [Description("The absolute path to the file to analyze.")] string filePath,
                CancellationToken cancellationToken) {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () => {
            ErrorHandlingHelpers.ValidateFilePath(filePath, logger);
            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(ReadTypesFromRoslynDocument));

            var pathInfo = documentOperations.GetPathInfo(filePath);
            if (!pathInfo.Exists) {
                throw new McpException($"File not found: {filePath}");
            }

            if (!pathInfo.IsReferencedBySolution) {
                throw new McpException($"File is not part of the solution: {filePath}");
            }

            var documentId = solutionManager.CurrentSolution?.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
            if (documentId == null) {
                throw new McpException($"Could not locate document in solution: {filePath}");
            }

            var sourceDoc = solutionManager.CurrentSolution?.GetDocument(documentId);
            if (sourceDoc == null) {
                throw new McpException($"Could not load document from solution: {filePath}");
            }

            var syntaxRoot = await sourceDoc.GetSyntaxRootAsync(cancellationToken);
            if (syntaxRoot == null) {
                throw new McpException($"Could not parse syntax tree for document: {filePath}");
            }

            var semanticModel = await sourceDoc.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null) {
                throw new McpException($"Could not get semantic model for document: {filePath}");
            }

            var typeNodes = syntaxRoot.DescendantNodes()
                .OfType<TypeDeclarationSyntax>();

            var result = new List<object>();
            foreach (var typeNode in typeNodes) {
                // Process only top-level types. Nested types are handled by BuildRoslynSubtypeTreeAsync.
                if (typeNode.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax) {
                    var declaredSymbol = semanticModel.GetDeclaredSymbol(typeNode, cancellationToken);

                    if (declaredSymbol is INamedTypeSymbol declaredNamedTypeSymbol) {
                        // It's crucial that the symbol passed to BuildRoslynSubtypeTreeAsync is from a compilation
                        // that has all necessary references, which FindRoslynNamedTypeSymbolAsync tries to ensure.
                        string fullyQualifiedName = declaredNamedTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        var resolvedNamedTypeSymbol = await solutionManager.FindRoslynNamedTypeSymbolAsync(fullyQualifiedName, cancellationToken);
                        INamedTypeSymbol symbolToProcess = resolvedNamedTypeSymbol ?? declaredNamedTypeSymbol;

                        // Ensure the symbol is not an error type and has a containing assembly.
                        // Symbols from GetDeclaredSymbol on a document not fully processed by a compilation might lack this.
                        if (symbolToProcess.TypeKind != TypeKind.Error && symbolToProcess.ContainingAssembly != null) {
                            result.Add(await AnalysisTools.BuildRoslynSubtypeTreeAsync(symbolToProcess, codeAnalysisService, cancellationToken));
                        } else if (resolvedNamedTypeSymbol == null && declaredNamedTypeSymbol.TypeKind != TypeKind.Error && declaredNamedTypeSymbol.ContainingAssembly != null) {
                            // Fallback to declared symbol if it's valid but resolution failed
                            logger.LogDebug("Using originally declared symbol for {SymbolName} as re-resolution failed but declared symbol appears valid.", fullyQualifiedName);
                            result.Add(await AnalysisTools.BuildRoslynSubtypeTreeAsync(declaredNamedTypeSymbol, codeAnalysisService, cancellationToken));
                        } else {
                            logger.LogWarning("Skipping symbol {SymbolName} from file {FilePath} as it could not be properly resolved to a valid named type symbol with an assembly context. Resolved: {ResolvedIsNull}, Declared TypeKind: {DeclaredTypeKind}, Declared AssemblyNull: {DeclaredAssemblyIsNull}",
                                fullyQualifiedName,
                                filePath,
                                resolvedNamedTypeSymbol == null,
                                declaredNamedTypeSymbol.TypeKind,
                                declaredNamedTypeSymbol.ContainingAssembly == null);
                        }
                    }
                }
            }

            return ToolHelpers.ToJson(result);
        }, logger, nameof(ReadTypesFromRoslynDocument), cancellationToken);
    }
}