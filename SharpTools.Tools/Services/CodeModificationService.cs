using SharpTools.Tools.Mcp;
using ModelContextProtocol;
using Microsoft.Extensions.FileSystemGlobbing;

namespace SharpTools.Tools.Services;

public class CodeModificationService : ICodeModificationService {
    private readonly ISolutionManager _solutionManager;
    private readonly IGitService _gitService;
    private readonly ILogger<CodeModificationService> _logger;

    public CodeModificationService(
        ISolutionManager solutionManager,
        IGitService gitService,
        ILogger<CodeModificationService> logger) {
        _solutionManager = solutionManager ?? throw new ArgumentNullException(nameof(solutionManager));
        _gitService = gitService ?? throw new ArgumentNullException(nameof(gitService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private Solution GetCurrentSolutionOrThrow() {
        if (!_solutionManager.IsSolutionLoaded) {
            throw new InvalidOperationException("No solution is currently loaded.");
        }
        return _solutionManager.CurrentSolution;
    }

    public async Task<Solution> AddMemberAsync(DocumentId documentId, INamedTypeSymbol targetTypeSymbol, MemberDeclarationSyntax newMember, int lineNumberHint = -1, CancellationToken cancellationToken = default) {
        var solution = GetCurrentSolutionOrThrow();
        var document = solution.GetDocument(documentId) ?? throw new ArgumentException($"Document with ID '{documentId}' not found in the current solution.", nameof(documentId));

        var typeDeclarationNode = targetTypeSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) as TypeDeclarationSyntax;
        if (typeDeclarationNode == null) {
            throw new InvalidOperationException($"Could not find syntax node for type '{targetTypeSymbol.Name}'.");
        }

        _logger.LogInformation("Adding member to type {TypeName} in document {DocumentPath}", targetTypeSymbol.Name, document.FilePath);

        NormalizeMemberDeclarationTrivia(newMember);

        if (lineNumberHint > 0) {
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root != null) {
                var sourceText = await document.GetTextAsync(cancellationToken);

                var members = typeDeclarationNode.Members
                    .Select(member => new {
                        Member = member,
                        LineSpan = member.GetLocation().GetLineSpan()
                    })
                    .OrderBy(m => m.LineSpan.StartLinePosition.Line)
                    .ToList();

                int insertIndex = 0;
                for (int i = 0; i < members.Count; i++) {
                    if (members[i].LineSpan.StartLinePosition.Line >= lineNumberHint) {
                        insertIndex = i;
                        break;
                    }
                    insertIndex = i + 1;
                }

                var editor = await DocumentEditor.CreateAsync(document, cancellationToken);
                var membersList = typeDeclarationNode.Members.ToList();
                membersList.Insert(insertIndex, newMember);
                var newTypeDeclaration = typeDeclarationNode.WithMembers(SyntaxFactory.List(membersList));

                editor.ReplaceNode(typeDeclarationNode, newTypeDeclaration);

                var newDocument = editor.GetChangedDocument();
                var formattedDocument = await FormatDocumentAsync(newDocument, cancellationToken);
                return formattedDocument.Project.Solution;
            }
        }

        var defaultEditor = await DocumentEditor.CreateAsync(document, cancellationToken);
        defaultEditor.AddMember(typeDeclarationNode, newMember);

        var changedDocument = defaultEditor.GetChangedDocument();
        var finalDocument = await FormatDocumentAsync(changedDocument, cancellationToken);
        return finalDocument.Project.Solution;
    }

    public async Task<Solution> AddStatementAsync(DocumentId documentId, MethodDeclarationSyntax targetMethodNode, StatementSyntax newStatement, CancellationToken cancellationToken, bool addToBeginning = false) {
        var solution = GetCurrentSolutionOrThrow();
        var document = solution.GetDocument(documentId) ?? throw new ArgumentException($"Document with ID '{documentId}' not found in the current solution.", nameof(documentId));

        _logger.LogInformation("Adding statement to method {MethodName} in document {DocumentPath}", targetMethodNode.Identifier.Text, document.FilePath);
        var editor = await DocumentEditor.CreateAsync(document, cancellationToken);

        if (targetMethodNode.Body != null) {
            var currentBody = targetMethodNode.Body;
            BlockSyntax newBody;
            if (addToBeginning) {
                var newStatements = currentBody.Statements.Insert(0, newStatement);
                newBody = currentBody.WithStatements(newStatements);
            } else {
                newBody = currentBody.AddStatements(newStatement);
            }
            editor.ReplaceNode(currentBody, newBody);
        } else if (targetMethodNode.ExpressionBody != null) {
            // Converting expression body to block body
            var returnStatement = SyntaxFactory.ReturnStatement(targetMethodNode.ExpressionBody.Expression);
            BlockSyntax bodyBlock;
            if (addToBeginning) {
                bodyBlock = SyntaxFactory.Block(newStatement, returnStatement);
            } else {
                bodyBlock = SyntaxFactory.Block(returnStatement, newStatement);
            }
            // Create a new method node with the block body
            var newMethod = targetMethodNode.WithBody(bodyBlock)
                .WithExpressionBody(null) // Remove expression body
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None)); // Remove semicolon if any
            editor.ReplaceNode(targetMethodNode, newMethod);
        } else {
            // Method has no body (e.g. abstract, partial, extern). Create one.
            var bodyBlock = SyntaxFactory.Block(newStatement);
            var newMethodWithBody = targetMethodNode.WithBody(bodyBlock)
                .WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));
            editor.ReplaceNode(targetMethodNode, newMethodWithBody);
        }

        var newDocument = editor.GetChangedDocument();
        var formattedDocument = await FormatDocumentAsync(newDocument, cancellationToken);
        return formattedDocument.Project.Solution;
    }

    private static SyntaxTrivia newline = SyntaxFactory.EndOfLine("\n");
    private SyntaxTriviaList NormalizeLeadingTrivia(SyntaxTriviaList trivia) {
        // Remove all newlines
        var filtered = trivia.Where(t => !t.IsKind(SyntaxKind.EndOfLineTrivia));

        // Create a new list with a newline at the beginning followed by the filtered trivia
        return SyntaxFactory.TriviaList(newline, newline).AddRange(filtered);
    }

    private SyntaxTriviaList NormalizeTrailingTrivia(SyntaxTriviaList trivia) {
        // Remove all newlines
        var filtered = trivia.Where(t => !t.IsKind(SyntaxKind.EndOfLineTrivia));

        // Create a new SyntaxTriviaList from the filtered items
        var result = SyntaxFactory.TriviaList(filtered);

        // Add the end-of-line trivia and return
        return result.Add(newline).Add(newline);
    }

    private SyntaxNode NormalizeMemberDeclarationTrivia(SyntaxNode member) {
        if (member is MemberDeclarationSyntax memberDeclaration) {
            var leadingTrivia = memberDeclaration.GetLeadingTrivia();
            var trailingTrivia = memberDeclaration.GetTrailingTrivia();

            // Normalize trivia
            var normalizedLeading = NormalizeLeadingTrivia(leadingTrivia);
            var normalizedTrailing = NormalizeTrailingTrivia(trailingTrivia);

            // Apply the normalized trivia
            return memberDeclaration.WithLeadingTrivia(normalizedLeading)
                                    .WithTrailingTrivia(normalizedTrailing);
        }
        return member;
    }
    public async Task<Solution> ReplaceNodeAsync(DocumentId documentId, SyntaxNode oldNode, SyntaxNode newNode, CancellationToken cancellationToken) {
        var solution = GetCurrentSolutionOrThrow();
        var document = solution.GetDocument(documentId) ?? throw new ArgumentException($"Document with ID '{documentId}' not found in the current solution.", nameof(documentId));

        _logger.LogInformation("Replacing node in document {DocumentPath}", document.FilePath);

        // Check if this is a deletion operation (newNode is an EmptyStatement with a delete comment)
        bool isDeleteOperation = newNode is Microsoft.CodeAnalysis.CSharp.Syntax.EmptyStatementSyntax emptyStmt &&
                                 emptyStmt.HasLeadingTrivia &&
                                 emptyStmt.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
                                                                     t.ToString().StartsWith("// Delete", StringComparison.OrdinalIgnoreCase));

        if (isDeleteOperation) {
            _logger.LogInformation("Detected deletion operation for node {NodeKind}", oldNode.Kind());

            // For deletion, we need to remove the node from its parent
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) {
                throw new InvalidOperationException("Could not get syntax root for document.");
            }

            // Different approach based on the node's parent context
            SyntaxNode newRoot;

            if (oldNode.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax compilationUnit) {
                // Handle top-level members in the compilation unit
                if (oldNode is Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax memberToRemove) {
                    var newMembers = compilationUnit.Members.Remove(memberToRemove);
                    newRoot = compilationUnit.WithMembers(newMembers);
                } else {
                    throw new InvalidOperationException($"Cannot delete node of type {oldNode.GetType().Name} directly from compilation unit.");
                }
            } else if (oldNode.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax namespaceDecl) {
                // Handle members in a namespace
                if (oldNode is Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax memberToRemove) {
                    var newMembers = namespaceDecl.Members.Remove(memberToRemove);
                    var newNamespace = namespaceDecl.WithMembers(newMembers);
                    newRoot = root.ReplaceNode(namespaceDecl, newNamespace);
                } else {
                    throw new InvalidOperationException($"Cannot delete node of type {oldNode.GetType().Name} from namespace declaration.");
                }
            } else if (oldNode.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax typeDecl) {
                // Handle members in a type declaration (class, struct, interface, etc.)
                if (oldNode is Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax memberToRemove) {
                    var newMembers = typeDecl.Members.Remove(memberToRemove);
                    var newType = typeDecl.WithMembers(newMembers);
                    newRoot = root.ReplaceNode(typeDecl, newType);
                } else {
                    throw new InvalidOperationException($"Cannot delete node of type {oldNode.GetType().Name} from type declaration.");
                }
            } else {
                throw new InvalidOperationException($"Cannot delete node of type {oldNode.GetType().Name} from parent of type {oldNode.Parent?.GetType().Name ?? "null"}.");
            }

            var newDocument = document.WithSyntaxRoot(newRoot);
            var formattedDocument = await FormatDocumentAsync(newDocument, cancellationToken);
            return formattedDocument.Project.Solution;
        } else {
            // Standard node replacement
            NormalizeMemberDeclarationTrivia(newNode);

            var editor = await DocumentEditor.CreateAsync(document, cancellationToken);
            editor.ReplaceNode(oldNode, newNode);

            var newDocument = editor.GetChangedDocument();
            var formattedDocument = await FormatDocumentAsync(newDocument, cancellationToken);
            return formattedDocument.Project.Solution;
        }
    }
    public async Task<Solution> RenameSymbolAsync(ISymbol symbol, string newName, CancellationToken cancellationToken) {
        var solution = GetCurrentSolutionOrThrow();
        _logger.LogInformation("Renaming symbol {SymbolName} to {NewName}", symbol.ToDisplayString(), newName);

        var options = solution.Workspace.Options;

        // Using the older API for now
        // Temporarily disable obsolete warning
#pragma warning disable CS0618
        var newSolution = await Renamer.RenameSymbolAsync(solution, symbol, newName, options, cancellationToken);
#pragma warning restore CS0618

        return newSolution;
    }
    public async Task<Solution> ReplaceAllReferencesAsync(ISymbol symbol, string replacementText, CancellationToken cancellationToken, Func<SyntaxNode, bool>? predicateFilter = null) {
        var solution = GetCurrentSolutionOrThrow();
        _logger.LogInformation("Replacing all references to symbol {SymbolName} with text '{ReplacementText}'",
            symbol.ToDisplayString(), replacementText);

        // Find all references to the symbol
        var referencedSymbols = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        var changedSolution = solution;

        foreach (var referencedSymbol in referencedSymbols) {
            foreach (var location in referencedSymbol.Locations) {
                cancellationToken.ThrowIfCancellationRequested();

                var document = changedSolution.GetDocument(location.Document.Id);
                if (document == null) continue;

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root == null) continue;

                var node = root.FindNode(location.Location.SourceSpan);

                // Apply filter if provided
                if (predicateFilter != null && !predicateFilter(node)) {
                    _logger.LogDebug("Skipping replacement for node at {Location} due to filter predicate",
                        location.Location.GetLineSpan());
                    continue;
                }

                // Create a new syntax node with the replacement text
                var replacementNode = SyntaxFactory.ParseExpression(replacementText)
                    .WithLeadingTrivia(node.GetLeadingTrivia())
                    .WithTrailingTrivia(node.GetTrailingTrivia());

                // Replace the node in the document
                var newRoot = root.ReplaceNode(node, replacementNode);
                var newDocument = document.WithSyntaxRoot(newRoot);

                // Format the document and update the solution
                var formattedDocument = await FormatDocumentAsync(newDocument, cancellationToken);
                changedSolution = formattedDocument.Project.Solution;
            }
        }

        return changedSolution;
    }
    public async Task<Solution> FindAndReplaceAsync(string targetString, string regexPattern, string replacementText, CancellationToken cancellationToken, RegexOptions options = RegexOptions.Multiline) {
        var solution = GetCurrentSolutionOrThrow();
        _logger.LogInformation("Performing find and replace with regex '{RegexPattern}' on target '{TargetString}'",
            regexPattern, targetString);

        // Create the regex with multiline option
        var regex = new Regex(regexPattern, options);
        Solution resultSolution = solution;

        // Check if the target is a fully qualified name (no wildcards)
        if (!targetString.Contains("*") && !targetString.Contains("?")) {
            try {
                // Try to resolve as a symbol
                var symbol = await _solutionManager.FindRoslynSymbolAsync(targetString, cancellationToken);
                if (symbol != null) {
                    _logger.LogInformation("Target is a valid symbol: {SymbolName}", symbol.ToDisplayString());

                    // For a symbol, we'll get its defining document and limit replacements to the symbol's span
                    var syntaxReferences = symbol.DeclaringSyntaxReferences;
                    if (syntaxReferences.Any()) {
                        foreach (var syntaxRef in syntaxReferences) {
                            cancellationToken.ThrowIfCancellationRequested();

                            var node = await syntaxRef.GetSyntaxAsync(cancellationToken);
                            var document = solution.GetDocument(node.SyntaxTree);

                            if (document == null) continue;

                            // Get the source text and limit replacement to the symbol's node span
                            var sourceText = await document.GetTextAsync(cancellationToken);
                            var originalText = sourceText.ToString();

                            // Extract only the text within the symbol's node span
                            var nodeSpan = node.Span;
                            var symbolText = originalText.Substring(nodeSpan.Start, nodeSpan.Length).NormalizeEndOfLines();

                            // Apply regex replacement only to the symbol's text
                            var newSymbolText = regex.Replace(symbolText, replacementText);

                            // Only update if changes were made to the symbol text
                            if (newSymbolText != symbolText) {
                                // Create new text by replacing the symbol's span with the modified text
                                var newFullText = originalText.Substring(0, nodeSpan.Start) +
                                    newSymbolText +
                                    originalText.Substring(nodeSpan.Start + nodeSpan.Length);

                                var newDocument = document.WithText(SourceText.From(newFullText, sourceText.Encoding));
                                var formattedDocument = await FormatDocumentAsync(newDocument, cancellationToken);
                                resultSolution = formattedDocument.Project.Solution;
                            }
                        }

                        return resultSolution;
                    }
                }
            } catch (Exception ex) {
                _logger.LogInformation("Target string is not a valid symbol: {Error}", ex.Message);
                // Fall through to file-based search
            }
        }

        // Handle as file path with potential wildcards
        var documentIds = new List<DocumentId>();

        // Log the pattern we're using
        _logger.LogInformation("Treating '{Target}' as a file path pattern", targetString);

        // Normalize path separators in the target pattern to use forward slashes consistently
        string normalizedTarget = targetString.Replace('\\', '/');

        Matcher matcher = new(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(normalizedTarget);
        string root = Path.GetPathRoot(solution.FilePath) ?? Path.GetPathRoot(Environment.CurrentDirectory)!;

        // Process all projects and documents
        foreach (var project in solution.Projects) {
            foreach (var document in project.Documents) {
                if (string.IsNullOrWhiteSpace(document.FilePath)) continue;
                // Use wildcard matching
                if (matcher.Match(root, document.FilePath).HasMatches) {
                    _logger.LogInformation("Document matched pattern: {DocumentPath}", document.FilePath);
                    documentIds.Add(document.Id);
                }
            }
        }

        _logger.LogInformation("Found {Count} documents matching pattern '{Pattern}'",
            documentIds.Count, targetString);

        resultSolution = solution;
        // Process all matching documents
        foreach (var documentId in documentIds) {
            cancellationToken.ThrowIfCancellationRequested();

            // Apply regex replacement
            var document = resultSolution.GetDocument(documentId);
            if (document == null) continue;
            var sourceText = await document.GetTextAsync(cancellationToken);
            var originalText = sourceText.ToString().NormalizeEndOfLines();

            var newText = regex.Replace(originalText, replacementText);

            // Only update if changes were made
            if (newText != originalText) {
                var newDocument = document.WithText(SourceText.From(newText, sourceText.Encoding));
                var formattedDocument = await FormatDocumentAsync(newDocument, cancellationToken);
                resultSolution = formattedDocument.Project.Solution;
            }
        }

        return resultSolution;
    }
    public async Task<Document> FormatDocumentAsync(Document document, CancellationToken cancellationToken) {
        _logger.LogDebug("Formatting document: {DocumentPath}", document.FilePath);
        var formattingOptions = await document.GetOptionsAsync(cancellationToken);
        var formattedDocument = await Formatter.FormatAsync(document, formattingOptions, cancellationToken);
        _logger.LogDebug("Document formatted: {DocumentPath}", document.FilePath);
        return formattedDocument;
    }
    public async Task ApplyChangesAsync(Solution newSolution, CancellationToken cancellationToken, string commitMessage, IEnumerable<string>? additionalFilePaths = null) {
        if (_solutionManager.CurrentWorkspace is not MSBuildWorkspace workspace) {
            _logger.LogError("Cannot apply changes: Workspace is not an MSBuildWorkspace or is null.");
            throw new InvalidOperationException("Workspace is not suitable for applying changes.");
        }

        var originalSolution = _solutionManager.CurrentSolution ?? throw new InvalidOperationException("Original solution is null before applying changes.");
        var solutionPath = originalSolution.FilePath ?? "";

        var solutionChanges = newSolution.GetChanges(originalSolution);
        var finalSolutionToApply = newSolution;

        // Collect changed file paths for git operations - include both changed and new documents
        var changedFilePaths = new List<string>();

        foreach (var projectChange in solutionChanges.GetProjectChanges()) {
            // Handle changed documents
            foreach (var changedDocumentId in projectChange.GetChangedDocuments()) {
                var documentToFormat = finalSolutionToApply.GetDocument(changedDocumentId);
                if (documentToFormat != null) {
                    _logger.LogDebug("Pre-apply formatting for changed document: {DocumentPath}", documentToFormat.FilePath);
                    var formattedDocument = await FormatDocumentAsync(documentToFormat, cancellationToken);
                    finalSolutionToApply = formattedDocument.Project.Solution;

                    if (!string.IsNullOrEmpty(documentToFormat.FilePath)) {
                        changedFilePaths.Add(documentToFormat.FilePath);
                    }
                }
            }

            // Handle added documents (new files)
            foreach (var addedDocumentId in projectChange.GetAddedDocuments()) {
                var addedDocument = finalSolutionToApply.GetDocument(addedDocumentId);
                if (addedDocument != null) {
                    _logger.LogDebug("Pre-apply formatting for added document: {DocumentPath}", addedDocument.FilePath);
                    var formattedDocument = await FormatDocumentAsync(addedDocument, cancellationToken);
                    finalSolutionToApply = formattedDocument.Project.Solution;

                    if (!string.IsNullOrEmpty(addedDocument.FilePath)) {
                        changedFilePaths.Add(addedDocument.FilePath);
                        _logger.LogInformation("Added new document for git tracking: {DocumentPath}", addedDocument.FilePath);
                    }
                }
            }

            // Handle removed documents
            foreach (var removedDocumentId in projectChange.GetRemovedDocuments()) {
                var removedDocument = originalSolution.GetDocument(removedDocumentId);
                if (removedDocument != null && !string.IsNullOrEmpty(removedDocument.FilePath)) {
                    changedFilePaths.Add(removedDocument.FilePath);
                    _logger.LogInformation("Marked removed document for git tracking: {DocumentPath}", removedDocument.FilePath);
                }
            }
        }

        _logger.LogInformation("Applying changes to workspace for {DocumentCount} changed documents across {ProjectCount} projects.",
            solutionChanges.GetProjectChanges().SelectMany(pc => pc.GetChangedDocuments().Concat(pc.GetAddedDocuments()).Concat(pc.GetRemovedDocuments())).Count(),
            solutionChanges.GetProjectChanges().Count());

        if (workspace.TryApplyChanges(finalSolutionToApply)) {
            _logger.LogInformation("Changes applied successfully to the workspace.");

            // If additional file paths are provided, add them to the changed file paths
            if (additionalFilePaths != null) {
                changedFilePaths.AddRange(additionalFilePaths.Where(fp => !string.IsNullOrEmpty(fp) && File.Exists(fp)));
            }
            // Git operations after successful changes
            await ProcessGitOperationsAsync(solutionPath, changedFilePaths, commitMessage, cancellationToken);

            _solutionManager.RefreshCurrentSolution();
        } else {
            _logger.LogError("Failed to apply changes to the workspace.");
            throw new InvalidOperationException("Failed to apply changes to the workspace. Files might have been modified externally.");
        }
    }
    private async Task ProcessGitOperationsAsync(string solutionPath, List<string> changedFilePaths, string commitMessage, CancellationToken cancellationToken) {
        if (string.IsNullOrEmpty(solutionPath) || changedFilePaths.Count == 0) {
            return;
        }

        try {
            // Check if solution is in a git repo
            if (!await _gitService.IsRepositoryAsync(solutionPath, cancellationToken)) {
                _logger.LogDebug("Solution is not in a Git repository, skipping Git operations");
                return;
            }

            _logger.LogDebug("Solution is in a Git repository, processing Git operations");

            // Check if already on sharptools branch
            if (!await _gitService.IsOnSharpToolsBranchAsync(solutionPath, cancellationToken)) {
                _logger.LogInformation("Not on a SharpTools branch, creating one");
                await _gitService.EnsureSharpToolsBranchAsync(solutionPath, cancellationToken);
            }

            // Commit changes with the provided commit message
            await _gitService.CommitChangesAsync(solutionPath, changedFilePaths, commitMessage, cancellationToken);
            _logger.LogInformation("Git operations completed successfully with commit message: {CommitMessage}", commitMessage);
        } catch (Exception ex) {
            // Log but don't fail the operation if Git operations fail
            _logger.LogWarning(ex, "Git operations failed but code changes were still applied");
        }
    }
    public async Task<(bool success, string message)> UndoLastChangeAsync(CancellationToken cancellationToken) {
        if (_solutionManager.CurrentWorkspace is not MSBuildWorkspace workspace) {
            _logger.LogError("Cannot undo changes: Workspace is not an MSBuildWorkspace or is null.");
            var message = "Error: Workspace is not an MSBuildWorkspace or is null. Cannot undo.";
            return (false, message);
        }

        var currentSolution = _solutionManager.CurrentSolution;
        if (currentSolution?.FilePath == null) {
            _logger.LogError("Cannot undo changes: Current solution or its file path is null.");
            var message = "Error: No solution loaded or solution file path is null. Cannot undo.";
            return (false, message);
        }

        var solutionPath = currentSolution.FilePath;

        // Check if solution is in a git repository
        if (!await _gitService.IsRepositoryAsync(solutionPath, cancellationToken)) {
            _logger.LogError("Cannot undo changes: Solution is not in a Git repository.");
            throw new McpException("Error: Solution is not in a Git repository. Undo functionality requires Git version control.");
        }

        // Check if we're on a sharptools branch
        if (!await _gitService.IsOnSharpToolsBranchAsync(solutionPath, cancellationToken)) {
            _logger.LogError("Cannot undo changes: Not on a SharpTools branch.");
            var message = "Error: Not on a SharpTools branch. Undo is only available on SharpTools branches.";
            return (false, message);
        }

        _logger.LogInformation("Attempting to undo last change by reverting last Git commit.");

        // Perform git revert with diff
        var (revertSuccess, diff) = await _gitService.RevertLastCommitAsync(solutionPath, cancellationToken);
        if (!revertSuccess) {
            _logger.LogError("Git revert operation failed.");
            var message = "Error: Failed to revert the last Git commit. There may be no commits to revert or the operation failed.";
            return (false, message);
        }

        // Reload the solution from disk to reflect the reverted changes
        await _solutionManager.ReloadSolutionFromDiskAsync(cancellationToken);

        _logger.LogInformation("Successfully reverted the last change using Git.");
        var successMessage = "Successfully reverted the last change by reverting the last Git commit. Solution reloaded from disk.";

        // Add the diff to the success message if available
        if (!string.IsNullOrEmpty(diff)) {
            successMessage += "\n\nChanges undone:\n" + diff;
        }

        return (true, successMessage);
    }
}