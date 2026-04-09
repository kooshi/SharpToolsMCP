using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SharpTools.Tools.Services;

public class EmbeddedSourceReader
{
    // GUID for embedded source custom debug information
    private static readonly Guid s_embeddedSourceGuid = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

    public class SourceResult
    {
        public string? SourceCode { get; set; }
        public string? FilePath { get; set; }
        public bool IsEmbedded { get; set; }
        public bool IsCompressed { get; set; }
    }

    /// <summary>
    /// Reads embedded source from a portable PDB file
    /// </summary>
    public static Dictionary<string, SourceResult> ReadEmbeddedSources(string pdbPath)
    {
        using FileStream fs = new(pdbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(fs);
        MetadataReader reader = provider.GetMetadataReader();

        return ReadEmbeddedSources(reader);
    }

    /// <summary>
    /// Reads embedded source from an assembly with embedded PDB
    /// </summary>
    public static Dictionary<string, SourceResult> ReadEmbeddedSourcesFromAssembly(string assemblyPath)
    {
        using FileStream fs = new(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using PEReader peReader = new(fs);

        // Check for embedded portable PDB
        ImmutableArray<DebugDirectoryEntry> debugDirectories = peReader.ReadDebugDirectory();
        DebugDirectoryEntry embeddedPdbEntry = debugDirectories
            .FirstOrDefault(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);

        if (embeddedPdbEntry.DataSize == 0)
        {
            return [];
        }

        using MetadataReaderProvider embeddedProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedPdbEntry);
        MetadataReader pdbReader = embeddedProvider.GetMetadataReader();

        return ReadEmbeddedSources(pdbReader);
    }

    /// <summary>
    /// Core method to read embedded sources from a MetadataReader
    /// </summary>
    public static Dictionary<string, SourceResult> ReadEmbeddedSources(MetadataReader reader)
    {
        Dictionary<string, SourceResult> results = [];

        // Get all documents
        Dictionary<DocumentHandle, System.Reflection.Metadata.Document> documents = [];

        foreach (DocumentHandle docHandle in reader.Documents)
        {
            System.Reflection.Metadata.Document doc = reader.GetDocument(docHandle);
            documents[docHandle] = doc;
        }

        // Look for embedded source in CustomDebugInformation
        foreach (CustomDebugInformationHandle cdiHandle in reader.CustomDebugInformation)
        {
            CustomDebugInformation cdi = reader.GetCustomDebugInformation(cdiHandle);

            // Check if this is embedded source information
            Guid kind = reader.GetGuid(cdi.Kind);

            if (kind != s_embeddedSourceGuid)
            {
                continue;
            }

            // The parent should be a Document
            if (cdi.Parent.Kind != HandleKind.Document)
            {
                continue;
            }

            DocumentHandle docHandle = (DocumentHandle)cdi.Parent;

            if (documents.TryGetValue(docHandle, out System.Reflection.Metadata.Document document) == false)
            {
                continue;
            }

            // Get the document name
            string fileName = GetDocumentName(reader, document.Name);

            // Read the embedded source content
            SourceResult? sourceContent = ReadEmbeddedSourceContent(reader, cdi.Value);

            if (sourceContent != null)
            {
                results[fileName] = sourceContent;
            }
        }

        return results;
    }

    /// <summary>
    /// Reads the actual embedded source content from the blob
    /// </summary>
    private static SourceResult? ReadEmbeddedSourceContent(MetadataReader reader, BlobHandle blobHandle)
    {
        BlobReader blobReader = reader.GetBlobReader(blobHandle);

        // Read the format indicator (first 4 bytes)
        int format = blobReader.ReadInt32();

        // Get remaining bytes
        int remainingLength = blobReader.Length - blobReader.Offset;
        byte[] contentBytes = blobReader.ReadBytes(remainingLength);

        string sourceText;
        bool isCompressed = false;

        if (format == 0)
        {
            // Uncompressed UTF-8 text
            sourceText = Encoding.UTF8.GetString(contentBytes);
        }
        else if (format > 0)
        {
            // Compressed with deflate, format contains uncompressed size
            isCompressed = true;
            using MemoryStream compressed = new(contentBytes);
            using DeflateStream deflate = new(compressed, CompressionMode.Decompress);
            using MemoryStream decompressed = new();

            deflate.CopyTo(decompressed);
            sourceText = Encoding.UTF8.GetString(decompressed.ToArray());
        }
        else
        {
            // Reserved for future formats
            return null;
        }

        return new SourceResult
        {
            SourceCode = sourceText,
            IsEmbedded = true,
            IsCompressed = isCompressed
        };
    }

    /// <summary>
    /// Reconstructs the document name from the portable PDB format
    /// </summary>
    private static string GetDocumentName(MetadataReader reader, DocumentNameBlobHandle handle)
    {
        BlobReader blobReader = reader.GetBlobReader(handle);
        char separator = (char)blobReader.ReadByte();

        StringBuilder sb = new();
        bool first = true;

        while (blobReader.Offset < blobReader.Length)
        {
            BlobHandle partHandle = blobReader.ReadBlobHandle();

            if (partHandle.IsNil == false)
            {
                if (first == false)
                {
                    sb.Append(separator);
                }

                byte[] nameBytes = reader.GetBlobBytes(partHandle);
                sb.Append(Encoding.UTF8.GetString(nameBytes));
                first = false;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Helper method to get source for a specific symbol from Roslyn
    /// </summary>
    public static SourceResult? GetEmbeddedSourceForSymbol(ISymbol symbol)
    {
        // Get the assembly containing the symbol
        IAssemblySymbol? assembly = symbol.ContainingAssembly;

        if (assembly == null)
        {
            return null;
        }

        // Get the locations from the symbol
        ImmutableArray<Location> locations = symbol.Locations;

        foreach (Location location in locations)
        {
            if (location.IsInMetadata && location.MetadataModule != null)
            {
                string moduleName = location.MetadataModule.Name;

                // Try to find the defining document for this symbol
                string symbolFileName = moduleName;

                // For types, properties, methods, etc., use a more specific name
                if (symbol is INamedTypeSymbol namedType)
                {
                    symbolFileName = $"{namedType.Name}.cs";
                }
                else if (symbol.ContainingType != null)
                {
                    symbolFileName = $"{symbol.ContainingType.Name}.cs";
                }

                // Check if we can find embedded source for this symbol
                // The actual PDB path lookup will be handled by the calling code
                return new SourceResult
                {
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
