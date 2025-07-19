
namespace SharpTools.Tools.Interfaces {
    public class SourceResult {
        public string Source { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public bool IsOriginalSource { get; set; }
        public bool IsDecompiled { get; set; }
        public string ResolutionMethod { get; set; } = string.Empty;
    }
    public interface ISourceResolutionService {
        /// <summary>
        /// Resolves source code for a symbol through various methods (Source Link, embedded source, decompilation)
        /// </summary>
        /// <param name="symbol">The symbol to resolve source for</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Source result containing the resolved source code and metadata</returns>
        Task<SourceResult?> ResolveSourceAsync(Microsoft.CodeAnalysis.ISymbol symbol, CancellationToken cancellationToken);

        /// <summary>
        /// Tries to get source via Source Link information in PDBs
        /// </summary>
        /// <param name="symbol">The symbol to resolve source for</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Source result if successful, null otherwise</returns>
        Task<SourceResult?> TrySourceLinkAsync(Microsoft.CodeAnalysis.ISymbol symbol, CancellationToken cancellationToken);

        /// <summary>
        /// Tries to get embedded source from the assembly
        /// </summary>
        /// <param name="symbol">The symbol to resolve source for</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Source result if successful, null otherwise</returns>
        Task<SourceResult?> TryEmbeddedSourceAsync(Microsoft.CodeAnalysis.ISymbol symbol, CancellationToken cancellationToken);

        /// <summary>
        /// Tries to decompile the symbol from its metadata
        /// </summary>
        /// <param name="symbol">The symbol to resolve source for</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Source result if successful, null otherwise</returns>
        Task<SourceResult?> TryDecompilationAsync(Microsoft.CodeAnalysis.ISymbol symbol, CancellationToken cancellationToken);
    }
}