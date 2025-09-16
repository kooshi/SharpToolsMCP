using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.IO.Compression;

namespace SharpTools.Tools.Services {
    public class EmbeddedSourceReader {
        // GUID for embedded source custom debug information
        private static readonly Guid EmbeddedSourceGuid = new Guid("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

        public class SourceResult {
            public string? SourceCode { get; set; }
            public string? FilePath { get; set; }
            public bool IsEmbedded { get; set; }
            public bool IsCompressed { get; set; }
        }

        /// <summary>
        /// Reads embedded source from a portable PDB file
        /// </summary>
        public static Dictionary<string, SourceResult> ReadEmbeddedSources(string pdbPath) {
            var results = new Dictionary<string, SourceResult>();

            using var fs = new FileStream(pdbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(fs);
            var reader = provider.GetMetadataReader();

            return ReadEmbeddedSources(reader);
        }

        /// <summary>
        /// Reads embedded source from an assembly with embedded PDB
        /// </summary>
        public static Dictionary<string, SourceResult> ReadEmbeddedSourcesFromAssembly(string assemblyPath) {
            using var fs = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var peReader = new PEReader(fs);

            // Check for embedded portable PDB
            var debugDirectories = peReader.ReadDebugDirectory();
            var embeddedPdbEntry = debugDirectories
                .FirstOrDefault(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);

            if (embeddedPdbEntry.DataSize == 0) {
                return new Dictionary<string, SourceResult>();
            }

            using var embeddedProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedPdbEntry);
            var pdbReader = embeddedProvider.GetMetadataReader();

            return ReadEmbeddedSources(pdbReader);
        }

        /// <summary>
        /// Core method to read embedded sources from a MetadataReader
        /// </summary>
        public static Dictionary<string, SourceResult> ReadEmbeddedSources(MetadataReader reader) {
            var results = new Dictionary<string, SourceResult>();

            // Get all documents
            var documents = new Dictionary<DocumentHandle, System.Reflection.Metadata.Document>();
            foreach (var docHandle in reader.Documents) {
                var doc = reader.GetDocument(docHandle);
                documents[docHandle] = doc;
            }

            // Look for embedded source in CustomDebugInformation
            foreach (var cdiHandle in reader.CustomDebugInformation) {
                var cdi = reader.GetCustomDebugInformation(cdiHandle);

                // Check if this is embedded source information
                var kind = reader.GetGuid(cdi.Kind);
                if (kind != EmbeddedSourceGuid)
                    continue;

                // The parent should be a Document
                if (cdi.Parent.Kind != HandleKind.Document)
                    continue;

                var docHandle = (DocumentHandle)cdi.Parent;
                if (!documents.TryGetValue(docHandle, out var document))
                    continue;

                // Get the document name
                var fileName = GetDocumentName(reader, document.Name);

                // Read the embedded source content
                var sourceContent = ReadEmbeddedSourceContent(reader, cdi.Value);

                if (sourceContent != null) {
                    results[fileName] = sourceContent;
                }
            }

            return results;
        }

        /// <summary>
        /// Reads the actual embedded source content from the blob
        /// </summary>
        private static SourceResult? ReadEmbeddedSourceContent(MetadataReader reader, BlobHandle blobHandle) {
            var blobReader = reader.GetBlobReader(blobHandle);

            // Read the format indicator (first 4 bytes)
            var format = blobReader.ReadInt32();

            // Get remaining bytes
            var remainingLength = blobReader.Length - blobReader.Offset;
            var contentBytes = blobReader.ReadBytes(remainingLength);

            string sourceText;
            bool isCompressed = false;

            if (format == 0) {
                // Uncompressed UTF-8 text
                sourceText = Encoding.UTF8.GetString(contentBytes);
            } else if (format > 0) {
                // Compressed with deflate, format contains uncompressed size
                isCompressed = true;
                using var compressed = new MemoryStream(contentBytes);
                using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
                using var decompressed = new MemoryStream();

                deflate.CopyTo(decompressed);
                sourceText = Encoding.UTF8.GetString(decompressed.ToArray());
            } else {
                // Reserved for future formats
                return null;
            }

            return new SourceResult {
                SourceCode = sourceText,
                IsEmbedded = true,
                IsCompressed = isCompressed
            };
        }

        /// <summary>
        /// Reconstructs the document name from the portable PDB format
        /// </summary>
        private static string GetDocumentName(MetadataReader reader, DocumentNameBlobHandle handle) {
            var blobReader = reader.GetBlobReader(handle);
            var separator = (char)blobReader.ReadByte();

            var sb = new StringBuilder();
            bool first = true;

            while (blobReader.Offset < blobReader.Length) {
                var partHandle = blobReader.ReadBlobHandle();
                if (!partHandle.IsNil) {
                    if (!first)
                        sb.Append(separator);

                    var nameBytes = reader.GetBlobBytes(partHandle);
                    sb.Append(Encoding.UTF8.GetString(nameBytes));
                    first = false;
                }
            }

            return sb.ToString();
        }
        /// <summary>
        /// Helper method to get source for a specific symbol from Roslyn
        /// </summary>
        public static SourceResult? GetEmbeddedSourceForSymbol(Microsoft.CodeAnalysis.ISymbol symbol) {
            // Get the assembly containing the symbol
            var assembly = symbol.ContainingAssembly;
            if (assembly == null)
                return null;

            // Get the locations from the symbol
            var locations = symbol.Locations;
            foreach (var location in locations) {
                if (location.IsInMetadata && location.MetadataModule != null) {
                    var moduleName = location.MetadataModule.Name;

                    // Try to find the defining document for this symbol
                    string symbolFileName = moduleName;

                    // For types, properties, methods, etc., use a more specific name
                    if (symbol is Microsoft.CodeAnalysis.INamedTypeSymbol namedType) {
                        symbolFileName = $"{namedType.Name}.cs";
                    } else if (symbol.ContainingType != null) {
                        symbolFileName = $"{symbol.ContainingType.Name}.cs";
                    }

                    // Check if we can find embedded source for this symbol
                    // The actual PDB path lookup will be handled by the calling code
                    return new SourceResult {
                        FilePath = symbolFileName,
                        IsEmbedded = true,
                        IsCompressed = false
                    };
                }
            }

            // If we reach here, we couldn't determine the assembly location directly
            return null;
        }
    }
}