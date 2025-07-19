
namespace SharpTools.Tools.Services {
    public class FuzzyFqnLookupService : IFuzzyFqnLookupService {
        private readonly ILogger<FuzzyFqnLookupService> _logger;
        private ISolutionManager? _solutionManager;

        // Match scoring constants - higher score is better
        private const double PerfectMatchScore = 1.0;
        private const double ConstructorShorthandTypeNameMatchScore = 0.98; // User typed "Namespace.Type" for constructor
        private const double ConstructorShorthandFullMatchScore = 0.97;   // User typed "Namespace.Type.Type" for constructor
        private const double ParamsOmittedScore = 0.9;
        private const double ArityOmittedScore = 0.85;
        private const double GenericArgsOmittedScore = 0.80;
        private const double NestedTypeDotForPlusScore = 0.75;
        private const double CaseInsensitiveMatchScore = 0.99;
        private const double ParametersContentMismatchScore = 0.7; // Base name matches, but params differ
        private const double MinScoreThreshold = 0.7; // Minimum score to be considered a match

        // Regex patterns for normalization
        private static readonly Regex ParamsRegex = new(@"\s*\([\s\S]*\)\s*$", RegexOptions.Compiled);
        private static readonly Regex ArityRegex = new(@"`\d+", RegexOptions.Compiled);
        private static readonly Regex GenericArgsRegex = new(@"<[^<>]+>", RegexOptions.Compiled); // Simplistic: removes <...>

        public FuzzyFqnLookupService(ILogger<FuzzyFqnLookupService> logger) {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        public static bool IsPartialType(ISymbol typeSymbol) {
            if (typeSymbol is not INamedTypeSymbol namedTypeSymbol) {
                return false; // Not a type symbol
            }

            return typeSymbol.DeclaringSyntaxReferences.Length > 1 ||
                typeSymbol.DeclaringSyntaxReferences.Any(syntax =>
                    syntax.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax declaration &&
                    declaration.Modifiers.Any(modifier =>
                        modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)));
        }
        /// <inheritdoc />
        public async Task<IEnumerable<FuzzyMatchResult>> FindMatchesAsync(string fuzzyFqnInput, ISolutionManager solutionManager, CancellationToken cancellationToken) {
            _solutionManager = solutionManager;
            if (!solutionManager.IsSolutionLoaded) {
                _logger.LogWarning("Cannot perform fuzzy FQN lookup: No solution loaded.");
                return Enumerable.Empty<FuzzyMatchResult>();
            }

            var potentialMatches = new List<FuzzyMatchResult>();
            string trimmedFuzzyFqn = fuzzyFqnInput.Trim().Replace(" ", string.Empty);
            var allRelevantSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

            // Process each document in the solution to collect symbols
            foreach (var project in solutionManager.CurrentSolution.Projects) {
                // Check cancellation before starting work on each project
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var document in project.Documents) {
                    // Check cancellation before starting work on each document
                    cancellationToken.ThrowIfCancellationRequested();

                    var semanticModel = await solutionManager.GetSemanticModelAsync(document.Id, cancellationToken);
                    if (semanticModel == null) {
                        _logger.LogWarning("Could not get semantic model for document {DocumentPath}", document.FilePath);
                        continue;
                    }

                    // Get all symbols from the semantic model
                    CollectSymbolsFromSemanticModel(semanticModel, allRelevantSymbols, cancellationToken);
                }
            }
            _logger.LogDebug("Collected {SymbolCount} symbols from solution documents", allRelevantSymbols.Count);
            int prefilter = allRelevantSymbols.Count;

            //Remove all duplicates, no idea why this is needed. //TODO
            var partialTypes = allRelevantSymbols
                //.OfType<INamedTypeSymbol>()
                //.Where(IsPartialType)
                .GroupBy(GetSearchableString)
                .ToList();

            allRelevantSymbols = new HashSet<ISymbol>(allRelevantSymbols.Except(partialTypes.SelectMany(g => g.Skip(1))), SymbolEqualityComparer.Default); // Remove all but one from each group



            _logger.LogDebug("Filtered {PrefilterCount} symbols to {SymbolCount} after removing duplicates", prefilter, allRelevantSymbols.Count);

            // Match symbols against the fuzzy FQN
            foreach (var symbol in allRelevantSymbols) {
                // Check cancellation periodically during symbol processing
                cancellationToken.ThrowIfCancellationRequested();

                //string canonicalFqn = symbol.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal);
                string canonicalFqn = GetSearchableString(symbol);
                //_logger.LogDebug("Checking symbol: {SymbolName} with FQN: {CanonicalFqn}", symbol.Name, canonicalFqn);
                var (score, reason) = CalculateMatchScore(trimmedFuzzyFqn, symbol, canonicalFqn, cancellationToken);

                if (score >= MinScoreThreshold) {
                    potentialMatches.Add(new FuzzyMatchResult(canonicalFqn, symbol, score, reason));
                }
            }

            // Check cancellation before sorting results
            cancellationToken.ThrowIfCancellationRequested();

            // Sort by score descending, then by FQN alphabetically for stable results
            var results = potentialMatches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.CanonicalFqn)
            .ToList();

            _logger.LogDebug("Found {MatchCount} matches for fuzzy FQN '{FuzzyFqn}'", results.Count, fuzzyFqnInput);

            // If multiple matches are found, but one is perfect, filter to that one
            if (results.Count > 1) {
                var perfectMatches = results.Where(m => m.Score >= PerfectMatchScore - 0.01).ToList();
                if (perfectMatches.Count == 1) {
                    _logger.LogDebug("Filtered to single perfect match for '{FuzzyFqn}'", fuzzyFqnInput);
                    return perfectMatches;
                }
            }

            // Log ambiguity details when multiple high-scoring matches are found
            await LogAmbiguityDetailsAsync(fuzzyFqnInput, results, cancellationToken);

            return results;
        }
        private void CollectSymbolsFromSemanticModel(SemanticModel semanticModel, HashSet<ISymbol> collectedSymbols, CancellationToken cancellationToken) {
            // Get the global namespace from the compilation
            var compilation = semanticModel.Compilation;

            // Collect from global namespace
            //CollectSymbols(compilation.GlobalNamespace, collectedSymbols, cancellationToken);

            // Also collect any declarations from the syntax tree of this document
            var root = semanticModel.SyntaxTree.GetRoot(cancellationToken);
            foreach (var node in root
                .DescendantNodes(descendIntoChildren: n => n is MemberDeclarationSyntax or TypeDeclarationSyntax or NamespaceDeclarationSyntax or CompilationUnitSyntax)
                .Where(n => n is MemberDeclarationSyntax or TypeDeclarationSyntax or NamespaceDeclarationSyntax)) {
                // Check cancellation periodically during node traversal
                cancellationToken.ThrowIfCancellationRequested();
                var declaredSymbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
                if (declaredSymbol != null) {
                    collectedSymbols.Add(declaredSymbol);
                }
            }
        }

        private void CollectSymbols(INamespaceOrTypeSymbol containerSymbol, HashSet<ISymbol> collectedSymbols, CancellationToken cancellationToken) {
            // Check cancellation at the beginning of recursive operations
            cancellationToken.ThrowIfCancellationRequested();

            if (containerSymbol is INamedTypeSymbol typeSymbol) {
                // Add the type itself
                collectedSymbols.Add(typeSymbol);
            }

            foreach (var member in containerSymbol.GetMembers()) {
                // Check cancellation periodically during member processing
                cancellationToken.ThrowIfCancellationRequested();

                switch (member.Kind) {
                    case SymbolKind.Namespace:
                        CollectSymbols((INamespaceSymbol)member, collectedSymbols, cancellationToken);
                        break;
                    case SymbolKind.NamedType:
                        CollectSymbols((INamedTypeSymbol)member, collectedSymbols, cancellationToken); // Recurse for nested types
                        break;
                    case SymbolKind.Method:
                        // Includes constructors, operators, accessors, destructors
                        collectedSymbols.Add(member);
                        break;
                    case SymbolKind.Property:
                        var prop = (IPropertySymbol)member;
                        collectedSymbols.Add(prop); // Add property itself for FQNs like Namespace.Type.Property
                        if (prop.GetMethod != null) collectedSymbols.Add(prop.GetMethod);
                        if (prop.SetMethod != null) collectedSymbols.Add(prop.SetMethod);
                        break;
                    case SymbolKind.Event:
                        var evt = (IEventSymbol)member;
                        collectedSymbols.Add(evt); // Add event itself for FQNs like Namespace.Type.Event
                        if (evt.AddMethod != null) collectedSymbols.Add(evt.AddMethod);
                        if (evt.RemoveMethod != null) collectedSymbols.Add(evt.RemoveMethod);
                        break;
                    case SymbolKind.Field:
                        collectedSymbols.Add(member);
                        break;
                }
            }
        }

        private (double score, string reason) CalculateMatchScore(string userInputFqn, ISymbol symbol, string canonicalFqn, CancellationToken cancellationToken) {
            // Periodically check cancellation during the scoring process
            cancellationToken.ThrowIfCancellationRequested();

            // 0. Direct case-sensitive match
            if (userInputFqn.Equals(canonicalFqn, StringComparison.Ordinal)) {
                return (PerfectMatchScore, "Exact match");
            }

            // 0.1. Direct case-insensitive match
            if (userInputFqn.Equals(canonicalFqn, StringComparison.OrdinalIgnoreCase)) {
                return (CaseInsensitiveMatchScore, "Case-insensitive exact match");
            }

            // Prepare normalized versions for comparison
            string userInputNoParams = RemoveParameters(userInputFqn);
            string canonicalFqnNoParams = RemoveParameters(canonicalFqn);

            // Normalize generic arguments - keep the angle brackets but normalize contents
            // For example, "List<int>" and "List<T>" might be considered equivalent in some cases
            string userInputWithNormalizedGenerics = NormalizeGenericArgs(userInputNoParams);
            string canonicalFqnWithNormalizedGenerics = NormalizeGenericArgs(canonicalFqnNoParams);

            // Check cancellation after regex processing
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Constructor shorthands
            if (symbol is IMethodSymbol methodSymbol &&
                (methodSymbol.MethodKind == MethodKind.Constructor || methodSymbol.MethodKind == MethodKind.StaticConstructor)) {

                // Get containing type name with proper generic arguments
                string typeFullName = GetSearchableString(methodSymbol.ContainingType);

                // User typed "Namespace.Type.Type" or "Namespace.Type.Type()"
                if (userInputNoParams.Equals(typeFullName + "." + methodSymbol.ContainingType.Name, StringComparison.OrdinalIgnoreCase)) {
                    return (ConstructorShorthandFullMatchScore, "Constructor shorthand (Type.Type)");
                }

                // User typed "Namespace.Type" or "Namespace.Type()"
                if (userInputNoParams.Equals(typeFullName, StringComparison.OrdinalIgnoreCase)) {
                    return (ConstructorShorthandTypeNameMatchScore, "Constructor shorthand (Type)");
                }
            }

            // Check cancellation before the next set of comparisons
            cancellationToken.ThrowIfCancellationRequested();

            // 2. Match after removing parameters (user omitted them or canonical was parameterless)
            if (userInputNoParams.Equals(canonicalFqnNoParams, StringComparison.OrdinalIgnoreCase)) {
                bool userInputHadParams = userInputFqn.Length != userInputNoParams.Length;
                bool canonicalHadParams = canonicalFqn.Length != canonicalFqnNoParams.Length;

                if (userInputHadParams && canonicalHadParams) { // Both had params, but content differed (caught by initial exact match if same)
                    return (ParametersContentMismatchScore, "Parameter content mismatch");
                }

                return (ParamsOmittedScore, "Parameter list omitted/matched empty");
            }

            // 3. Match with normalized generic arguments
            if (userInputWithNormalizedGenerics.Equals(canonicalFqnWithNormalizedGenerics, StringComparison.OrdinalIgnoreCase)) {
                return (0.95, "Generic arguments normalized match");
            }

            // 4. Match with generic arguments stripped (user might search for base name)
            string userInputNoGenerics = StripGenericArgs(userInputNoParams);
            string canonicalFqnNoGenerics = StripGenericArgs(canonicalFqnNoParams);

            if (userInputNoGenerics.Equals(canonicalFqnNoGenerics, StringComparison.OrdinalIgnoreCase)) {
                // If the user actually included generic args, but we had to strip them to match, 
                // it could mean their generic args don't match the canonical ones
                bool userHadGenericArgs = userInputNoParams.Contains("<") && userInputNoParams != userInputNoGenerics;
                bool canonicalHadGenericArgs = canonicalFqnNoParams.Contains("<") && canonicalFqnNoParams != canonicalFqnNoGenerics;

                if (userHadGenericArgs && canonicalHadGenericArgs) {
                    // Both had generic args but they didn't match exactly
                    return (GenericArgsOmittedScore, "Generic arguments content mismatch");
                }

                return (ArityOmittedScore, "Generic arguments omitted/matched empty");
            }

            // 5. Nested Type: User used '.' where canonical FQN uses '+'
            // This should be less relevant now that we normalize '+' to '.' in GetSearchableString,
            // but kept for backward compatibility
            string? userNestedFixed = TryFixNestedTypeSeparator(userInputNoGenerics, canonicalFqnNoGenerics, cancellationToken);
            if (userNestedFixed != null && userNestedFixed.Equals(canonicalFqnNoGenerics, StringComparison.OrdinalIgnoreCase)) {
                return (NestedTypeDotForPlusScore, "Nested type separator '.' used for '+'");
            }

            return (0.0, "No significant match");
        }
        private string RemoveParameters(string fqn) {
            // A more robust way to find the first '(':
            int openParenIndex = fqn.IndexOf('(');
            if (openParenIndex != -1) {
                // Check if it's part of a generic type argument list like `Method(List<string>)`
                // or a method generic parameter list like `Method<T>(T p)`
                // SymbolDisplayFormat.FullyQualifiedFormat puts method type parameters like `Method``1`
                // and parameters like `(System.Int32)`.
                // So, `(` should reliably indicate start of parameter list for methods.
                // For delegates, it might be `System.Action()`
                string result = fqn.Substring(0, openParenIndex);
                return result;
            }
            return fqn;
        }

        private string? TryFixNestedTypeSeparator(string userInputFqnPart, string canonicalFqnPart, CancellationToken cancellationToken) {
            // Check cancellation at the beginning of processing
            cancellationToken.ThrowIfCancellationRequested();

            // This heuristic attempts to replace '.' with '+' in the type path part of the FQN.
            // It assumes member names (after the last type segment) don't contain '.' or '+'.
            // Example: User "N.O.N.M", Canonical "N.O+N.M"
            // We need to identify "N.O.N" vs "N.O+N" and "M" vs "M"
            int userLastDot = userInputFqnPart.LastIndexOf('.');
            int canonicalLastPlusOrDot = Math.Max(canonicalFqnPart.LastIndexOf('+'), canonicalFqnPart.LastIndexOf('.'));

            // If no dot/plus, it might be a global type or just a member name if type path was empty.
            string userTypePath = userLastDot > -1 ? userInputFqnPart.Substring(0, userLastDot) : "";
            string userMemberName = userLastDot > -1 ? userInputFqnPart.Substring(userLastDot) : userInputFqnPart; // Member name includes the leading '.' if present
            string canonicalTypePath = canonicalLastPlusOrDot > -1 ? canonicalFqnPart.Substring(0, canonicalLastPlusOrDot) : "";
            string canonicalMemberName = canonicalLastPlusOrDot > -1 ? canonicalFqnPart.Substring(canonicalLastPlusOrDot) : canonicalFqnPart;

            if (userMemberName.Equals(canonicalMemberName, StringComparison.OrdinalIgnoreCase)) {
                if (userTypePath.Replace('.', '+').Equals(canonicalTypePath, StringComparison.OrdinalIgnoreCase)) {
                    string result = canonicalTypePath + userMemberName;
                    return result; // Return the "fixed" version based on canonical structure
                }
            }
            // Special case: if the whole thing is a type name (no distinct member part)
            else if (string.IsNullOrEmpty(userMemberName) && string.IsNullOrEmpty(canonicalMemberName) || userLastDot == -1 && canonicalLastPlusOrDot == -1) {
                if (userInputFqnPart.Replace('.', '+').Equals(canonicalFqnPart, StringComparison.OrdinalIgnoreCase)) {
                    return canonicalFqnPart;
                }
            }

            return null; // No simple fix found
        }

        public static string GetSearchableString(ISymbol symbol) {
            var fullFormat = new SymbolDisplayFormat(
                globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
                memberOptions: SymbolDisplayMemberOptions.IncludeParameters |
                    SymbolDisplayMemberOptions.IncludeType |
                    SymbolDisplayMemberOptions.IncludeRef |
                    SymbolDisplayMemberOptions.IncludeContainingType,
                parameterOptions: SymbolDisplayParameterOptions.IncludeType |
                    SymbolDisplayParameterOptions.IncludeName |
                    SymbolDisplayParameterOptions.IncludeParamsRefOut |
                    SymbolDisplayParameterOptions.IncludeDefaultValue,
                propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
                localOptions: SymbolDisplayLocalOptions.None,
                miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                    SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

            // For parameters and return types, use a format that doesn't qualify types
            var shortFormat = new SymbolDisplayFormat(
                globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                memberOptions: SymbolDisplayMemberOptions.IncludeParameters |
                    SymbolDisplayMemberOptions.IncludeType,
                parameterOptions: SymbolDisplayParameterOptions.IncludeType,
                //SymbolDisplayParameterOptions.IncludeName |
                //SymbolDisplayParameterOptions.IncludeParamsRefOut |
                //SymbolDisplayParameterOptions.IncludeDefaultValue,
                propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
                localOptions: SymbolDisplayLocalOptions.None,
                miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                    SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

            var fqn = new SymbolDisplayFormat(
                globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.OmittedAsContaining,
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
                parameterOptions: SymbolDisplayParameterOptions.None,
                propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
                miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

            if (symbol is IMethodSymbol methodSymbol) {
                // Format the method name and containing type with full qualification
                var methodNameAndType = methodSymbol.ToDisplayString(fqn);

                // Find the indices where return type ends and method name begins
                //var returnTypeEndIndex = methodParts.TakeWhile(p => p.Kind != SymbolDisplayPartKind.MethodName).Count();
                //var nameStartIndex = returnTypeEndIndex;
                //var nameEndIndex = methodParts.TakeWhile(p => p.Kind != SymbolDisplayPartKind.Punctuation && p.ToString() != "(").Count();

                // Get the parts we need
                //var modifiers = methodParts.Take(methodParts.TakeWhile(p => p.Kind == SymbolDisplayPartKind.Keyword).Count());
                //var returnType = methodSymbol.ReturnType.ToDisplayString(shortFormat);
                //var nameAndContainingType = string.Concat(methodParts.Take(nameEndIndex));

                // Get param types only
                var parameters = string.Join(", ", methodSymbol.Parameters
                    .Select(p => string.Concat(p.ToDisplayParts(shortFormat)
                        .TakeWhile(part => part.Kind != SymbolDisplayPartKind.ParameterName)
                        .Select(part => part.ToString()))));

                // Combine all parts
                var signature = methodNameAndType + "(" + parameters + ")";
                return signature.Replace(" ", string.Empty);
            }

            // For non-method symbols, use the original full format
            return symbol.ToDisplayString(fqn).Replace(" ", string.Empty);
        }
        private async Task LogAmbiguityDetailsAsync(string fuzzyFqnInput, List<FuzzyMatchResult> results, CancellationToken cancellationToken) {
            const double HighScoreThreshold = 0.8; // Threshold for considering a match "high-scoring"
            const int MaxDetailedLogsPerAmbiguity = 10; // Limit detailed logs to prevent spam

            // Group matches by score ranges to identify ambiguity patterns
            var highScoreMatches = results.Where(r => r.Score >= HighScoreThreshold).ToList();
            var perfectMatches = results.Where(r => r.Score >= PerfectMatchScore - 0.01).ToList();

            // Log ambiguity when we have multiple high-scoring matches
            if (highScoreMatches.Count > 1) {
                _logger.LogWarning("Ambiguity detected for input '{FuzzyFqn}': Found {HighScoreCount} high-scoring matches (>= {Threshold})",
                fuzzyFqnInput, highScoreMatches.Count, HighScoreThreshold);

                // Group by project to understand cross-project ambiguity
                var matchesByProject = highScoreMatches
                .GroupBy(match => GetProjectName(match.Symbol))
                .ToList();

                if (matchesByProject.Count > 1) {
                    _logger.LogWarning("Cross-project ambiguity detected: Matches span {ProjectCount} projects", matchesByProject.Count);
                    foreach (var projectGroup in matchesByProject) {
                        _logger.LogInformation("Project '{ProjectName}' has {MatchCount} ambiguous matches",
                        projectGroup.Key, projectGroup.Count());
                    }
                }

                // Log detailed information for the top matches
                var detailedMatches = highScoreMatches.Take(MaxDetailedLogsPerAmbiguity).ToList();
                for (int i = 0; i < detailedMatches.Count; i++) {
                    var match = detailedMatches[i];
                    LogDetailedMatchInfoAsync(match, i + 1, cancellationToken);
                }

                // Log summary statistics about the ambiguity
                LogAmbiguitySummary(fuzzyFqnInput, results, highScoreMatches, perfectMatches);
            }
        }
        private void LogDetailedMatchInfoAsync(FuzzyMatchResult match, int rank, CancellationToken cancellationToken) {
            try {
                var symbol = match.Symbol;
                var projectName = GetProjectName(symbol);
                var symbolKind = GetSymbolKindString(symbol);
                var containingType = GetContainingTypeInfo(symbol);
                var assemblyName = GetAssemblyName(symbol);
                var location = GetLocationInfo(symbol);
                var formattedSignature = CodeAnalysisService.GetFormattedSignatureAsync(symbol, false);

                _logger.LogWarning("Ambiguous Match #{Rank}: Score={Score:F3}, Reason='{Reason}'", rank, match.Score, match.MatchReason);
                _logger.LogInformation("  Symbol Details:");
                _logger.LogInformation("    FQN: {CanonicalFqn}", match.CanonicalFqn);
                _logger.LogInformation("    Formatted Signature: {FormattedSignature}", formattedSignature);
                _logger.LogInformation("    Symbol Kind: {SymbolKind}", symbolKind);
                _logger.LogInformation("    Symbol Name: {SymbolName}", symbol.Name);
                _logger.LogInformation("    Project: {ProjectName}", projectName);
                _logger.LogInformation("    Assembly: {AssemblyName}", assemblyName);
                _logger.LogInformation("    Location: {Location}", location);

                if (!string.IsNullOrEmpty(containingType)) {
                    _logger.LogInformation("    Containing Type: {ContainingType}", containingType);
                }

                // Log additional type-specific information
                LogTypeSpecificDetails(symbol);

                // Log accessibility and modifiers
                var accessibility = symbol.DeclaredAccessibility.ToString();
                var modifiers = GetSymbolModifiers(symbol);
                _logger.LogInformation("    Accessibility: {Accessibility}", accessibility);
                if (!string.IsNullOrEmpty(modifiers)) {
                    _logger.LogInformation("    Modifiers: {Modifiers}", modifiers);
                }
            } catch (Exception ex) {
                _logger.LogError(ex, "Error logging detailed match info for symbol {SymbolName}", match.Symbol.Name);
            }
        }
        private void LogTypeSpecificDetails(ISymbol symbol) {
            switch (symbol) {
                case INamedTypeSymbol namedType:
                    _logger.LogInformation("    Type Kind: {TypeKind}", namedType.TypeKind);
                    _logger.LogInformation("    Is Generic: {IsGeneric}", namedType.IsGenericType);
                    _logger.LogInformation("    Arity: {Arity}", namedType.Arity);
                    _logger.LogInformation("    Member Count: {MemberCount}", namedType.GetMembers().Length);
                    if (namedType.BaseType != null) {
                        _logger.LogInformation("    Base Type: {BaseType}", namedType.BaseType.ToDisplayString());
                    }
                    if (namedType.Interfaces.Any()) {
                        _logger.LogInformation("    Implements: {InterfaceCount} interfaces", namedType.Interfaces.Length);
                    }
                    break;

                case IMethodSymbol method:
                    _logger.LogInformation("    Method Kind: {MethodKind}", method.MethodKind);
                    _logger.LogInformation("    Return Type: {ReturnType}", method.ReturnType.ToDisplayString());
                    _logger.LogInformation("    Parameter Count: {ParameterCount}", method.Parameters.Length);
                    _logger.LogInformation("    Is Generic: {IsGeneric}", method.IsGenericMethod);
                    _logger.LogInformation("    Is Extension: {IsExtension}", method.IsExtensionMethod);
                    _logger.LogInformation("    Is Async: {IsAsync}", method.IsAsync);
                    if (method.Parameters.Any()) {
                        var parameterTypes = string.Join(", ", method.Parameters.Select(p => p.Type.ToDisplayString()));
                        _logger.LogInformation("    Parameters: {ParameterTypes}", parameterTypes);
                    }
                    break;

                case IPropertySymbol property:
                    _logger.LogInformation("    Property Type: {PropertyType}", property.Type.ToDisplayString());
                    _logger.LogInformation("    Is ReadOnly: {IsReadOnly}", property.IsReadOnly);
                    _logger.LogInformation("    Is WriteOnly: {IsWriteOnly}", property.IsWriteOnly);
                    _logger.LogInformation("    Is Indexer: {IsIndexer}", property.IsIndexer);
                    break;

                case IFieldSymbol field:
                    _logger.LogInformation("    Field Type: {FieldType}", field.Type.ToDisplayString());
                    _logger.LogInformation("    Is Const: {IsConst}", field.IsConst);
                    _logger.LogInformation("    Is ReadOnly: {IsReadOnly}", field.IsReadOnly);
                    _logger.LogInformation("    Is Static: {IsStatic}", field.IsStatic);
                    if (field.IsConst && field.ConstantValue != null) {
                        _logger.LogInformation("    Constant Value: {ConstantValue}", field.ConstantValue);
                    }
                    break;

                case IEventSymbol eventSymbol:
                    _logger.LogInformation("    Event Type: {EventType}", eventSymbol.Type.ToDisplayString());
                    break;
            }
        }
        private void LogAmbiguitySummary(string fuzzyFqnInput, List<FuzzyMatchResult> allResults, List<FuzzyMatchResult> highScoreMatches, List<FuzzyMatchResult> perfectMatches) {
            _logger.LogInformation("Ambiguity Summary for '{FuzzyFqn}':", fuzzyFqnInput);
            _logger.LogInformation("  Total matches: {TotalMatches}", allResults.Count);
            _logger.LogInformation("  Perfect matches (>= {PerfectThreshold:F3}): {PerfectCount}", PerfectMatchScore - 0.01, perfectMatches.Count);
            _logger.LogInformation("  High-scoring matches (>= 0.8): {HighScoreCount}", highScoreMatches.Count);

            // Score distribution
            var scoreRanges = new[] {
(1.0, 0.95, "Excellent"),
(0.95, 0.9, "Very Good"),
(0.9, 0.8, "Good"),
(0.8, 0.7, "Acceptable")
};

            foreach (var (upper, lower, label) in scoreRanges) {
                var count = allResults.Count(r => r.Score < upper && r.Score >= lower);
                if (count > 0) {
                    _logger.LogInformation("  {Label} matches ({Lower:F1}-{Upper:F1}): {Count}", label, lower, upper, count);
                }
            }

            // Symbol kind distribution
            var symbolKinds = highScoreMatches
            .GroupBy(m => GetSymbolKindString(m.Symbol))
            .OrderByDescending(g => g.Count())
            .ToList();

            if (symbolKinds.Any()) {
                _logger.LogInformation("  Symbol kinds in high-scoring matches:");
                foreach (var kind in symbolKinds) {
                    _logger.LogInformation("    {SymbolKind}: {Count}", kind.Key, kind.Count());
                }
            }

            // Project distribution
            var projects = highScoreMatches
            .GroupBy(m => GetProjectName(m.Symbol))
            .OrderByDescending(g => g.Count())
            .ToList();

            if (projects.Count > 1) {
                _logger.LogInformation("  Project distribution:");
                foreach (var project in projects) {
                    _logger.LogInformation("    {ProjectName}: {Count}", project.Key, project.Count());
                }
            }
        }

        private string GetProjectName(ISymbol symbol) {
            try {
                // Try to get the project from the symbol's containing assembly
                var assembly = symbol.ContainingAssembly;
                if (assembly?.Name != null) {
                    return assembly.Name;
                }

                // Fallback: look through loaded projects
                var syntaxRefs = symbol.DeclaringSyntaxReferences;
                if (syntaxRefs.Any()) {
                    var syntaxTree = syntaxRefs.First().SyntaxTree;
                    foreach (var project in _solutionManager?.CurrentSolution?.Projects ?? Enumerable.Empty<Project>()) {
                        if (project.Documents.Any(d => d.GetSyntaxTreeAsync().Result == syntaxTree)) {
                            return project.Name;
                        }
                    }
                }

                return "Unknown Project";
            } catch {
                return "Unknown Project";
            }
        }

        private string GetSymbolKindString(ISymbol symbol) {
            return symbol.Kind.ToString();
        }

        private string GetContainingTypeInfo(ISymbol symbol) {
            if (symbol.ContainingType != null) {
                return symbol.ContainingType.ToDisplayString();
            }
            return string.Empty;
        }

        private string GetAssemblyName(ISymbol symbol) {
            return symbol.ContainingAssembly?.Name ?? "Unknown Assembly";
        }

        private string GetLocationInfo(ISymbol symbol) {
            var location = symbol.Locations.FirstOrDefault(loc => loc.IsInSource);
            if (location?.SourceTree?.FilePath != null) {
                var lineSpan = location.GetLineSpan();
                return $"{Path.GetFileName(location.SourceTree.FilePath)}:{lineSpan.StartLinePosition.Line + 1}";
            }
            return "No source location";
        }

        private string GetSymbolModifiers(ISymbol symbol) {
            var modifiers = new List<string>();

            if (symbol.IsStatic) modifiers.Add("static");
            if (symbol.IsVirtual) modifiers.Add("virtual");
            if (symbol.IsOverride) modifiers.Add("override");
            if (symbol.IsAbstract) modifiers.Add("abstract");
            if (symbol.IsSealed) modifiers.Add("sealed");

            if (symbol is IMethodSymbol method) {
                if (method.IsAsync) modifiers.Add("async");
                if (method.IsExtern) modifiers.Add("extern");
            }

            if (symbol is IFieldSymbol field) {
                if (field.IsReadOnly) modifiers.Add("readonly");
                if (field.IsConst) modifiers.Add("const");
                if (field.IsVolatile) modifiers.Add("volatile");
            }

            return string.Join(" ", modifiers);
        }
        /// <summary>
        /// Normalizes generic arguments in a type name by replacing specific type names with placeholders.
        /// This allows matching between different generic instantiations.
        /// </summary>
        /// <param name="typeName">The type name with generic arguments</param>
        /// <returns>Type name with normalized generic arguments</returns>
        private string NormalizeGenericArgs(string typeName) {
            // If there are no generic arguments, return as is
            if (!typeName.Contains("<")) {
                return typeName;
            }

            // Match angle bracket content, keeping the brackets
            return Regex.Replace(typeName, @"<([^<>]*)>", match => {
                // Replace the content with a normalized form
                string content = match.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(content)) {
                    return "<>";
                }

                // Handle multiple type arguments separated by commas
                var args = content.Split(',').Select(arg => "T").ToArray();
                return $"<{string.Join(",", args)}>";
            });
        }

        /// <summary>
        /// Completely strips generic arguments from a type name.
        /// </summary>
        /// <param name="typeName">The type name with generic arguments</param>
        /// <returns>Type name without generic arguments</returns>
        private string StripGenericArgs(string typeName) {
            // First, remove Roslyn-style arity indicators like List`1
            string withoutArity = ArityRegex.Replace(typeName, "");

            // Then remove angle bracket content including brackets
            return GenericArgsRegex.Replace(withoutArity, "");
        }
    }
}