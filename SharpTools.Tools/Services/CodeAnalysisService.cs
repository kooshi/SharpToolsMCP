using TypeInfo = Microsoft.CodeAnalysis.TypeInfo;
namespace SharpTools.Tools.Services;

public class CodeAnalysisService : ICodeAnalysisService
{
    private readonly ISolutionManager _solutionManager;
    private readonly ILogger<CodeAnalysisService> _logger;

    public CodeAnalysisService(ISolutionManager solutionManager, ILogger<CodeAnalysisService> logger)
    {
        _solutionManager = solutionManager ?? throw new ArgumentNullException(nameof(solutionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private Solution GetCurrentSolutionOrThrow()
    {
        if (_solutionManager.IsSolutionLoaded == false)
        {
            throw new InvalidOperationException("No solution is currently loaded.");
        }

        return _solutionManager.CurrentSolution;
    }

    public async Task<IEnumerable<ISymbol>> FindImplementationsAsync(
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        Solution solution = GetCurrentSolutionOrThrow();
        _logger.LogDebug("Finding implementations for symbol: {SymbolName}", symbol.Name);
        return await SymbolFinder.FindImplementationsAsync(
            symbol, solution, cancellationToken: cancellationToken);
    }

    public async Task<IEnumerable<ISymbol>> FindOverridesAsync(
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        Solution solution = GetCurrentSolutionOrThrow();
        _logger.LogDebug("Finding overrides for symbol: {SymbolName}", symbol.Name);
        return await SymbolFinder.FindOverridesAsync(
            symbol, solution, cancellationToken: cancellationToken);
    }

    public async Task<IEnumerable<ReferencedSymbol>> FindReferencesAsync(
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        Solution solution = GetCurrentSolutionOrThrow();
        _logger.LogDebug("Finding references for symbol: {SymbolName}", symbol.Name);
        return await SymbolFinder.FindReferencesAsync(
            symbol, solution, cancellationToken: cancellationToken);
    }

    public async Task<IEnumerable<INamedTypeSymbol>> FindDerivedClassesAsync(
        INamedTypeSymbol typeSymbol,
        CancellationToken cancellationToken)
    {
        Solution solution = GetCurrentSolutionOrThrow();
        _logger.LogDebug("Finding derived classes for type: {TypeName}", typeSymbol.Name);
        return await SymbolFinder.FindDerivedClassesAsync(
            typeSymbol, solution, cancellationToken: cancellationToken);
    }

    public async Task<IEnumerable<INamedTypeSymbol>> FindDerivedInterfacesAsync(
        INamedTypeSymbol typeSymbol,
        CancellationToken cancellationToken)
    {
        Solution solution = GetCurrentSolutionOrThrow();
        _logger.LogDebug("Finding derived interfaces for type: {TypeName}", typeSymbol.Name);
        return await SymbolFinder.FindDerivedInterfacesAsync(
            typeSymbol, solution, cancellationToken: cancellationToken);
    }

    public async Task<IEnumerable<SymbolCallerInfo>> FindCallersAsync(
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        Solution solution = GetCurrentSolutionOrThrow();
        _logger.LogDebug("Finding callers for symbol: {SymbolName}", symbol.Name);
        return await SymbolFinder.FindCallersAsync(
            symbol, solution, cancellationToken: cancellationToken);
    }

    public async Task<IEnumerable<ISymbol>> FindOutgoingCallsAsync(
        IMethodSymbol methodSymbol,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Finding outgoing calls for method: {MethodName}", methodSymbol.Name);
        List<ISymbol> outgoingCalls = new();
        if (methodSymbol.DeclaringSyntaxReferences.Any() == false)
        {
            _logger.LogWarning(
                "Method {MethodName} has no declaring syntax references, cannot find outgoing calls.",
                methodSymbol.Name);
            return outgoingCalls;
        }

        Solution currentSolution = GetCurrentSolutionOrThrow();
        foreach (SyntaxReference syntaxRef in methodSymbol.DeclaringSyntaxReferences)
        {
            MethodDeclarationSyntax? methodNode =
                await syntaxRef.GetSyntaxAsync(cancellationToken) as MethodDeclarationSyntax;
            if (methodNode?.Body == null && methodNode?.ExpressionBody == null)
            {
                continue;
            }

            Document? document = currentSolution.GetDocument(syntaxRef.SyntaxTree);
            if (document == null)
            {
                _logger.LogWarning(
                    "Could not get document for syntax tree {FilePath} of method {MethodName}",
                    syntaxRef.SyntaxTree.FilePath,
                    methodSymbol.Name);
                continue;
            }

            SemanticModel? semanticModel =
                await _solutionManager.GetSemanticModelAsync(document.Id, cancellationToken);
            if (semanticModel == null)
            {
                _logger.LogWarning(
                    "Could not get semantic model for method {MethodName} in document {DocumentPath}",
                    methodSymbol.Name,
                    document.FilePath);
                continue;
            }

            InvocationWalker walker = new(semanticModel, cancellationToken);
            walker.Visit(methodNode);
            outgoingCalls.AddRange(walker.CalledSymbols);
        }

        return outgoingCalls.Distinct(SymbolEqualityComparer.Default).ToList();
    }

    public static string GetFormattedSignatureAsync(ISymbol symbol, bool includeContainingType = true)
    {
        SymbolDisplayFormat fullFormat = new(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: includeContainingType
                ? SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces
                : SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
                | SymbolDisplayGenericsOptions.IncludeVariance,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                | SymbolDisplayMemberOptions.IncludeType
                | SymbolDisplayMemberOptions.IncludeRef
                | SymbolDisplayMemberOptions.IncludeContainingType,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType
                | SymbolDisplayParameterOptions.IncludeName
                | SymbolDisplayParameterOptions.IncludeParamsRefOut
                | SymbolDisplayParameterOptions.IncludeDefaultValue,
            propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
            localOptions: SymbolDisplayLocalOptions.None,
            kindOptions: SymbolDisplayKindOptions.IncludeMemberKeyword
                | SymbolDisplayKindOptions.IncludeNamespaceKeyword
                | SymbolDisplayKindOptions.IncludeTypeKeyword,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        SymbolDisplayFormat shortFormat = new(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                | SymbolDisplayMemberOptions.IncludeType
                | SymbolDisplayMemberOptions.IncludeRef,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType
                | SymbolDisplayParameterOptions.IncludeName
                | SymbolDisplayParameterOptions.IncludeParamsRefOut
                | SymbolDisplayParameterOptions.IncludeDefaultValue,
            propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
            localOptions: SymbolDisplayLocalOptions.None,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        if (symbol is IMethodSymbol methodSymbol)
        {
            IEnumerable<SymbolDisplayPart> methodParts = methodSymbol.ToDisplayParts(fullFormat);

            int returnTypeEndIndex =
                methodParts.TakeWhile(p => p.Kind != SymbolDisplayPartKind.MethodName).Count();
            int nameStartIndex = returnTypeEndIndex;
            int nameEndIndex = methodParts
                .TakeWhile(p => p.Kind != SymbolDisplayPartKind.Punctuation || p.ToString() != "(")
                .Count();

            IEnumerable<SymbolDisplayPart> modifiers =
                methodParts.Take(methodParts.TakeWhile(p => p.Kind == SymbolDisplayPartKind.Keyword).Count());
            string returnType = methodSymbol.ReturnType.ToDisplayString(shortFormat);
            string nameAndContainingType =
                string.Concat(methodParts.Skip(nameStartIndex).Take(nameEndIndex - nameStartIndex));
            string parameters =
                string.Join(", ", methodSymbol.Parameters.Select(p => p.ToDisplayString(shortFormat)));

            string signature =
                string.Concat(modifiers) + " " + returnType + " " + nameAndContainingType
                    + "(" + parameters + ")";
            return signature.Replace("  ", " ");
        }

        return symbol.ToDisplayString(fullFormat);
    }

    public Task<string?> GetXmlDocumentationAsync(ISymbol symbol, CancellationToken cancellationToken)
    {
        string? commentXml = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken);
        return Task.FromResult(string.IsNullOrEmpty(commentXml) ? null : commentXml);
    }

    private class InvocationWalker : CSharpSyntaxWalker
    {
        private readonly SemanticModel _semanticModel;
        private readonly CancellationToken _cancellationToken;
        private readonly List<ISymbol> _calledSymbols = new();

        public IEnumerable<ISymbol> CalledSymbols => _calledSymbols;

        public InvocationWalker(SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            _semanticModel = semanticModel;
            _cancellationToken = cancellationToken;
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            SymbolInfo symbolInfo = _semanticModel.GetSymbolInfo(node.Expression, _cancellationToken);
            AddSymbol(symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault());
            base.VisitInvocationExpression(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            SymbolInfo symbolInfo = _semanticModel.GetSymbolInfo(node.Type, _cancellationToken);
            AddSymbol(symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault());
            base.VisitObjectCreationExpression(node);
        }

        private void AddSymbol(ISymbol? symbol)
        {
            if (symbol != null)
            {
                _calledSymbols.Add(symbol);
            }
        }
    }

    private static void AddReferencedType(
        ITypeSymbol typeSymbol,
        INamedTypeSymbol sourceType,
        HashSet<string> referencedTypes,
        CancellationToken cancellationToken)
    {
        if (SymbolEqualityComparer.Default.Equals(typeSymbol, sourceType))
        {
            return;
        }

        if (typeSymbol.IsAnonymousType
            || typeSymbol is ITypeParameterSymbol
            || typeSymbol.TypeKind == TypeKind.Error)
        {
            return;
        }

        if (typeSymbol.SpecialType != SpecialType.None
            && typeSymbol.SpecialType != SpecialType.System_Object
            && typeSymbol.SpecialType != SpecialType.System_ValueType
            && typeSymbol.SpecialType != SpecialType.System_Enum)
        {
            return;
        }

        bool isInSolution = typeSymbol.ContainingAssembly != null
            && typeSymbol.ContainingAssembly.Locations.Any(loc => loc.IsInSource);

        if (isInSolution == false)
        {
            return;
        }

        if (typeSymbol is INamedTypeSymbol namedTypeSymbol)
        {
            string typeFqn = FuzzyFqnLookupService.GetSearchableString(namedTypeSymbol);
            referencedTypes.Add(typeFqn);

            if (namedTypeSymbol.IsGenericType)
            {
                foreach (ITypeSymbol typeArg in namedTypeSymbol.TypeArguments)
                {
                    if (typeArg is INamedTypeSymbol namedTypeArg)
                    {
                        AddReferencedType(namedTypeArg, sourceType, referencedTypes, cancellationToken);
                    }
                }
            }
        }
        else if (typeSymbol is IArrayTypeSymbol arrayType && arrayType.ElementType != null)
        {
            AddReferencedType(arrayType.ElementType, sourceType, referencedTypes, cancellationToken);
        }
        else if (typeSymbol is IPointerTypeSymbol pointerType && pointerType.PointedAtType != null)
        {
            AddReferencedType(pointerType.PointedAtType, sourceType, referencedTypes, cancellationToken);
        }
    }

    public async Task<HashSet<string>> FindReferencedTypesAsync(
        INamedTypeSymbol typeSymbol,
        CancellationToken cancellationToken)
    {
        Solution solution = GetCurrentSolutionOrThrow();
        HashSet<string> referencedTypes = new(StringComparer.Ordinal);

        if (typeSymbol == null)
        {
            _logger.LogWarning("Cannot analyze referenced types: Type symbol is null.");
            return referencedTypes;
        }

        try
        {
            if (typeSymbol.BaseType != null
                && typeSymbol.BaseType.SpecialType != SpecialType.System_Object)
            {
                AddReferencedType(typeSymbol.BaseType, typeSymbol, referencedTypes, cancellationToken);
            }

            foreach (INamedTypeSymbol iface in typeSymbol.Interfaces)
            {
                AddReferencedType(iface, typeSymbol, referencedTypes, cancellationToken);
            }

            foreach (ISymbol member in typeSymbol.GetMembers())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (member.IsImplicitlyDeclared || member.IsOverride)
                {
                    continue;
                }

                if (member is IFieldSymbol fieldSymbol && fieldSymbol.Type != null)
                {
                    AddReferencedType(fieldSymbol.Type, typeSymbol, referencedTypes, cancellationToken);
                }
                else if (member is IPropertySymbol propertySymbol && propertySymbol.Type != null)
                {
                    AddReferencedType(propertySymbol.Type, typeSymbol, referencedTypes, cancellationToken);
                }
                else if (member is IMethodSymbol methodSymbol)
                {
                    if (methodSymbol.ReturnType != null)
                    {
                        AddReferencedType(
                            methodSymbol.ReturnType, typeSymbol, referencedTypes, cancellationToken);
                    }

                    foreach (IParameterSymbol parameter in methodSymbol.Parameters)
                    {
                        if (parameter.Type != null)
                        {
                            AddReferencedType(
                                parameter.Type, typeSymbol, referencedTypes, cancellationToken);
                        }
                    }
                }
                else if (member is IEventSymbol eventSymbol && eventSymbol.Type != null)
                {
                    AddReferencedType(eventSymbol.Type, typeSymbol, referencedTypes, cancellationToken);
                }

                if (member.DeclaringSyntaxReferences.Any())
                {
                    foreach (SyntaxReference syntaxRef in member.DeclaringSyntaxReferences)
                    {
                        SyntaxNode memberNode = await syntaxRef.GetSyntaxAsync(cancellationToken);
                        if (memberNode == null)
                        {
                            continue;
                        }

                        Document? document = solution.GetDocument(syntaxRef.SyntaxTree);
                        if (document == null)
                        {
                            continue;
                        }

                        SemanticModel? semanticModel =
                            await document.GetSemanticModelAsync(cancellationToken);
                        if (semanticModel == null)
                        {
                            continue;
                        }

                        IEnumerable<SyntaxNode> descendantNodes = memberNode.DescendantNodes();
                        foreach (SyntaxNode node in descendantNodes)
                        {
                            if (node is TypeDeclarationSyntax)
                            {
                                continue;
                            }

                            SymbolInfo symbolInfo =
                                semanticModel.GetSymbolInfo(node, cancellationToken);
                            ISymbol? referencedSymbol =
                                symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

                            if (referencedSymbol != null)
                            {
                                ITypeSymbol? definingType = null;

                                if (referencedSymbol is ITypeSymbol typeRef)
                                {
                                    definingType = typeRef;
                                }
                                else if (referencedSymbol is IFieldSymbol fieldRef)
                                {
                                    definingType = fieldRef.Type;
                                    if (fieldRef.ContainingType != null)
                                    {
                                        AddReferencedType(
                                            fieldRef.ContainingType,
                                            typeSymbol,
                                            referencedTypes,
                                            cancellationToken);
                                    }
                                }
                                else if (referencedSymbol is IPropertySymbol propertyRef)
                                {
                                    definingType = propertyRef.Type;
                                    if (propertyRef.ContainingType != null)
                                    {
                                        AddReferencedType(
                                            propertyRef.ContainingType,
                                            typeSymbol,
                                            referencedTypes,
                                            cancellationToken);
                                    }
                                }
                                else if (referencedSymbol is IMethodSymbol methodRef)
                                {
                                    definingType = methodRef.ReturnType;
                                    if (methodRef.ContainingType != null)
                                    {
                                        AddReferencedType(
                                            methodRef.ContainingType,
                                            typeSymbol,
                                            referencedTypes,
                                            cancellationToken);
                                    }

                                    foreach (IParameterSymbol param in methodRef.Parameters)
                                    {
                                        if (param.Type != null)
                                        {
                                            AddReferencedType(
                                                param.Type,
                                                typeSymbol,
                                                referencedTypes,
                                                cancellationToken);
                                        }
                                    }
                                }
                                else if (referencedSymbol is ILocalSymbol localRef)
                                {
                                    definingType = localRef.Type;
                                }
                                else if (referencedSymbol is IParameterSymbol paramRef)
                                {
                                    definingType = paramRef.Type;
                                }
                                else if (referencedSymbol is IEventSymbol eventRef)
                                {
                                    definingType = eventRef.Type;
                                    if (eventRef.ContainingType != null)
                                    {
                                        AddReferencedType(
                                            eventRef.ContainingType,
                                            typeSymbol,
                                            referencedTypes,
                                            cancellationToken);
                                    }
                                }

                                if (definingType != null)
                                {
                                    AddReferencedType(
                                        definingType, typeSymbol, referencedTypes, cancellationToken);
                                }
                            }

                            TypeInfo typeInfo = semanticModel.GetTypeInfo(node, cancellationToken);
                            if (typeInfo.Type != null)
                            {
                                AddReferencedType(
                                    typeInfo.Type, typeSymbol, referencedTypes, cancellationToken);
                            }

                            if (typeInfo.ConvertedType != null
                                && SymbolEqualityComparer.Default.Equals(
                                    typeInfo.Type, typeInfo.ConvertedType) == false)
                            {
                                AddReferencedType(
                                    typeInfo.ConvertedType, typeSymbol, referencedTypes, cancellationToken);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when ((ex is OperationCanceledException) == false)
        {
            _logger.LogWarning(ex, "Error finding referenced types for type {TypeName}", typeSymbol.Name);
        }

        return referencedTypes;
    }
}
