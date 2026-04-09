using System.Xml;
using System.Xml.Linq;
using ModelContextProtocol;

namespace SharpTools.Tools.Mcp.Tools;

// Marker class for ILogger<T> category specific to SolutionTools
public class SolutionToolsLogCategory { }

[McpServerToolType]
public static class SolutionTools
{
    private const int MaxOutputLength = 50000;

    private enum DetailLevel
    {
        Full,
        NoConstantFieldNames,
        NoCommonDerivedOrImplementedClasses,
        NoEventEnumNames,
        NoMethodParamTypes,
        NoPropertyTypes,
        NoMethodParamNames,
        FiftyPercentPropertyNames,
        NoPropertyNames,
        FiftyPercentMethodNames,
        NoMethodNames,
        NamespacesAndTypesOnly
    }

    private const string LoadSolutionDescriptionText =
        "The the `SharpTool` suite provides you with focused, high quality, and high information density dotnet analysis and editing tools. " +
        "When using `SharpTool`s, you focus on individual components, and navigate with type hierarchies and call graphs instead of raw code. " +
        "Because of this, you create more modular, coherent, composable, type-safe, and thus inherently correct code. " +
        $"`{ToolHelpers.SharpToolPrefix}{nameof(LoadSolution)}` is the entry point for the suite, and should be called once at the beginning of your session to initialize the other tools with data from the solution.";

    [McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(LoadSolution), Idempotent = true, Destructive = false, OpenWorld = false, ReadOnly = true)]
    [Description(LoadSolutionDescriptionText)]
    public static async Task<object> LoadSolution(
        ISolutionManager solutionManager,
        IEditorConfigProvider editorConfigProvider,
        ILogger<SolutionToolsLogCategory> logger,
        [Description("The absolute file path to the .sln or .slnx solution file.")] string solutionPath,
        CancellationToken cancellationToken)
    {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () =>
        {
            ErrorHandlingHelpers.ValidateStringParameter(solutionPath, nameof(solutionPath), logger);
            logger.LogInformation(
                "Executing '{LoadSolution}' tool for path: {SolutionPath}",
                nameof(LoadSolution),
                solutionPath);

            // Validate solution file exists and has correct extension
            if (File.Exists(solutionPath) == false)
            {
                logger.LogError("Solution file not found at path: {SolutionPath}", solutionPath);
                throw new McpException($"Solution file does not exist at path: {solutionPath}");
            }

            string ext = Path.GetExtension(solutionPath);
            if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) == false &&
                ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase) == false)
            {
                logger.LogError("File is not a valid solution file: {SolutionPath}", solutionPath);
                throw new McpException($"File at path '{solutionPath}' is not a .sln or .slnx file.");
            }

            try
            {
                await solutionManager.LoadSolutionAsync(solutionPath, cancellationToken);
            }
            catch (Exception ex) when ((ex is McpException || ex is OperationCanceledException) == false)
            {
                logger.LogError(ex, "Failed to load solution at {SolutionPath}", solutionPath);
                throw new McpException($"Failed to load solution: {ex.Message}");
            }

            // Get solution directory and initialize editor config
            string? solutionDir = Path.GetDirectoryName(solutionPath);
            if (string.IsNullOrEmpty(solutionDir))
            {
                logger.LogWarning(
                    ".editorconfig provider could not determine solution directory from path: {SolutionPath}",
                    solutionPath);
                throw new McpException($"Could not determine directory for solution path: {solutionPath}");
            }

            try
            {
                await editorConfigProvider.InitializeAsync(solutionDir, cancellationToken);
            }
            catch (Exception ex) when ((ex is McpException || ex is OperationCanceledException) == false)
            {
                // Log but don't fail - editor config is helpful but not critical
                logger.LogWarning(ex, "Failed to initialize .editorconfig from {SolutionDir}", solutionDir);
                // Continue execution, don't throw
            }

            int projectCount = solutionManager.GetProjects().Count();
            string successMessage = $"Solution '{Path.GetFileName(solutionPath)}' loaded successfully with {projectCount} project(s). Caches and .editorconfig initialized.";
            logger.LogInformation(successMessage);

            try
            {
                return await GetProjectStructure(solutionManager, logger, cancellationToken);
            }
            catch (Exception ex) when ((ex is McpException || ex is OperationCanceledException) == false)
            {
                logger.LogWarning(ex, "Successfully loaded solution but failed to retrieve project structure");
                // Return basic info instead of detailed structure
                return ToolHelpers.ToJson(new
                {
                    solutionName = Path.GetFileName(solutionPath),
                    projectCount,
                    status = "Solution loaded successfully, but project structure retrieval failed."
                });
            }
        }, logger, nameof(LoadSolution), cancellationToken);
    }

    private static async Task<object> GetProjectStructure(
        ISolutionManager solutionManager,
        ILogger<SolutionToolsLogCategory> logger,
        CancellationToken cancellationToken)
    {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () =>
        {
            ToolHelpers.EnsureSolutionLoaded(solutionManager);

            List<object> projectsData = [];

            try
            {
                foreach (Project project in solutionManager.GetProjects())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        Compilation? compilation = await solutionManager.GetCompilationAsync(project.Id, cancellationToken);
                        string targetFramework = "Unknown";

                        // Get the actual target framework from the project file
                        if (string.IsNullOrEmpty(project.FilePath) == false && File.Exists(project.FilePath))
                        {
                            targetFramework = ExtractTargetFrameworkFromProjectFile(project.FilePath);
                        }

                        // Get top level namespaces
                        HashSet<string> topLevelNamespaces = [];

                        try
                        {
                            foreach (Document document in project.Documents)
                            {
                                if (document.SourceCodeKind != SourceCodeKind.Regular || document.SupportsSyntaxTree == false)
                                {
                                    continue;
                                }

                                try
                                {
                                    SyntaxNode? syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
                                    if (syntaxRoot == null)
                                    {
                                        continue;
                                    }
                                    foreach (BaseNamespaceDeclarationSyntax nsNode in syntaxRoot.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
                                    {
                                        topLevelNamespaces.Add(nsNode.Name.ToString());
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger.LogWarning(ex, "Error getting namespaces from document {DocumentPath}", document.FilePath);
                                    // Continue with other documents
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Error getting namespaces for project {ProjectName}", project.Name);
                            // Continue with basic project info
                        }

                        // Get project references safely
                        List<string> projectRefs = [];
                        try
                        {
                            if (solutionManager.CurrentSolution != null)
                            {
                                projectRefs = project.ProjectReferences
                                    .Select(pr => solutionManager.CurrentSolution.GetProject(pr.ProjectId)?.Name)
                                    .Where(name => name != null)
                                    .OrderBy(name => name)
                                    .ToList()!;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Error getting project references for {ProjectName}", project.Name);
                            // Continue with empty project references
                        }

                        // Get NuGet package references from project file (with enhanced format detection)
                        List<string> packageRefs = [];
                        try
                        {
                            if (string.IsNullOrEmpty(project.FilePath) == false && File.Exists(project.FilePath))
                            {
                                // Get all packages
                                List<(string PackageId, string Version)> packages = Services.LegacyNuGetPackageReader.GetAllPackages(project.FilePath);
                                foreach ((string PackageId, string Version) package in packages)
                                {
                                    packageRefs.Add($"{package.PackageId} ({package.Version})");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Error getting NuGet package references for {ProjectName}", project.Name);
                            // Continue with empty package references
                        }

                        // Build namespace hierarchy as a nested tree representation
                        Dictionary<string, HashSet<string>> namespaceTree = [];

                        foreach (string ns in topLevelNamespaces)
                        {
                            string[] parts = ns.Split('.');
                            string current = "";

                            for (int i = 0; i < parts.Length; i++)
                            {
                                string part = parts[i];
                                string nextNamespace = string.IsNullOrEmpty(current) ? part : $"{current}.{part}";

                                if (namespaceTree.TryGetValue(current, out HashSet<string>? children) == false)
                                {
                                    children = [];
                                    namespaceTree[current] = children;
                                }

                                children.Add(part);
                                current = nextNamespace;
                            }
                        }

                        // Format the namespace tree as a string representation
                        StringBuilder namespaceTreeBuilder = new();
                        BuildNamespaceTreeString("", namespaceTree, namespaceTreeBuilder);
                        string namespaceStructure = namespaceTreeBuilder.ToString();

                        // Local function to recursively build the tree string
                        void BuildNamespaceTreeString(string current, Dictionary<string, HashSet<string>> tree, StringBuilder builder)
                        {
                            if (tree.TryGetValue(current, out HashSet<string>? children) == false || children.Count == 0)
                            {
                                return;
                            }

                            bool first = true;
                            foreach (string child in children.OrderBy(c => c))
                            {
                                if (first == false)
                                {
                                    builder.Append(',');
                                }
                                first = false;

                                builder.Append(child);

                                string nextNamespace = string.IsNullOrEmpty(current) ? child : $"{current}.{child}";
                                if (tree.ContainsKey(nextNamespace))
                                {
                                    builder.Append('{');
                                    BuildNamespaceTreeString(nextNamespace, tree, builder);
                                    builder.Append('}');
                                }
                            }
                        }

                        // Build the project data
                        projectsData.Add(new
                        {
                            name = project.Name + (project.AssemblyName.Equals(project.Name, StringComparison.OrdinalIgnoreCase) ? "" : $" ({project.AssemblyName})"),
                            version = project.Version.ToString(),
                            targetFramework,
                            namespaces = namespaceStructure,
                            documentCount = project.DocumentIds.Count,
                            projectReferences = projectRefs,
                            packageReferences = packageRefs
                        });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Error processing project {ProjectName}, adding basic info only", project.Name);
                        // Add minimal project info when there's an error
                        projectsData.Add(new
                        {
                            name = project.Name,
                            //filePath = project.FilePath,
                            language = project.Language,
                            error = $"Error processing project: {ex.Message}",
                            documentCount = project.DocumentIds.Count
                        });
                    }
                }

                // Create the result safely
                string? solutionName = null;
                try
                {
                    solutionName = Path.GetFileName(solutionManager.CurrentSolution?.FilePath ?? "unknown");
                }
                catch
                {
                    solutionName = "unknown";
                }

                var result = new
                {
                    solutionName,
                    projects = projectsData.OrderBy(p => ((dynamic)p).name).ToList(),
                    nextStep = $"Use `{ToolHelpers.SharpToolPrefix}{nameof(LoadProject)}` to get a detailed view of a specific project's structure."
                };

                logger.LogInformation(
                    "Project structure retrieved successfully for {ProjectCount} projects.",
                    projectsData.Count);
                return ToolHelpers.ToJson(result);
            }
            catch (Exception ex) when ((ex is McpException || ex is OperationCanceledException) == false)
            {
                logger.LogError(ex, "Error retrieving project structure");
                throw new McpException($"Failed to retrieve project structure: {ex.Message}");
            }
        }, logger, nameof(GetProjectStructure), cancellationToken);
    }

    public static string ExtractTargetFrameworkFromProjectFile(string projectFilePath)
    {
        try
        {
            if (string.IsNullOrEmpty(projectFilePath))
            {
                return "Unknown";
            }

            if (File.Exists(projectFilePath) == false)
            {
                return "Unknown";
            }

            XDocument xDoc = XDocument.Load(projectFilePath);

            // New-style .csproj (SDK-style)
            IEnumerable<XElement> propertyGroupElements = xDoc.Descendants("PropertyGroup");
            foreach (XElement propertyGroup in propertyGroupElements)
            {
                XElement? targetFrameworkElement = propertyGroup.Element("TargetFramework");
                if (targetFrameworkElement != null)
                {
                    string value = targetFrameworkElement.Value.Trim();
                    return string.IsNullOrEmpty(value) == false ? value : "Unknown";
                }

                XElement? targetFrameworksElement = propertyGroup.Element("TargetFrameworks");
                if (targetFrameworksElement != null)
                {
                    string value = targetFrameworksElement.Value.Trim();
                    return string.IsNullOrEmpty(value) == false ? value : "Unknown";
                }
            }

            // Old-style .csproj format
            XElement? targetFrameworkVersionElement = xDoc.Descendants("TargetFrameworkVersion").FirstOrDefault();
            if (targetFrameworkVersionElement != null)
            {
                string version = targetFrameworkVersionElement.Value.Trim();

                // Map from old-style version format (v4.x) to new-style (.NETFramework,Version=v4.x)
                if (string.IsNullOrEmpty(version) == false)
                {
                    if (version.StartsWith("v"))
                    {
                        return $"net{version.Substring(1).Replace(".", "")}";
                    }
                    return version;
                }
            }

            // Additional old-style property check
            string? targetFrameworkProfile = xDoc.Descendants("TargetFrameworkProfile").FirstOrDefault()?.Value?.Trim();
            string? targetFrameworkIdentifier = xDoc.Descendants("TargetFrameworkIdentifier").FirstOrDefault()?.Value?.Trim();

            if (string.IsNullOrEmpty(targetFrameworkIdentifier) == false)
            {
                // Parse the old-style framework identifier
                if (targetFrameworkIdentifier.Contains(".NETFramework"))
                {
                    string? version = xDoc.Descendants("TargetFrameworkVersion").FirstOrDefault()?.Value?.Trim();
                    if (string.IsNullOrEmpty(version) == false && version.StartsWith("v"))
                    {
                        return $"net{version.Substring(1).Replace(".", "")}";
                    }
                }
                else if (targetFrameworkIdentifier.Contains(".NETCore"))
                {
                    string? version = xDoc.Descendants("TargetFrameworkVersion").FirstOrDefault()?.Value?.Trim();
                    if (string.IsNullOrEmpty(version) == false && version.StartsWith("v"))
                    {
                        return $"netcoreapp{version.Substring(1).Replace(".", "")}";
                    }
                }
                else if (targetFrameworkIdentifier.Contains(".NETStandard"))
                {
                    string? version = xDoc.Descendants("TargetFrameworkVersion").FirstOrDefault()?.Value?.Trim();
                    if (string.IsNullOrEmpty(version) == false && version.StartsWith("v"))
                    {
                        return $"netstandard{version.Substring(1).Replace(".", "")}";
                    }
                }

                // Add profile if present
                if (string.IsNullOrEmpty(targetFrameworkProfile) == false)
                {
                    return $"{targetFrameworkIdentifier},{targetFrameworkProfile}";
                }

                return targetFrameworkIdentifier;
            }

            return "Unknown";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // File access issues
            return "Unknown (Access Error)";
        }
        catch (Exception ex) when (ex is XmlException)
        {
            // XML parsing issues
            return "Unknown (XML Error)";
        }
        catch (Exception)
        {
            // Any other exceptions
            return "Unknown";
        }
    }

    [McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(LoadProject), ReadOnly = true, OpenWorld = false, Destructive = false, Idempotent = false)]
    [Description($"Use this immediately after {nameof(LoadSolution)}. This injects a comprehensive understanding of the project structure into your context.")]
    public static async Task<object> LoadProject(
        ISolutionManager solutionManager,
        ILogger<SolutionToolsLogCategory> logger,
        ICodeAnalysisService codeAnalysisService,
        string projectName,
        CancellationToken cancellationToken)
    {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () =>
        {
            ErrorHandlingHelpers.ValidateStringParameter(projectName, nameof(projectName), logger);
            logger.LogInformation(
                "Executing '{LoadProjectToolName}' tool for project: {ProjectName}",
                nameof(LoadProject),
                projectName);

            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(LoadProject));
            int indexOfParen = projectName.IndexOf('(');
            string projectNameNormalized = indexOfParen == -1
                ? projectName.Trim()
                : projectName[..indexOfParen].Trim();

            Project? project = solutionManager.GetProjects().FirstOrDefault(
                p => p.Name == projectName
                || p.AssemblyName == projectName
                || p.Name == projectNameNormalized);
            if (project == null)
            {
                logger.LogError("Project '{ProjectName}' not found in the loaded solution", projectName);
                throw new McpException($"Project '{projectName}' not found in the solution.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            logger.LogDebug("Processing project: {ProjectName}", project.Name);

            // Get the compilation for the project
            Compilation? compilation;
            try
            {
                compilation = await solutionManager.GetCompilationAsync(project.Id, cancellationToken);
                if (compilation == null)
                {
                    logger.LogError("Failed to get compilation for project: {ProjectName}", project.Name);
                    throw new McpException($"Failed to get compilation for project: {project.Name}");
                }
            }
            catch (Exception ex) when ((ex is McpException || ex is OperationCanceledException) == false)
            {
                logger.LogError(ex, "Error getting compilation for project {ProjectName}", project.Name);
                throw new McpException($"Error getting compilation for project '{project.Name}': {ex.Message}");
            }

            // For display (excludes nested types)
            Dictionary<string, List<INamedTypeSymbol>> namespaceContents = [];
            Dictionary<string, List<INamedTypeSymbol>> typesByNamespace = [];

            // For analysis (includes all types including nested)
            Dictionary<string, List<INamedTypeSymbol>> allNamespaceContents = [];
            Dictionary<string, List<INamedTypeSymbol>> allTypesByNamespace = [];

            try
            {
                // First pass: Collect all source symbols and organize types by namespace
                foreach (Document document in project.Documents)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (document.SourceCodeKind != SourceCodeKind.Regular || document.SupportsSyntaxTree == false)
                    {
                        continue;
                    }

                    try
                    {
                        SyntaxTree? syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
                        if (syntaxTree == null)
                        {
                            continue;
                        }

                        SemanticModel? semanticModel = compilation.GetSemanticModel(syntaxTree);
                        if (semanticModel == null)
                        {
                            continue;
                        }

                        SyntaxNode root = await syntaxTree.GetRootAsync(cancellationToken);

                        // Get all type declarations
                        foreach (BaseTypeDeclarationSyntax typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            try
                            {
                                if (semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken) is INamedTypeSymbol symbol)
                                {
                                    string nsName = symbol.ContainingNamespace?.ToDisplayString() ?? "global";

                                    // Always add to the "all types" collection for analysis
                                    if (allTypesByNamespace.TryGetValue(nsName, out List<INamedTypeSymbol>? allTypeList) == false)
                                    {
                                        allTypeList = [];
                                        allTypesByNamespace[nsName] = allTypeList;
                                    }
                                    allTypeList.Add(symbol);

                                    // Only add non-nested types to the display collection
                                    if (symbol.ContainingType == null)
                                    {
                                        if (typesByNamespace.TryGetValue(nsName, out List<INamedTypeSymbol>? typeList) == false)
                                        {
                                            typeList = [];
                                            typesByNamespace[nsName] = typeList;
                                        }
                                        typeList.Add(symbol);
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                logger.LogWarning(ex,
                                    "Error processing type declaration in document {DocumentPath}",
                                    document.FilePath);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Error processing document {DocumentPath}", document.FilePath);
                    }
                }

                // Populate the display namespace contents
                foreach (KeyValuePair<string, List<INamedTypeSymbol>> nsEntry in typesByNamespace)
                {
                    if (namespaceContents.TryGetValue(nsEntry.Key, out List<INamedTypeSymbol>? globalTypeList) == false)
                    {
                        globalTypeList = [];
                        namespaceContents[nsEntry.Key] = globalTypeList;
                    }
                    globalTypeList.AddRange(nsEntry.Value);
                }

                // Populate the analysis namespace contents
                foreach (KeyValuePair<string, List<INamedTypeSymbol>> nsEntry in allTypesByNamespace)
                {
                    if (allNamespaceContents.TryGetValue(nsEntry.Key, out List<INamedTypeSymbol>? globalTypeList) == false)
                    {
                        globalTypeList = [];
                        allNamespaceContents[nsEntry.Key] = globalTypeList;
                    }
                    globalTypeList.AddRange(nsEntry.Value);
                }

                // Collect derived/implemented type information for the NoCommonDerivedOrImplementedClasses detail level
                // Use the all types collection for analysis to include nested types
                Dictionary<INamedTypeSymbol, Dictionary<string, List<INamedTypeSymbol>>> derivedTypesByNamespace =
                    await CollectDerivedAndImplementedCounts(allNamespaceContents, codeAnalysisService, logger, cancellationToken);
                CommonImplementationInfo commonImplementationInfo = new(derivedTypesByNamespace);

                logger.LogInformation(
                    "Found {ImplementationCount} types with derived classes or implementations. Mean count: {MeanCount:F2}, Common base types: {CommonBaseCount}",
                    commonImplementationInfo.TotalImplementationCounts.Count,
                    commonImplementationInfo.MedianImplementationCount,
                    commonImplementationInfo.CommonBaseTypes.Count);

                StringBuilder structureBuilder = new();
                DetailLevel currentDetailLevel = DetailLevel.Full;
                string output = "";
                bool lengthAcceptable = false;
                Random random = new Random();

                while (lengthAcceptable == false && currentDetailLevel <= DetailLevel.NamespacesAndTypesOnly)
                {
                    structureBuilder.Clear();
                    List<string> sortedNamespaces = [.. namespaceContents.Keys.OrderBy(ns => ns)];
                    Dictionary<string, Dictionary<string, List<INamedTypeSymbol>>> namespaceParts =
                        BuildNamespaceHierarchy(sortedNamespaces, namespaceContents, logger);
                    List<string> rootNamespaces = [.. namespaceParts.Keys.Where(ns => ns.IndexOf('.') == -1).OrderBy(n => n)];

                    foreach (string rootNs in rootNamespaces)
                    {
                        structureBuilder.Append(BuildNamespaceStructureText(
                            rootNs, namespaceParts, namespaceContents, logger, currentDetailLevel, random, commonImplementationInfo));
                    }

                    output = structureBuilder.ToString();

                    if (output.Length <= MaxOutputLength)
                    {
                        lengthAcceptable = true;
                    }
                    else
                    {
                        logger.LogInformation(
                            "Output string length ({Length}) exceeds limit ({Limit}). Reducing detail from {OldLevel} to {NewLevel}.",
                            output.Length, MaxOutputLength, currentDetailLevel, currentDetailLevel + 1);
                        currentDetailLevel++;
                        if (currentDetailLevel > DetailLevel.NamespacesAndTypesOnly)
                        {
                            logger.LogWarning(
                                "Even at the most compressed level, output length ({Length}) exceeds limit ({Limit}). Returning compressed output.",
                                output.Length, MaxOutputLength);
                        }
                    }
                }

                return $"<typeTree note=\"Use {ToolHelpers.SharpToolPrefix}{nameof(AnalysisTools.GetMembers)} for more detailed information about specific types.\">" +
                    output +
                    "\n</typeTree>";
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Operation was cancelled while analyzing project {ProjectName}", project.Name);
                throw;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                logger.LogError(ex, "Error analyzing project structure for {ProjectName}", project.Name);
                throw new McpException($"Error analyzing project structure: {ex.Message}");
            }
        }, logger, nameof(LoadProject), cancellationToken);
    }

    private static Dictionary<string, Dictionary<string, List<INamedTypeSymbol>>> BuildNamespaceHierarchy(
        List<string> sortedNamespaces,
        Dictionary<string, List<INamedTypeSymbol>> namespaceContents,
        ILogger<SolutionToolsLogCategory> logger)
    {
        // Process namespaces to build the hierarchy
        Dictionary<string, Dictionary<string, List<INamedTypeSymbol>>> namespaceParts = [];

        foreach (string fullNamespace in sortedNamespaces)
        {
            try
            {
                // Skip empty global namespace
                if (string.IsNullOrEmpty(fullNamespace) || fullNamespace == "global")
                {
                    continue;
                }

                // Split namespace into parts
                string[] parts = fullNamespace.Split('.');

                // Create entries for each namespace part
                string currentNs = "";

                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i];

                    if (string.IsNullOrEmpty(currentNs) == false)
                    {
                        currentNs += ".";
                    }
                    currentNs += part;

                    if (namespaceParts.TryGetValue(currentNs, out Dictionary<string, List<INamedTypeSymbol>>? children) == false)
                    {
                        children = [];
                        namespaceParts[currentNs] = children;
                    }

                    // If not the last part, add the next part as child namespace
                    if (i < parts.Length - 1)
                    {
                        string nextPart = parts[i + 1];
                        if (children.ContainsKey(nextPart) == false)
                        {
                            children[nextPart] = [];
                        }
                    }
                }

                // Add types to the leaf namespace
                if (namespaceContents.TryGetValue(fullNamespace, out List<INamedTypeSymbol>? types) && types.Any())
                {
                    Dictionary<string, List<INamedTypeSymbol>> leafNsParts = namespaceParts[fullNamespace];
                    foreach (INamedTypeSymbol type in types)
                    {
                        string typeName = type.Name;
                        if (leafNsParts.TryGetValue(typeName, out List<INamedTypeSymbol>? typeList) == false)
                        {
                            typeList = [];
                            leafNsParts[typeName] = typeList;
                        }
                        typeList.Add(type);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error processing namespace {Namespace} in hierarchy", fullNamespace);
            }
        }
        return namespaceParts;
    }

    private static string BuildNamespaceStructureText(
        string namespaceName,
        Dictionary<string, Dictionary<string, List<INamedTypeSymbol>>> namespaceParts,
        Dictionary<string, List<INamedTypeSymbol>> namespaceContents,
        ILogger<SolutionToolsLogCategory> logger,
        DetailLevel detailLevel,
        Random random,
        CommonImplementationInfo? commonImplementationInfo = null)
    {
        StringBuilder sb = new();
        try
        {
            string simpleName = namespaceName.Contains('.')
                ? namespaceName.Substring(namespaceName.LastIndexOf('.') + 1)
                : namespaceName;

            sb.Append('\n').Append(simpleName).Append('{');

            // If we're at NoCommonDerivedOrImplementedClasses level or above
            // show derived class counts for common base types in this namespace
            if (commonImplementationInfo != null &&
                detailLevel >= DetailLevel.NoCommonDerivedOrImplementedClasses)
            {
                // Build a dictionary of base types to their derived classes in this namespace
                Dictionary<INamedTypeSymbol, int> derivedCountsInNamespace = new(SymbolEqualityComparer.Default);

                foreach (INamedTypeSymbol baseType in commonImplementationInfo.CommonBaseTypes)
                {
                    if (commonImplementationInfo.DerivedTypesByNamespace.TryGetValue(baseType, out Dictionary<string, List<INamedTypeSymbol>>? derivedByNs) &&
                        derivedByNs.TryGetValue(namespaceName, out List<INamedTypeSymbol>? derivedTypes) &&
                        derivedTypes.Count > 0)
                    {
                        derivedCountsInNamespace[baseType] = derivedTypes.Count;
                    }
                }

                // If there are any derived classes from common base types in this namespace, show their counts
                if (derivedCountsInNamespace.Count > 0)
                {
                    foreach (KeyValuePair<INamedTypeSymbol, int> entry in derivedCountsInNamespace)
                    {
                        INamedTypeSymbol baseType = entry.Key;
                        int count = entry.Value;
                        string typeKindStr = baseType.TypeKind == TypeKind.Interface ? "implementation" : "derived class";
                        string baseTypeName = CommonImplementationInfo.GetTypeDisplayName(baseType);

                        sb.Append($"\n  {count} {typeKindStr}{(count == 1 ? "" : "es")} of {baseTypeName};");
                    }
                }
            }

            List<INamedTypeSymbol>? typesInNamespace = namespaceContents.GetValueOrDefault(namespaceName);
            StringBuilder typeContent = new();

            if (typesInNamespace != null)
            {
                foreach (INamedTypeSymbol type in typesInNamespace.OrderBy(t => t.Name))
                {
                    try
                    {
                        string typeStructure = BuildTypeStructure(type, logger, detailLevel, random, 1, commonImplementationInfo);
                        if (string.IsNullOrEmpty(typeStructure) == false) // Skip empty results (filtered derived types)
                        {
                            typeContent.Append(typeStructure);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "Error building structure for type {TypeName} in namespace {Namespace}",
                            type.Name, namespaceName);
                        typeContent.Append($"\n{new string(' ', 2 * 1)}{type.Name}{{/* Error: {ex.Message} */}}");
                    }
                }
            }

            StringBuilder childNamespaceContent = new();
            if (namespaceParts.TryGetValue(namespaceName, out Dictionary<string, List<INamedTypeSymbol>>? children))
            {
                foreach (KeyValuePair<string, List<INamedTypeSymbol>> child in children.OrderBy(c => c.Key))
                {
                    if (child.Value?.Count == 0) // This indicates a child namespace rather than a type within the current namespace
                    {
                        string childNamespace = namespaceName + "." + child.Key;
                        try
                        {
                            childNamespaceContent.Append(BuildNamespaceStructureText(
                                childNamespace, namespaceParts, namespaceContents, logger, detailLevel, random, commonImplementationInfo));
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Error building structure for child namespace {Namespace}", childNamespace);
                            childNamespaceContent.Append($"\n{child.Key}{{/* Error: {ex.Message} */}}");
                        }
                    }
                }
            }

            sb.Append(typeContent);
            sb.Append(childNamespaceContent);
            sb.Append("\n}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error building namespace structure text for {Namespace}", namespaceName);
            return $"\n{namespaceName}{{/* Error: {ex.Message} */}}";
        }
        return sb.ToString();
    }

    private static string BuildTypeStructure(
        INamedTypeSymbol type,
        ILogger<SolutionToolsLogCategory> logger,
        DetailLevel detailLevel,
        Random random,
        int indentLevel,
        CommonImplementationInfo? commonImplementationInfo = null)
    {
        StringBuilder sb = new();
        string indent = string.Empty; // new string(' ', 2 * indentLevel);
        try
        {
            // Skip derived classes that are part of a common base type at NoCommonDerivedOrImplementedClasses level or above
            if (commonImplementationInfo != null &&
                detailLevel >= DetailLevel.NoCommonDerivedOrImplementedClasses)
            {
                // Check if this type inherits from or implements a common base type
                bool shouldSkip = false;
                foreach (INamedTypeSymbol commonBaseType in commonImplementationInfo.CommonBaseTypes)
                {
                    // Check if this type directly inherits from a common base type
                    if (SymbolEqualityComparer.Default.Equals(type.BaseType, commonBaseType))
                    {
                        shouldSkip = true;
                        break;
                    }

                    // Check if this type implements a common interface
                    foreach (INamedTypeSymbol iface in type.AllInterfaces)
                    {
                        if (SymbolEqualityComparer.Default.Equals(iface, commonBaseType))
                        {
                            shouldSkip = true;
                            break;
                        }
                    }

                    if (shouldSkip)
                    {
                        break;
                    }
                }

                if (shouldSkip)
                {
                    return string.Empty; // Skip this type
                }
            }

            sb.Append('\n').Append(indent).Append(type.Name);

            if (type.TypeParameters.Length > 0 && detailLevel < DetailLevel.NamespacesAndTypesOnly)
            {
                sb.Append('<').Append(type.TypeParameters.Length).Append('>');
            }
            sb.Append("{");

            if (detailLevel == DetailLevel.NamespacesAndTypesOnly)
            {
                foreach (INamedTypeSymbol nestedType in type.GetTypeMembers().OrderBy(t => t.Name))
                {
                    try
                    {
                        sb.Append(BuildTypeStructure(nestedType, logger, detailLevel, random, indentLevel + 1, commonImplementationInfo));
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "Error building structure for nested type {TypeName} in {ParentType}",
                            nestedType.Name, type.Name);
                        sb.Append($"\n{new string(' ', 2 * (indentLevel + 1))}{nestedType.Name}{{/* Error: {ex.Message} */}}");
                    }
                }
                sb.Append('\n').Append(indent).Append("}");
                return sb.ToString();
            }

            // Regular member info for non-common base types
            bool membersContent = AppendMemberInfo(sb, type, logger, detailLevel, random, indent);

            // Nested Types
            foreach (INamedTypeSymbol nestedType in type.GetTypeMembers().OrderBy(t => t.Name))
            {
                try
                {
                    sb.Append(BuildTypeStructure(nestedType, logger, detailLevel, random, indentLevel + 1, commonImplementationInfo));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Error building structure for nested type {TypeName} in {ParentType}",
                        nestedType.Name, type.Name);
                    sb.Append($"\n{new string(' ', 2 * (indentLevel + 1))}{nestedType.Name}{{/* Error: {ex.Message} */}}");
                }
            }

            if (membersContent || type.GetTypeMembers().Any())
            {
                sb.Append('\n').Append(indent).Append("}");
            }
            else
            {
                sb.Append("}"); // No newline if type is empty and no members shown
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error building structure for type {TypeName}", type.Name);
            return $"\n{indent}{type.Name}{{/* Error: {ex.Message} */}}";
        }
        return sb.ToString();
    }

    private static string GetTypeShortName(ITypeSymbol type)
    {
        try
        {
            if (type == null)
            {
                return "?";
            }

            if (type.SpecialType != SpecialType.None)
            {
                return type.SpecialType switch
                {
                    SpecialType.System_Boolean => "bool",
                    SpecialType.System_Byte => "byte",
                    SpecialType.System_SByte => "sbyte",
                    SpecialType.System_Char => "char",
                    SpecialType.System_Int16 => "short",
                    SpecialType.System_UInt16 => "ushort",
                    SpecialType.System_Int32 => "int",
                    SpecialType.System_UInt32 => "uint",
                    SpecialType.System_Int64 => "long",
                    SpecialType.System_UInt64 => "ulong",
                    SpecialType.System_Single => "float",
                    SpecialType.System_Double => "double",
                    SpecialType.System_Decimal => "decimal",
                    SpecialType.System_String => "string",
                    SpecialType.System_Object => "object",
                    SpecialType.System_Void => "void",
                    _ => type.Name
                };
            }

            if (type is IArrayTypeSymbol arrayType)
            {
                return $"{GetTypeShortName(arrayType.ElementType)}[]";
            }

            if (type is INamedTypeSymbol namedType)
            {
                if (namedType.IsTupleType && namedType.TupleElements.Any())
                {
                    return $"({string.Join(", ", namedType.TupleElements.Select(te => $"{GetTypeShortName(te.Type)} {te.Name}"))})";
                }
                if (namedType.TypeArguments.Length > 0)
                {
                    string typeArgs = string.Join(", ", namedType.TypeArguments.Select(GetTypeShortName));
                    string baseName = namedType.Name;
                    // Handle common nullable syntax
                    if (namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                    {
                        return $"{GetTypeShortName(namedType.TypeArguments[0])}?";
                    }
                    return $"{baseName}<{typeArgs}>";
                }
            }

            return type.Name;
        }
        catch (Exception)
        {
            return type?.Name ?? "?";
        }
    }

    private static async Task<Dictionary<INamedTypeSymbol, Dictionary<string, List<INamedTypeSymbol>>>> CollectDerivedAndImplementedCounts(
        Dictionary<string, List<INamedTypeSymbol>> namespaceContents,
        ICodeAnalysisService codeAnalysisService,
        ILogger<SolutionToolsLogCategory> logger,
        CancellationToken cancellationToken)
    {
        // Dictionary of base types to their derived types, organized by namespace
        Dictionary<INamedTypeSymbol, Dictionary<string, List<INamedTypeSymbol>>> baseTypeImplementations =
            new(SymbolEqualityComparer.Default);
        HashSet<INamedTypeSymbol> processedSymbols = new(SymbolEqualityComparer.Default);

        try
        {
            // Process each namespace and its types
            foreach (List<INamedTypeSymbol> typesList in namespaceContents.Values)
            {
                foreach (INamedTypeSymbol typeSymbol in typesList)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Skip if we've already processed this type
                    if (processedSymbols.Contains(typeSymbol))
                    {
                        continue;
                    }

                    processedSymbols.Add(typeSymbol);

                    // Skip types that can't have derived classes (static, sealed, etc.) or implementations (non-interfaces)
                    if ((typeSymbol.IsStatic || typeSymbol.IsSealed) && typeSymbol.TypeKind != TypeKind.Interface)
                    {
                        continue;
                    }

                    try
                    {
                        List<INamedTypeSymbol> derivedTypes = [];

                        // Find classes derived from this type
                        if (typeSymbol.TypeKind == TypeKind.Class)
                        {
                            derivedTypes.AddRange(await codeAnalysisService.FindDerivedClassesAsync(typeSymbol, cancellationToken));
                        }

                        // Find implementations of this interface
                        if (typeSymbol.TypeKind == TypeKind.Interface)
                        {
                            IEnumerable<ISymbol> implementations = await codeAnalysisService.FindImplementationsAsync(typeSymbol, cancellationToken);
                            foreach (ISymbol impl in implementations)
                            {
                                if (impl is INamedTypeSymbol namedTypeImpl)
                                {
                                    derivedTypes.Add(namedTypeImpl);
                                }
                            }
                        }

                        // Skip if there are no derived types or implementations
                        if (derivedTypes.Count == 0)
                        {
                            continue;
                        }

                        // Group derived types by namespace
                        Dictionary<string, List<INamedTypeSymbol>> byNamespace = [];
                        foreach (INamedTypeSymbol derivedType in derivedTypes)
                        {
                            string namespaceName = derivedType.ContainingNamespace?.ToDisplayString() ?? "global";
                            if (byNamespace.TryGetValue(namespaceName, out List<INamedTypeSymbol>? nsTypes) == false)
                            {
                                nsTypes = [];
                                byNamespace[namespaceName] = nsTypes;
                            }
                            nsTypes.Add(derivedType);
                        }

                        // Store the grouped derived types
                        baseTypeImplementations[typeSymbol] = byNamespace;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Error analyzing derived/implemented types for {TypeName}", typeSymbol.Name);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error collecting derived/implemented type counts");
        }

        return baseTypeImplementations;
    }

    private class CommonImplementationInfo
    {
        // Maps base types to their derived/implemented types grouped by namespace
        public Dictionary<INamedTypeSymbol, Dictionary<string, List<INamedTypeSymbol>>> DerivedTypesByNamespace { get; }

        // Maps base types to their total derived/implemented type count
        public Dictionary<INamedTypeSymbol, int> TotalImplementationCounts { get; }

        // The mean number of derived/implemented types across all base types
        public double MedianImplementationCount { get; }

        // Base types with above-average number of derived/implemented types
        public HashSet<INamedTypeSymbol> CommonBaseTypes { get; }

        public CommonImplementationInfo(Dictionary<INamedTypeSymbol, Dictionary<string, List<INamedTypeSymbol>>> derivedTypesByNamespace)
        {
            DerivedTypesByNamespace = derivedTypesByNamespace;

            // Calculate total counts for each base type
            TotalImplementationCounts = new Dictionary<INamedTypeSymbol, int>(SymbolEqualityComparer.Default);
            foreach (INamedTypeSymbol baseType in derivedTypesByNamespace.Keys)
            {
                int totalCount = 0;
                foreach (List<INamedTypeSymbol> nsTypes in derivedTypesByNamespace[baseType].Values)
                {
                    totalCount += nsTypes.Count;
                }
                TotalImplementationCounts[baseType] = totalCount;
            }

            // Calculate the mean implementation count
            if (TotalImplementationCounts.Count > 0)
            {
                List<int> counts = [.. TotalImplementationCounts.Values.OrderBy(c => c)];
                MedianImplementationCount = counts.Count % 2 == 0
                    ? (counts[counts.Count / 2 - 1] + counts[counts.Count / 2]) / 2.0
                    : counts[counts.Count / 2];

                // Identify base types with above-average number of implementations
                CommonBaseTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                foreach (KeyValuePair<INamedTypeSymbol, int> pair in TotalImplementationCounts)
                {
                    if (pair.Value > MedianImplementationCount)
                    {
                        CommonBaseTypes.Add(pair.Key);
                    }
                }
            }
            else
            {
                MedianImplementationCount = 0;
                CommonBaseTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            }
        }

        // Get the full qualified display name of a type for display purposes
        public static string GetTypeDisplayName(INamedTypeSymbol type)
        {
            return FuzzyFqnLookupService.GetSearchableString(type);
        }
    }

    // Helper method to append member information and return whether any members were added
    private static bool AppendMemberInfo(
        StringBuilder sb,
        INamedTypeSymbol type,
        ILogger<SolutionToolsLogCategory> logger,
        DetailLevel detailLevel,
        Random random,
        string indent)
    {
        StringBuilder membersContent = new();
        List<ISymbol> publicOrInternalMembers = [.. type.GetMembers()
            .Where(m => m.IsImplicitlyDeclared == false &&
                (m is INamedTypeSymbol) == false &&
                (m.DeclaredAccessibility == Accessibility.Public ||
                m.DeclaredAccessibility == Accessibility.Internal ||
                m.DeclaredAccessibility == Accessibility.ProtectedOrInternal))];

        List<IFieldSymbol> fields = [.. publicOrInternalMembers.OfType<IFieldSymbol>().Where(f => f.IsImplicitlyDeclared == false && f.Name.Contains("k__BackingField") == false && f.IsConst == false && type.TypeKind != TypeKind.Enum)];
        List<IFieldSymbol> constants = [.. publicOrInternalMembers.OfType<IFieldSymbol>().Where(f => f.IsConst)];
        List<IFieldSymbol> enumValues = type.TypeKind == TypeKind.Enum
            ? [.. publicOrInternalMembers.OfType<IFieldSymbol>()]
            : [];
        List<IEventSymbol> events = [.. publicOrInternalMembers.OfType<IEventSymbol>()];
        List<IPropertySymbol> properties = [.. publicOrInternalMembers.OfType<IPropertySymbol>()];
        List<IMethodSymbol> methods = [.. publicOrInternalMembers.OfType<IMethodSymbol>()
            .Where(m => m.MethodKind != MethodKind.PropertyGet &&
                m.MethodKind != MethodKind.PropertySet &&
                m.MethodKind != MethodKind.EventAdd &&
                m.MethodKind != MethodKind.EventRemove &&
                m.Name.StartsWith("<") == false)];

        // Fields
        if (fields.Any())
        {
            if (detailLevel <= DetailLevel.NoConstantFieldNames)
            {
                foreach (IFieldSymbol field in fields.OrderBy(f => f.Name))
                {
                    membersContent.Append($"\n{indent}  {field.Name}:{GetTypeShortName(field.Type)};");
                }
            }
            else
            {
                membersContent.Append($"\n{indent}  {fields.Count} field{(fields.Count == 1 ? "" : "s")};");
            }
        }

        // Constants
        if (constants.Any())
        {
            if (detailLevel < DetailLevel.NoConstantFieldNames) // Show names if detail is Full
            {
                foreach (IFieldSymbol cnst in constants.OrderBy(c => c.Name))
                {
                    membersContent.Append($"\n{indent}  const {cnst.Name}:{GetTypeShortName(cnst.Type)};");
                }
            }
            else
            {
                membersContent.Append($"\n{indent}  {constants.Count} constant{(constants.Count == 1 ? "" : "s")};");
            }
        }

        // Enum Members
        if (enumValues.Any())
        {
            if (detailLevel < DetailLevel.NoEventEnumNames)
            {
                foreach (IFieldSymbol enumVal in enumValues.OrderBy(e => e.Name))
                {
                    membersContent.Append($"\n{indent}  {enumVal.Name};");
                }
            }
            else
            {
                membersContent.Append($"\n{indent}  {enumValues.Count} enum value{(enumValues.Count == 1 ? "" : "s")};");
            }
        }

        // Events
        if (events.Any())
        {
            if (detailLevel < DetailLevel.NoEventEnumNames)
            {
                foreach (IEventSymbol evt in events.OrderBy(e => e.Name))
                {
                    membersContent.Append($"\n{indent}  event {evt.Name}:{GetTypeShortName(evt.Type)};");
                }
            }
            else
            {
                membersContent.Append($"\n{indent}  {events.Count} event{(events.Count == 1 ? "" : "s")};");
            }
        }

        // Properties
        if (properties.Any())
        {
            if (detailLevel < DetailLevel.NoPropertyTypes) // Full, NoConstantFieldNames, NoEventEnumNames, NoMethodParamTypes
            {
                foreach (IPropertySymbol prop in properties.OrderBy(p => p.Name))
                {
                    membersContent.Append($"\n{indent}  {prop.Name}:{GetTypeShortName(prop.Type)};");
                }
            }
            else if (detailLevel == DetailLevel.NoPropertyTypes || detailLevel == DetailLevel.NoMethodParamNames) // Retain property names without types
            {
                foreach (IPropertySymbol prop in properties.OrderBy(p => p.Name))
                {
                    membersContent.Append($"\n{indent}  {prop.Name};");
                }
            }
            else if (detailLevel == DetailLevel.FiftyPercentPropertyNames)
            {
                List<IPropertySymbol> shuffledProps = [.. properties.OrderBy(_ => random.Next())];
                List<IPropertySymbol> propsToShow = [.. shuffledProps.Take(Math.Max(1, properties.Count / 2))];
                foreach (IPropertySymbol prop in propsToShow.OrderBy(p => p.Name))
                {
                    membersContent.Append($"\n{indent}  {prop.Name};"); // Type omitted
                }
                if (propsToShow.Count < properties.Count)
                {
                    membersContent.Append($"\n{indent}  and {properties.Count - propsToShow.Count} more propert{(properties.Count - propsToShow.Count == 1 ? "y" : "ies")};");
                }
            }
            else if (detailLevel == DetailLevel.NoPropertyNames || detailLevel == DetailLevel.FiftyPercentMethodNames) // Only count for NoPropertyNames or if method names are also being reduced
            {
                membersContent.Append($"\n{indent}  {properties.Count} propert{(properties.Count == 1 ? "y" : "ies")};");
            }
            else if (detailLevel < DetailLevel.NamespacesAndTypesOnly) // Default for levels more compressed than NoPropertyNames but not NamespacesAndTypesOnly (e.g. NoMethodNames)
            {
                membersContent.Append($"\n{indent}  {properties.Count} propert{(properties.Count == 1 ? "y" : "ies")};");
            }
            // If detailLevel is NamespacesAndTypesOnly, properties are skipped entirely by the initial check.
        }

        // Methods (including constructors)
        if (methods.Any())
        {
            if (detailLevel <= DetailLevel.FiftyPercentMethodNames)
            {
                List<IMethodSymbol> methodsToShow = methods;
                if (detailLevel == DetailLevel.FiftyPercentMethodNames)
                {
                    List<IMethodSymbol> shuffledMethods = [.. methods.OrderBy(_ => random.Next())];
                    methodsToShow = [.. shuffledMethods.Take(Math.Max(1, methods.Count / 2))];
                }
                foreach (IMethodSymbol method in methodsToShow.OrderBy(m => m.Name))
                {
                    membersContent.Append($"\n{indent}  {method.Name}");
                    if (detailLevel < DetailLevel.NoMethodParamNames)
                    {
                        membersContent.Append("(");
                        if (method.Parameters.Length > 0)
                        {
                            IEnumerable<string> paramStrings = method.Parameters.Select(p =>
                                detailLevel < DetailLevel.NoMethodParamTypes ? $"{p.Name}:{GetTypeShortName(p.Type)}" : p.Name
                            );
                            membersContent.Append(string.Join(", ", paramStrings));
                        }
                        membersContent.Append(")");
                    }
                    else if (method.Parameters.Length > 0)
                    {
                        membersContent.Append($"({method.Parameters.Length} param{(method.Parameters.Length == 1 ? "" : "s")})");
                    }
                    else
                    {
                        membersContent.Append("()");
                    }
                    if (method.MethodKind != MethodKind.Constructor && method.ReturnsVoid == false)
                    {
                        membersContent.Append($":{GetTypeShortName(method.ReturnType)}");
                    }
                    membersContent.Append(";");
                }
                if (detailLevel == DetailLevel.FiftyPercentMethodNames && methodsToShow.Count < methods.Count)
                {
                    membersContent.Append($"\n{indent}  and {methods.Count - methodsToShow.Count} more method{(methods.Count - methodsToShow.Count == 1 ? "" : "s")};");
                }
            }
            else // NoMethodNames or higher compression
            {
                membersContent.Append($"\n{indent}  {methods.Count} method{(methods.Count == 1 ? "" : "s")};");
            }
        }

        // Append the members content to the main StringBuilder
        if (membersContent.Length > 0)
        {
            sb.Append(membersContent);
            return true;
        }

        return false;
    }
}
