namespace SharpTools.Tools.Interfaces;

public interface ISourceResolutionService
{
    /// <summary>
    /// Resolves source code for a symbol through various methods (Source Link, embedded source, decompilation)
    /// </summary>
    /// <param name="symbol">The symbol to resolve source for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Source result containing the resolved source code and metadata</returns>
    Task<SourceResult?> ResolveSourceAsync(ISymbol symbol, CancellationToken cancellationToken);

    /// <summary>
    /// Tries to get source via Source Link information in PDBs
    /// </summary>
    /// <param name="symbol">The symbol to resolve source for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Source result if successful, null otherwise</returns>
    Task<SourceResult?> TrySourceLinkAsync(ISymbol symbol, CancellationToken cancellationToken);

    /// <summary>
    /// Tries to get embedded source from the assembly
    /// </summary>
    /// <param name="symbol">The symbol to resolve source for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Source result if successful, null otherwise</returns>
    Task<SourceResult?> TryEmbeddedSourceAsync(ISymbol symbol, CancellationToken cancellationToken);

    /// <summary>
    /// Tries to decompile the symbol from its metadata
    /// </summary>
    /// <param name="symbol">The symbol to resolve source for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Source result if successful, null otherwise</returns>
    Task<SourceResult?> TryDecompilationAsync(ISymbol symbol, CancellationToken cancellationToken);
}
