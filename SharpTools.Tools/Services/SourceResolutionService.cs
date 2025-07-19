using System.Reflection.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

namespace SharpTools.Tools.Services {
    public class SourceResolutionService : ISourceResolutionService {
        private readonly ISolutionManager _solutionManager;
        private readonly ILogger<SourceResolutionService> _logger;
        private readonly HttpClient _httpClient;

        public SourceResolutionService(ISolutionManager solutionManager, ILogger<SourceResolutionService> logger) {
            _solutionManager = solutionManager ?? throw new ArgumentNullException(nameof(solutionManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = new HttpClient();
        }

        public async Task<SourceResult?> ResolveSourceAsync(Microsoft.CodeAnalysis.ISymbol symbol, CancellationToken cancellationToken) {
            if (symbol == null) {
                _logger.LogWarning("Cannot resolve source: Symbol is null");
                return null;
            }

            // 1. Try to get from syntax references (source available)
            if (symbol.DeclaringSyntaxReferences.Length > 0) {
                var syntaxRef = symbol.DeclaringSyntaxReferences[0];
                var sourceText = await syntaxRef.GetSyntaxAsync(cancellationToken);
                if (sourceText != null) {
                    var tree = syntaxRef.SyntaxTree;
                    return new SourceResult {
                        Source = sourceText.ToString(),
                        FilePath = tree.FilePath,
                        IsOriginalSource = true,
                        IsDecompiled = false,
                        ResolutionMethod = "Local Source"
                    };
                }
            }

            // 2. Try Source Link
            var sourceLinkResult = await TrySourceLinkAsync(symbol, cancellationToken);
            if (sourceLinkResult != null) return sourceLinkResult;

            // 3. Try embedded source
            var embeddedResult = await TryEmbeddedSourceAsync(symbol, cancellationToken);
            if (embeddedResult != null) return embeddedResult;

            // 4. Try decompilation as fallback
            var decompiledResult = await TryDecompilationAsync(symbol, cancellationToken);
            if (decompiledResult != null) return decompiledResult;

            return null;
        }

        public async Task<SourceResult?> TrySourceLinkAsync(Microsoft.CodeAnalysis.ISymbol symbol, CancellationToken cancellationToken) {
            _logger.LogInformation("Attempting to retrieve source via Source Link for {SymbolName}", symbol.Name);
            try {
                // Get location of the assembly containing the symbol
                var assembly = symbol.ContainingAssembly;
                if (assembly == null) {
                    _logger.LogWarning("No containing assembly found for symbol {SymbolName}", symbol.Name);
                    return null;
                }

                // Find the PE reference for this assembly
                var metadataReference = GetMetadataReferenceForAssembly(assembly);
                if (metadataReference == null) {
                    _logger.LogWarning("No metadata reference found for assembly {AssemblyName}", assembly.Name);
                    return null;
                }

                // Check for PDB adjacent to the DLL
                var dllPath = metadataReference.Display;
                if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath)) {
                    _logger.LogWarning("Assembly file not found: {DllPath}", dllPath);
                    return null;
                }

                var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
                if (!File.Exists(pdbPath)) {
                    _logger.LogWarning("PDB file not found: {PdbPath}", pdbPath);
                    return null;
                }

                _logger.LogInformation("Found PDB file: {PdbPath}", pdbPath);

                // Open the PDB and look for Source Link information
                using var pdbStream = File.OpenRead(pdbPath);
                using var metadataReaderProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
                var metadataReader = metadataReaderProvider.GetMetadataReader();

                // Extract Source Link JSON document
                string? sourceLinkJson = null;
                foreach (var customDebugInfoHandle in metadataReader.CustomDebugInformation) {
                    var customDebugInfo = metadataReader.GetCustomDebugInformation(customDebugInfoHandle);
                    var kind = metadataReader.GetGuid(customDebugInfo.Kind);


                    // Source Link kind GUID
                    if (kind == new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A")) {
                        var blobReader = metadataReader.GetBlobReader(customDebugInfo.Value);
                        sourceLinkJson = Encoding.UTF8.GetString(blobReader.ReadBytes(blobReader.Length));
                        break;
                    }
                }

                if (string.IsNullOrEmpty(sourceLinkJson)) {
                    _logger.LogWarning("No Source Link information found in PDB");
                    return null;
                }

                _logger.LogInformation("Found Source Link JSON: {Json}", sourceLinkJson);

                // Parse the JSON and extract source URLs
                var sourceLinkDoc = System.Text.Json.JsonDocument.Parse(sourceLinkJson);
                var urlsElement = sourceLinkDoc.RootElement.GetProperty("documents");

                // Get the document containing our symbol
                string symbolDocumentPath = GetSymbolDocumentPath(symbol);
                if (string.IsNullOrEmpty(symbolDocumentPath)) {
                    _logger.LogWarning("Could not determine document path for symbol {SymbolName}", symbol.Name);
                    return null;
                }

                // Normalize path for comparison with Source Link entries
                symbolDocumentPath = symbolDocumentPath.Replace('\\', '/');

                // Find matching URL in Source Link data
                string? sourceUrl = null;
                foreach (var property in urlsElement.EnumerateObject()) {
                    var pattern = property.Name;
                    var url = property.Value.GetString();

                    // Source Link uses wildcard patterns like "C:/Projects/*" -> "https://raw.githubusercontent.com/user/repo/*"
                    if (IsPathMatch(symbolDocumentPath, pattern) && !string.IsNullOrEmpty(url)) {
                        // Replace the wildcard part in the URL
                        sourceUrl = url.Replace("*", GetWildcardMatch(symbolDocumentPath, pattern));
                        break;
                    }
                }

                if (string.IsNullOrEmpty(sourceUrl)) {
                    _logger.LogWarning("No matching source URL found for document {Path}", symbolDocumentPath);
                    return null;
                }

                // Download the source from the URL
                _logger.LogInformation("Downloading source from URL: {Url}", sourceUrl);
                var sourceCode = await _httpClient.GetStringAsync(sourceUrl, cancellationToken);

                return new SourceResult {
                    Source = sourceCode,
                    FilePath = sourceUrl,
                    IsOriginalSource = true,
                    IsDecompiled = false,
                    ResolutionMethod = "Source Link"
                };
            } catch (Exception ex) {
                _logger.LogError(ex, "Error retrieving source via Source Link for {SymbolName}", symbol.Name);
                return null;
            }
        }
        public async Task<SourceResult?> TryEmbeddedSourceAsync(Microsoft.CodeAnalysis.ISymbol symbol, CancellationToken cancellationToken) {
            _logger.LogInformation("Attempting to retrieve embedded source for {SymbolName}", symbol.Name);
            try {
                cancellationToken.ThrowIfCancellationRequested();

                // Get location of the assembly containing the symbol
                var assembly = symbol.ContainingAssembly;
                if (assembly == null) {
                    _logger.LogWarning("No containing assembly found for symbol {SymbolName}", symbol.Name);
                    return null;
                }

                // Find the PE reference for this assembly
                var metadataReference = GetMetadataReferenceForAssembly(assembly);
                if (metadataReference == null) {
                    _logger.LogWarning("No metadata reference found for assembly {AssemblyName}", assembly.Name);
                    return null;
                }

                // Get the assembly path
                var assemblyPath = metadataReference.Display;
                if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath)) {
                    _logger.LogWarning("Assembly file not found: {AssemblyPath}", assemblyPath);
                    return null;
                }

                _logger.LogInformation("Checking for embedded source in assembly: {AssemblyPath}", assemblyPath);

                // Get embedded source information for this symbol
                var embeddedSourceInfo = EmbeddedSourceReader.GetEmbeddedSourceForSymbol(symbol);
                if (embeddedSourceInfo == null) {
                    _logger.LogInformation("No embedded source info available for {SymbolName}", symbol.Name);
                    return null;
                }

                // Check for PDB embedded in the assembly
                Dictionary<string, EmbeddedSourceReader.SourceResult> embeddedSources = new();
                try {
                    embeddedSources = await Task.Run(() => EmbeddedSourceReader.ReadEmbeddedSourcesFromAssembly(assemblyPath), cancellationToken);
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "Error reading embedded sources from assembly: {AssemblyPath}", assemblyPath);
                    // Continue to check standalone PDB
                }

                // If no embedded sources found, check for standalone PDB
                if (!embeddedSources.Any()) {
                    var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
                    if (File.Exists(pdbPath)) {
                        _logger.LogInformation("Checking standalone PDB file: {PdbPath}", pdbPath);

                        try {
                            // Read embedded sources in a background task to avoid blocking
                            embeddedSources = await Task.Run(() => EmbeddedSourceReader.ReadEmbeddedSources(pdbPath), cancellationToken);
                        } catch (Exception ex) {
                            _logger.LogDebug(ex, "Error reading embedded sources from PDB: {PdbPath}", pdbPath);
                        }
                    }
                }

                if (!embeddedSources.Any()) {
                    _logger.LogInformation("No embedded sources found in assembly or PDB for {SymbolName}", symbol.Name);
                    return null;
                }

                // Try to find matching source based on file name
                string symbolFileName = embeddedSourceInfo.FilePath ?? string.Empty;

                // Try exact match first
                if (!string.IsNullOrEmpty(symbolFileName) && embeddedSources.TryGetValue(symbolFileName, out var exactMatch)) {
                    _logger.LogInformation("Found exact matching source file: {FileName}", symbolFileName);
                    return new SourceResult {
                        Source = exactMatch.SourceCode ?? string.Empty,
                        FilePath = symbolFileName,
                        IsOriginalSource = true,
                        IsDecompiled = false,
                        ResolutionMethod = "Embedded Source (Exact Match)"
                    };
                }

                // Try filename match (ignoring path)
                string fileNameOnly = Path.GetFileName(symbolFileName);
                foreach (var source in embeddedSources) {
                    string sourceFileName = Path.GetFileName(source.Key);
                    if (string.Equals(sourceFileName, fileNameOnly, StringComparison.OrdinalIgnoreCase)) {
                        _logger.LogInformation("Found matching source file by name: {FileName}", sourceFileName);
                        return new SourceResult {
                            Source = source.Value.SourceCode ?? string.Empty,
                            FilePath = source.Key,
                            IsOriginalSource = true,
                            IsDecompiled = false,
                            ResolutionMethod = "Embedded Source (Filename Match)"
                        };
                    }
                }

                // If the symbol is a method, property, etc., try to find its containing type
                if (symbol.ContainingType != null) {
                    string containingTypeName = symbol.ContainingType.Name + ".cs";
                    foreach (var source in embeddedSources) {
                        string sourceFileName = Path.GetFileName(source.Key);
                        if (string.Equals(sourceFileName, containingTypeName, StringComparison.OrdinalIgnoreCase)) {
                            _logger.LogInformation("Found source file for containing type: {TypeName}", symbol.ContainingType.Name);
                            return new SourceResult {
                                Source = source.Value.SourceCode ?? string.Empty,
                                FilePath = source.Key,
                                IsOriginalSource = true,
                                IsDecompiled = false,
                                ResolutionMethod = "Embedded Source (Containing Type)"
                            };
                        }
                    }
                }

                // If we still don't have a match and there's just one source file, use it
                // This is common for small libraries with a single source file
                if (embeddedSources.Count == 1) {
                    var singleSource = embeddedSources.First();
                    _logger.LogInformation("Using single available source file: {FileName}", singleSource.Key);
                    return new SourceResult {
                        Source = singleSource.Value.SourceCode ?? string.Empty,
                        FilePath = singleSource.Key,
                        IsOriginalSource = true,
                        IsDecompiled = false,
                        ResolutionMethod = "Embedded Source (Single File)"
                    };
                }

                _logger.LogWarning("No matching embedded source found for symbol {SymbolName} among {Count} available files",
                    symbol.Name, embeddedSources.Count);
                return null;
            } catch (Exception ex) {
                _logger.LogError(ex, "Error retrieving embedded source for {SymbolName}", symbol.Name);
                return null;
            }
        }
        public async Task<SourceResult?> TryDecompilationAsync(Microsoft.CodeAnalysis.ISymbol symbol, CancellationToken cancellationToken) {
            _logger.LogInformation("Attempting decompilation for {SymbolName}", symbol.Name);
            try {
                // Get location of the assembly containing the symbol
                var assembly = symbol.ContainingAssembly;
                if (assembly == null) {
                    _logger.LogWarning("No containing assembly found for symbol {SymbolName}", symbol.Name);
                    return null;
                }

                // Find the PE reference for this assembly
                var metadataReference = GetMetadataReferenceForAssembly(assembly);
                if (metadataReference == null) {
                    _logger.LogWarning("No metadata reference found for assembly {AssemblyName}", assembly.Name);
                    return null;
                }

                var assemblyPath = metadataReference.Display;
                if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath)) {
                    _logger.LogWarning("Assembly file not found: {AssemblyPath}", assemblyPath);
                    return null;
                }

                _logger.LogInformation("Decompiling from assembly: {AssemblyPath}", assemblyPath);

                // Create settings for the decompiler
                var decompilerSettings = new DecompilerSettings {
                    ThrowOnAssemblyResolveErrors = false,
                    UseExpressionBodyForCalculatedGetterOnlyProperties = true,
                    UsingDeclarations = true,
                    NullPropagation = true,
                    AlwaysUseBraces = true,
                    RemoveDeadCode = true
                };

                // Decompilation can be CPU intensive, so run it in a background task
                return await Task.Run(() => {
                    try {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Create the decompiler
                        var decompiler = new CSharpDecompiler(assemblyPath, decompilerSettings);

                        // Process based on symbol type
                        string? typeFullName = null;
                        string? memberName = null;

                        if (symbol is Microsoft.CodeAnalysis.INamedTypeSymbol namedType) {
                            typeFullName = namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        } else if (symbol is Microsoft.CodeAnalysis.IMethodSymbol method) {
                            typeFullName = method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                            memberName = method.Name;
                        } else if (symbol is Microsoft.CodeAnalysis.IPropertySymbol property) {
                            typeFullName = property.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                            memberName = property.Name;
                        } else if (symbol is Microsoft.CodeAnalysis.IFieldSymbol field) {
                            typeFullName = field.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                            memberName = field.Name;
                        } else if (symbol is Microsoft.CodeAnalysis.IEventSymbol eventSymbol) {
                            typeFullName = eventSymbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                            memberName = eventSymbol.Name;
                        }

                        if (string.IsNullOrEmpty(typeFullName)) {
                            _logger.LogWarning("Could not determine type name for symbol {SymbolName}", symbol.Name);
                            return null;
                        }

                        // Clean up the type name for the decompiler
                        typeFullName = typeFullName.Replace("global::", "")
                            .Replace("<", "{")
                            .Replace(">", "}");

                        try {
                            // Try to decompile the type or member
                            string decompiled;
                            if (string.IsNullOrEmpty(memberName)) {
                                // Decompile entire type
                                decompiled = decompiler.DecompileTypeAsString(new FullTypeName(typeFullName));
                            } else {
                                // Decompile specific member
                                var typeDef = decompiler.TypeSystem.FindType(new FullTypeName(typeFullName))?.GetDefinition();
                                if (typeDef == null) {
                                    _logger.LogWarning("Could not find type definition for {TypeName}", typeFullName);
                                    return null;
                                }

                                var memberDef = typeDef.Members.FirstOrDefault(m => m.Name == memberName);
                                if (memberDef == null) {
                                    _logger.LogWarning("Could not find member {MemberName} in type {TypeName}", memberName, typeFullName);
                                    return null;
                                }

                                decompiled = decompiler.DecompileAsString(memberDef.MetadataToken);
                            }

                            return new SourceResult {
                                Source = decompiled,
                                FilePath = $"{typeFullName}.cs (decompiled)",
                                IsOriginalSource = false,
                                IsDecompiled = true,
                                ResolutionMethod = "Decompilation"
                            };
                        } catch (Exception ex) {
                            _logger.LogWarning(ex, "Error during specific decompilation for {SymbolName}, falling back to full type decompilation", symbol.Name);

                            // Fallback: try to decompile just the containing type
                            try {
                                cancellationToken.ThrowIfCancellationRequested();

                                var containingTypeFullName = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                                if (!string.IsNullOrEmpty(containingTypeFullName)) {
                                    containingTypeFullName = containingTypeFullName.Replace("global::", "")
                                        .Replace("<", "{")
                                        .Replace(">", "}");

                                    var decompiled = decompiler.DecompileTypeAsString(new FullTypeName(containingTypeFullName));
                                    return new SourceResult {
                                        Source = decompiled,
                                        FilePath = $"{containingTypeFullName}.cs (decompiled)",
                                        IsOriginalSource = false,
                                        IsDecompiled = true,
                                        ResolutionMethod = "Decompilation (Fallback)"
                                    };
                                }
                            } catch (Exception innerEx) {
                                _logger.LogError(innerEx, "Fallback decompilation failed for {SymbolName}", symbol.Name);
                            }
                        }

                        return null;
                    } catch (Exception ex) {
                        _logger.LogError(ex, "Error during decompilation for {SymbolName}", symbol.Name);
                        return null;
                    }
                }, cancellationToken);
            } catch (Exception ex) {
                _logger.LogError(ex, "Error during decompilation for {SymbolName}", symbol.Name);
                return null;
            }
        }
        #region Helper Methods

        private PortableExecutableReference? GetMetadataReferenceForAssembly(IAssemblySymbol assembly) {
            if (!_solutionManager.IsSolutionLoaded) {
                _logger.LogWarning("Cannot get metadata reference: Solution not loaded");
                return null;
            }

            foreach (var project in _solutionManager.GetProjects()) {
                foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>()) {
                    if (Path.GetFileNameWithoutExtension(reference.FilePath) == assembly.Name) {
                        return reference;
                    }
                }
            }

            return null;
        }

        private string GetSymbolDocumentPath(Microsoft.CodeAnalysis.ISymbol symbol) {
            // For symbols with syntax references, get the file path directly
            if (symbol.DeclaringSyntaxReferences.Length > 0) {
                var syntaxRef = symbol.DeclaringSyntaxReferences[0];
                return syntaxRef.SyntaxTree.FilePath;
            }

            // For metadata symbols, try to infer document path
            // This is a simplistic approach and might not work in all cases
            return $"{symbol.ContainingType?.Name ?? symbol.Name}.cs";
        }

        private bool IsPathMatch(string path, string pattern) {
            // Source Link uses patterns with * wildcards
            if (!pattern.Contains('*')) {
                return string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase);
            }

            // Simple wildcard matching for patterns like "C:/Projects/*"
            var prefix = pattern.Substring(0, pattern.IndexOf('*'));
            var suffix = pattern.Substring(pattern.IndexOf('*') + 1);

            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            (suffix.Length == 0 || path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        private string GetWildcardMatch(string path, string pattern) {
            var prefix = pattern.Substring(0, pattern.IndexOf('*'));
            var suffix = pattern.Substring(pattern.IndexOf('*') + 1);

            if (suffix.Length == 0) {
                return path.Substring(prefix.Length);
            }

            return path.Substring(prefix.Length, path.Length - prefix.Length - suffix.Length);
        }

        #endregion
    }
}