namespace SharpTools.Tools.Interfaces;

public interface ICodeAnalysisService
{
    Task<IEnumerable<ISymbol>> FindImplementationsAsync(ISymbol symbol, CancellationToken cancellationToken);
    Task<IEnumerable<ISymbol>> FindOverridesAsync(ISymbol symbol, CancellationToken cancellationToken);
    Task<IEnumerable<ReferencedSymbol>> FindReferencesAsync(ISymbol symbol, CancellationToken cancellationToken);
    Task<IEnumerable<INamedTypeSymbol>> FindDerivedClassesAsync(
        INamedTypeSymbol typeSymbol,
        CancellationToken cancellationToken);
    Task<IEnumerable<INamedTypeSymbol>> FindDerivedInterfacesAsync(
        INamedTypeSymbol typeSymbol,
        CancellationToken cancellationToken);
    Task<IEnumerable<SymbolCallerInfo>> FindCallersAsync(ISymbol symbol, CancellationToken cancellationToken);
    Task<IEnumerable<ISymbol>> FindOutgoingCallsAsync(IMethodSymbol methodSymbol, CancellationToken cancellationToken);
    Task<string?> GetXmlDocumentationAsync(ISymbol symbol, CancellationToken cancellationToken);
    Task<HashSet<string>> FindReferencedTypesAsync(INamedTypeSymbol typeSymbol, CancellationToken cancellationToken);
}
