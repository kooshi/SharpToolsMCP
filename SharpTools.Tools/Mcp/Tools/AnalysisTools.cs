using ModelContextProtocol;

namespace SharpTools.Tools.Mcp.Tools;

// Marker class for ILogger<T> category specific to AnalysisTools
public class AnalysisToolsLogCategory
{
}

[McpServerToolType]
public static partial class AnalysisTools
{
    //Disabled for now, seems unnecessary
    //[McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(GetAllSubtypes), Idempotent = true, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Recursively lists all nested types, methods, properties, fields, and enums within a given parent type. Ideal for gaining a complete mental model of a type hierarchy at a glance.")]
    public static async Task<object> GetAllSubtypes(
        ISolutionManager solutionManager,
        ICodeAnalysisService codeAnalysisService,
        ILogger<AnalysisToolsLogCategory> logger,
        [Description("The fully qualified name of the parent type (e.g., MyNamespace.MyClass).")] string fullyQualifiedParentTypeName,
        CancellationToken cancellationToken)
    {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () =>
        {
            ErrorHandlingHelpers.ValidateStringParameter(fullyQualifiedParentTypeName, nameof(fullyQualifiedParentTypeName), logger);
            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(GetAllSubtypes));

            logger.LogInformation("Executing {GetAllSubtypes} for: {TypeName}", nameof(GetAllSubtypes), fullyQualifiedParentTypeName);

            try
            {
                INamedTypeSymbol roslynSymbol = await ToolHelpers.GetRoslynNamedTypeSymbolOrThrowAsync(solutionManager, fullyQualifiedParentTypeName, cancellationToken);
                return ToolHelpers.ToJson(await BuildRoslynSubtypeTreeAsync(roslynSymbol, codeAnalysisService, cancellationToken));
            }
            catch (McpException ex)
            {
                logger.LogDebug(ex, "Roslyn symbol not found for {TypeName}, trying reflection.", fullyQualifiedParentTypeName);
                // Fall through to reflection if Roslyn symbol not found or other McpException related to Roslyn.
            }

            // If Roslyn symbol wasn't found or an McpException occurred, try reflection.
            // GetReflectionTypeOrThrowAsync will throw if the type is not found in reflection either.
            Type reflectionType = await ToolHelpers.GetReflectionTypeOrThrowAsync(solutionManager, fullyQualifiedParentTypeName, cancellationToken);
            return ToolHelpers.ToJson(BuildReflectionSubtypeTree(reflectionType, cancellationToken));

        }, logger, nameof(GetAllSubtypes), cancellationToken);
    }

    internal static async Task<object> BuildRoslynSubtypeTreeAsync(INamedTypeSymbol typeSymbol, ICodeAnalysisService codeAnalysisService, CancellationToken cancellationToken)
    {
        Dictionary<string, List<object>> membersByKind = [];

        foreach (ISymbol member in typeSymbol.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (member.IsImplicitlyDeclared || member.Kind == SymbolKind.ErrorType || ToolHelpers.IsPropertyAccessor(member))
            {
                continue;
            }

            List<LocationInfo> locationInfo = GetDeclarationLocationInfo(member);
            LocationInfo? memberLocation = locationInfo.FirstOrDefault();
            string kind = ToolHelpers.GetSymbolKindString(member);

            // Create an entry for this kind if it doesn't exist
            if (membersByKind.ContainsKey(kind) == false)
            {
                membersByKind[kind] = [];
            }

            var memberInfo = new
            {
                signature = ToolHelpers.GetRoslynSymbolModifiersString(member) + " " + CodeAnalysisService.GetFormattedSignatureAsync(member, false),
                fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(member),
                line = memberLocation?.StartLine,
                members = member is INamedTypeSymbol nestedType ?
                    await BuildRoslynSubtypeTreeAsync(nestedType, codeAnalysisService, cancellationToken) : null
            };

            membersByKind[kind].Add(memberInfo);
        }

        // Sort members within each kind by signature
        foreach (string kind in membersByKind.Keys.ToList())
        {
            membersByKind[kind] = [.. membersByKind[kind]
                .OrderBy(m => ((dynamic)m).line)
                .OrderBy(m => ((dynamic)m).signature)];
        }

        // Sort kinds in a logical order: Nested Types, Fields, Properties, Events, Methods
        Dictionary<string, List<object>> orderedKinds = membersByKind.Keys
            .OrderBy(k => k switch
            {
                "Class" => 1,
                "Interface" => 1,
                "Struct" => 1,
                "Enum" => 1,
                "Field" => 2,
                "Property" => 3,
                "Event" => 4,
                "Method" => 5,
                _ => 99
            })
            .ThenBy(k => k)
            .ToDictionary(k => k, k => membersByKind[k]);

        List<LocationInfo> locations = GetDeclarationLocationInfo(typeSymbol);
        // For partial classes, we may have multiple locations
        object? location = locations.Count > 1 ? locations : locations.FirstOrDefault();
        return new
        {
            kind = ToolHelpers.GetSymbolKindString(typeSymbol),
            signature = ToolHelpers.GetRoslynTypeSpecificModifiersString(typeSymbol) + " " +
                typeSymbol.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal),
            fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(typeSymbol),
            location,
            membersByKind = orderedKinds
        };
    }

    private static object BuildReflectionSubtypeTree(Type type, CancellationToken cancellationToken)
    {
        var members = new List<object>();

        foreach (MemberInfo memberInfo in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.DeclaredOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip property/event accessors to reduce noise
            if (memberInfo is MethodInfo mi && mi.IsSpecialName &&
                (mi.Name.StartsWith("get_") || mi.Name.StartsWith("set_") ||
                mi.Name.StartsWith("add_") || mi.Name.StartsWith("remove_")))
            {
                continue;
            }

            var memberItem = new
            {
                kind = ToolHelpers.GetReflectionMemberTypeKindString(memberInfo),
                signature = ToolHelpers.GetReflectionMemberModifiersString(memberInfo) + " " + memberInfo.ToString(),
                members = memberInfo is Type nestedType ? BuildReflectionSubtypeTree(nestedType, cancellationToken) : null
            };

            members.Add(memberItem);
        }

        members = [.. members.OrderBy(m => ((dynamic)m).kind)
            .ThenBy(m => ((dynamic)m).signature)
            .GroupBy(m => ((dynamic)m).kind)
            .Select(g => (object)new
            {
                kind = g.Key,
                members = g.Select(m => new
                {
                    ((dynamic)m).signature,
                    ((dynamic)m).members
                }).ToList()
            })];

        return new
        {
            kind = ToolHelpers.GetReflectionTypeKindString(type),
            signature = ToolHelpers.GetReflectionTypeModifiersString(type) + " " + type.FullName,
            members
        };
    }

    [McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(GetMembers), Idempotent = true, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Lists the full signatures of members of a specified type, including XML documentation. Essential for rapidly understanding a type's API, but does not give you the implementations. Use this like Intellisense when you're writing code which depends on the target class.")]
    public static async Task<object> GetMembers(
        ISolutionManager solutionManager,
        ICodeAnalysisService codeAnalysisService,
        ILogger<AnalysisToolsLogCategory> logger,
        [Description("The fully qualified name of the type.")] string fullyQualifiedTypeName,
        [Description("If true, includes private members; otherwise, only public/internal/protected members.")] bool includePrivateMembers,
        CancellationToken cancellationToken = default)
    {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () =>
        {
            ErrorHandlingHelpers.ValidateStringParameter(fullyQualifiedTypeName, nameof(fullyQualifiedTypeName), logger);
            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(GetMembers));
            logger.LogInformation(
                "Executing '{GetMembers}' for: {TypeName} (IncludePrivate: {IncludePrivate})",
                nameof(GetMembers), fullyQualifiedTypeName, includePrivateMembers);

            try
            {
                INamedTypeSymbol roslynSymbol = await ToolHelpers.GetRoslynNamedTypeSymbolOrThrowAsync(solutionManager, fullyQualifiedTypeName, cancellationToken);
                string typeName = ToolHelpers.RemoveGlobalPrefix(roslynSymbol.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal));
                Dictionary<string, Dictionary<string, List<string>>> membersByLocation = [];
                string defaultLocation = "Unknown Location";

                foreach (ISymbol member in roslynSymbol.GetMembers())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (member.IsImplicitlyDeclared || ToolHelpers.IsPropertyAccessor(member))
                    {
                        continue;
                    }

                    bool shouldInclude = includePrivateMembers ||
                        member.DeclaredAccessibility == Accessibility.Public ||
                        member.DeclaredAccessibility == Accessibility.Internal ||
                        member.DeclaredAccessibility == Accessibility.Protected ||
                        member.DeclaredAccessibility == Accessibility.ProtectedAndInternal ||
                        member.DeclaredAccessibility == Accessibility.ProtectedOrInternal;

                    if (shouldInclude)
                    {
                        try
                        {
                            List<LocationInfo> locationInfo = GetDeclarationLocationInfo(member);
                            LocationInfo? location = locationInfo.FirstOrDefault();
                            string locationKey = location != null
                                ? location.FilePath
                                : defaultLocation;

                            string kind = ToolHelpers.GetSymbolKindString(member);
                            string xmlDocs = await codeAnalysisService.GetXmlDocumentationAsync(member, cancellationToken) ?? string.Empty;
                            string signature = ToolHelpers.GetRoslynSymbolModifiersString(member)
                                + " " + (CodeAnalysisService.GetFormattedSignatureAsync(member, false)).Replace(typeName + ".", string.Empty).Trim()
                                + "//FQN: " + FuzzyFqnLookupService.GetSearchableString(member);
                            if (string.IsNullOrEmpty(xmlDocs) == false)
                            {
                                signature = xmlDocs + "\n" + signature;
                            }
                            string memberInfo = signature;

                            if (membersByLocation.ContainsKey(locationKey) == false)
                            {
                                membersByLocation[locationKey] = [];
                            }
                            Dictionary<string, List<string>> membersByKind = membersByLocation[locationKey];

                            if (membersByKind.ContainsKey(kind) == false)
                            {
                                membersByKind[kind] = [];
                            }
                            membersByKind[kind].Add(memberInfo);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Error retrieving details for member {MemberName} in type {TypeName}",
                                member.Name, fullyQualifiedTypeName);

                            string memberInfo = ToolHelpers.GetRoslynSymbolModifiersString(member) + " " + member.ToDisplayString();

                            string kind = ToolHelpers.GetSymbolKindString(member);
                            if (membersByLocation.ContainsKey(defaultLocation) == false)
                            {
                                membersByLocation[defaultLocation] = [];
                            }
                            Dictionary<string, List<string>> membersByKind = membersByLocation[defaultLocation];

                            if (membersByKind.ContainsKey(kind) == false)
                            {
                                membersByKind[kind] = [];
                            }
                            membersByKind[kind].Add(memberInfo);
                        }
                    }
                }

                List<LocationInfo> typeLocations = GetDeclarationLocationInfo(roslynSymbol);

                return ToolHelpers.ToJson(new
                {
                    typeName,
                    note = $"Use {ToolHelpers.SharpToolPrefix}{nameof(ViewDefinition)} to view the full source code of the types or members.",
                    locations = typeLocations,
                    includesPrivateMembers = includePrivateMembers,
                    membersByLocation
                });
            }
            catch (McpException ex)
            {
                logger.LogDebug(ex, "Roslyn symbol not found for {TypeName} or error occurred, trying reflection.", fullyQualifiedTypeName);
                // Fall through to reflection if Roslyn symbol not found or other McpException related to Roslyn.
            }

            Type reflectionType = await ToolHelpers.GetReflectionTypeOrThrowAsync(solutionManager, fullyQualifiedTypeName, cancellationToken);
            return GetReflectionTypeMembersAsync(reflectionType, includePrivateMembers, logger, cancellationToken);

        }, logger, nameof(GetMembers), cancellationToken);
    }

    private static string GetReflectionTypeMembersAsync(
        Type reflectionType,
        bool includePrivateMembers,
        ILogger<AnalysisToolsLogCategory> logger,
        CancellationToken cancellationToken)
    {
        List<object> apiMembers = [];
        try
        {
            foreach (MemberInfo memberInfo in reflectionType.GetMembers(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.DeclaredOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip property and event accessors which are exposed as separate methods
                if (memberInfo is MethodInfo mi && mi.IsSpecialName &&
                    (mi.Name.StartsWith("get_") || mi.Name.StartsWith("set_") ||
                    mi.Name.StartsWith("add_") || mi.Name.StartsWith("remove_")))
                {
                    continue;
                }

                // Determine if the member should be included based on its accessibility
                bool shouldInclude;
                string accessibilityString = "";

                try
                {
                    switch (memberInfo)
                    {
                        case FieldInfo fi:
                            shouldInclude = includePrivateMembers || fi.IsPublic || fi.IsAssembly || fi.IsFamily || fi.IsFamilyOrAssembly;
                            accessibilityString = ToolHelpers.GetReflectionMemberModifiersString(fi);
                            break;
                        case MethodBase mb:
                            shouldInclude = includePrivateMembers || mb.IsPublic || mb.IsAssembly || mb.IsFamily || mb.IsFamilyOrAssembly;
                            accessibilityString = ToolHelpers.GetReflectionMemberModifiersString(mb);
                            break;
                        case PropertyInfo pi:
                            MethodInfo? getter = pi.GetGetMethod(true);
                            shouldInclude = includePrivateMembers;  // Default for properties with no accessor
                            if (getter != null)
                            {
                                shouldInclude = includePrivateMembers || getter.IsPublic || getter.IsAssembly || getter.IsFamily || getter.IsFamilyOrAssembly;
                            }
                            accessibilityString = ToolHelpers.GetReflectionMemberModifiersString(pi);
                            break;
                        case EventInfo ei:
                            MethodInfo? adder = ei.GetAddMethod(true);
                            shouldInclude = includePrivateMembers;  // Default for events with no accessor
                            if (adder != null)
                            {
                                shouldInclude = includePrivateMembers || adder.IsPublic || adder.IsAssembly || adder.IsFamily || adder.IsFamilyOrAssembly;
                            }
                            accessibilityString = ToolHelpers.GetReflectionMemberModifiersString(ei);
                            break;
                        case Type nt: // Nested Type
                            shouldInclude = includePrivateMembers || nt.IsPublic || nt.IsNestedPublic || nt.IsNestedAssembly ||
                                nt.IsNestedFamily || nt.IsNestedFamORAssem;
                            accessibilityString = ToolHelpers.GetReflectionMemberModifiersString(nt);
                            break;
                        default:
                            shouldInclude = includePrivateMembers;
                            break;
                    }

                    if (shouldInclude)
                    {
                        apiMembers.Add(new
                        {
                            name = memberInfo.Name,
                            kind = ToolHelpers.GetReflectionMemberTypeKindString(memberInfo),
                            modifiers = accessibilityString,
                            signature = memberInfo.ToString(),
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error processing reflection member {MemberName}", memberInfo.Name);

                    // Add with partial information
                    if (includePrivateMembers)
                    {
                        apiMembers.Add(new
                        {
                            name = memberInfo.Name,
                            kind = "Unknown",
                            modifiers = "Error",
                            signature = $"{memberInfo.Name} (Error: {ex.Message})",
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving members for reflection type {TypeName}", reflectionType.FullName);
            throw new McpException($"Failed to retrieve members for type '{reflectionType.FullName}': {ex.Message}");
        }

        return ToolHelpers.ToJson(new
        {
            typeName = reflectionType.FullName,
            source = "Reflection",
            includesPrivateMembers = includePrivateMembers,
            members = apiMembers.OrderBy(m => ((dynamic)m).kind).ThenBy(m => ((dynamic)m).name).ToList()
        });
    }

    private static readonly string[] s_separator = ["\r\n", "\r", "\n"];

    [McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(ViewDefinition), Idempotent = true, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Displays the verbatim source code from the declaration of a target symbol (class, method, property, etc.) with indentation omitted to save tokens. Essential to fully understand a specific implementation without opening files.")]
    public static async Task<string> ViewDefinition(
        ISolutionManager solutionManager,
        ILogger<AnalysisToolsLogCategory> logger,
        ICodeAnalysisService codeAnalysisService,
        ISourceResolutionService sourceResolutionService,
        [Description("The fully qualified name of the symbol (type, method, property, etc.).")] string fullyQualifiedSymbolName,
        CancellationToken cancellationToken)
    {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () =>
        {
            ErrorHandlingHelpers.ValidateStringParameter(fullyQualifiedSymbolName, nameof(fullyQualifiedSymbolName), logger);
            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(ViewDefinition));

            logger.LogInformation("Executing '{ViewDefinition}' for: {SymbolName}", nameof(ViewDefinition), fullyQualifiedSymbolName);

            // Get the symbol or throw an exception if not found
            ISymbol roslynSymbol = await ToolHelpers.GetRoslynSymbolOrThrowAsync(solutionManager, fullyQualifiedSymbolName, cancellationToken);
            Solution? solution = solutionManager.CurrentSolution;
            List<Location> locations = [.. roslynSymbol.Locations.Where(l => l.IsInSource)];

            if (locations.Count == 0)
            {
                // No source locations found in the solution, try to resolve from external sources
                logger.LogInformation(
                    "No source locations found for '{SymbolName}' in the current solution, attempting external source resolution",
                    fullyQualifiedSymbolName);

                SourceResult? sourceResult = await sourceResolutionService.ResolveSourceAsync(roslynSymbol, cancellationToken);
                if (sourceResult != null)
                {
                    // Add relevant reference context based on the symbol type
                    string externalReferenceContext = roslynSymbol switch
                    {
                        INamedTypeSymbol type => await ContextInjectors.CreateTypeReferenceContextAsync(codeAnalysisService, logger, type, cancellationToken),
                        IMethodSymbol method => await ContextInjectors.CreateCallGraphContextAsync(codeAnalysisService, logger, method, cancellationToken),
                        _ => string.Empty
                    };

                    // Remove leading whitespace from each line of the resolved source
                    string[] sourceLines = sourceResult.Source.Split(s_separator, StringSplitOptions.None);
                    for (int i = 0; i < sourceLines.Length; i++)
                    {
                        sourceLines[i] = TrimLeadingWhitespace(sourceLines[i]);
                    }
                    string formattedSource = string.Join(Environment.NewLine, sourceLines);

                    return $"<definition>\n<referencingTypes>\n{externalReferenceContext}\n</referencingTypes>\n"
                        + $"<code file='{sourceResult.FilePath}' source='External - {sourceResult.ResolutionMethod}'>\n{formattedSource}\n</code>\n</definition>";
                }

                throw new McpException($"No source definition found for '{fullyQualifiedSymbolName}'. The symbol might be defined in metadata (compiled assembly) only and couldn't be decompiled.");
            }

            // Check if this is a partial type with multiple declarations
            bool isPartialType = roslynSymbol is INamedTypeSymbol namedTypeSymbol &&
                roslynSymbol.DeclaringSyntaxReferences.Count() > 1;

            if (isPartialType)
            {
                // Handle partial types by collecting all partial declarations
                return await HandlePartialTypeDefinitionAsync(roslynSymbol, solution, codeAnalysisService, logger, cancellationToken);
            }
            else
            {
                // Handle single declaration (non-partial or single-part symbols)
                return await HandleSingleDefinitionAsync(roslynSymbol, solution, locations.First(), codeAnalysisService, logger, cancellationToken);
            }
        }, logger, nameof(ViewDefinition), cancellationToken);
    }

    private static string TrimLeadingWhitespace(string line)
    {
        int index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }
        return index < line.Length ? line.Substring(index) : string.Empty;
    }

    [McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(ListImplementations), Idempotent = true, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Gets the locations and FQNs of all implementations of an interface or abstract method, and lists derived classes for a base class. Crucial for navigating polymorphic code and understanding implementation patterns.")]
    public static async Task<object> ListImplementations(
        ISolutionManager solutionManager,
        ICodeAnalysisService codeAnalysisService,
        ILogger<AnalysisToolsLogCategory> logger,
        [Description("The fully qualified name of the interface, abstract method, or base class.")] string fullyQualifiedSymbolName,
        CancellationToken cancellationToken)
    {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () =>
        {
            ErrorHandlingHelpers.ValidateStringParameter(fullyQualifiedSymbolName, nameof(fullyQualifiedSymbolName), logger);
            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(ListImplementations));

            logger.LogInformation("Executing '{ViewImplementations}' for: {SymbolName}",
                nameof(ListImplementations), fullyQualifiedSymbolName);

            List<object> implementations = [];
            ISymbol roslynSymbol = await ToolHelpers.GetRoslynSymbolOrThrowAsync(solutionManager, fullyQualifiedSymbolName, cancellationToken);

            try
            {
                if (roslynSymbol is INamedTypeSymbol namedTypeSymbol)
                {
                    if (namedTypeSymbol.TypeKind == TypeKind.Interface)
                    {
                        IEnumerable<ISymbol> implementingSymbols = await codeAnalysisService.FindImplementationsAsync(namedTypeSymbol, cancellationToken);
                        foreach (INamedTypeSymbol impl in implementingSymbols.OfType<INamedTypeSymbol>())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            implementations.Add(new
                            {
                                kind = ToolHelpers.GetSymbolKindString(impl),
                                signature = CodeAnalysisService.GetFormattedSignatureAsync(impl, false),
                                fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(impl),
                                location = GetDeclarationLocationInfo(impl).FirstOrDefault()
                            });
                        }
                    }
                    else if (namedTypeSymbol.IsAbstract || namedTypeSymbol.TypeKind == TypeKind.Class)
                    {
                        IEnumerable<INamedTypeSymbol> derivedClasses = await codeAnalysisService.FindDerivedClassesAsync(namedTypeSymbol, cancellationToken);
                        foreach (INamedTypeSymbol derived in derivedClasses)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            implementations.Add(new
                            {
                                kind = ToolHelpers.GetSymbolKindString(derived),
                                signature = CodeAnalysisService.GetFormattedSignatureAsync(derived, false),
                                fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(derived),
                                location = GetDeclarationLocationInfo(derived).FirstOrDefault()
                            });
                        }
                    }
                }
                else if (roslynSymbol is IMethodSymbol methodSymbol && (methodSymbol.IsAbstract || methodSymbol.IsVirtual))
                {
                    IEnumerable<ISymbol> overrides = await codeAnalysisService.FindOverridesAsync(methodSymbol, cancellationToken);
                    foreach (IMethodSymbol over in overrides.OfType<IMethodSymbol>())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        implementations.Add(new
                        {
                            kind = ToolHelpers.GetSymbolKindString(over),
                            signature = CodeAnalysisService.GetFormattedSignatureAsync(over, false),
                            fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(over),
                            location = GetDeclarationLocationInfo(over).FirstOrDefault()
                        });
                    }
                }
                else if (roslynSymbol is IPropertySymbol propSymbol && (propSymbol.IsAbstract || propSymbol.IsVirtual))
                {
                    IEnumerable<ISymbol> overrides = await codeAnalysisService.FindOverridesAsync(propSymbol, cancellationToken);
                    foreach (IPropertySymbol over in overrides.OfType<IPropertySymbol>())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        implementations.Add(new
                        {
                            kind = ToolHelpers.GetSymbolKindString(over),
                            signature = CodeAnalysisService.GetFormattedSignatureAsync(over, false),
                            fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(over),
                            location = GetDeclarationLocationInfo(over).FirstOrDefault()
                        });
                    }
                }
                else if (roslynSymbol is IEventSymbol eventSymbol && (eventSymbol.IsAbstract || eventSymbol.IsVirtual))
                {
                    IEnumerable<ISymbol> overrides = await codeAnalysisService.FindOverridesAsync(eventSymbol, cancellationToken);
                    foreach (IEventSymbol over in overrides.OfType<IEventSymbol>())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        implementations.Add(new
                        {
                            kind = ToolHelpers.GetSymbolKindString(over),
                            signature = CodeAnalysisService.GetFormattedSignatureAsync(over, false),
                            fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(over),
                            location = GetDeclarationLocationInfo(over).FirstOrDefault()
                        });
                    }
                }
                else
                {
                    throw new McpException($"Symbol '{fullyQualifiedSymbolName}' is not an interface, abstract/virtual member, or class.");
                }

                implementations = [.. implementations.OrderBy(i => ((dynamic)i).signature)];
            }
            catch (Exception ex) when ((ex is McpException || ex is OperationCanceledException) == false)
            {
                logger.LogError(ex, "Error finding implementations for symbol {SymbolName}", fullyQualifiedSymbolName);
                throw new McpException($"Error finding implementations for symbol '{fullyQualifiedSymbolName}': {ex.Message}");
            }

            return ToolHelpers.ToJson(new
            {
                kind = ToolHelpers.GetSymbolKindString(roslynSymbol),
                signature = CodeAnalysisService.GetFormattedSignatureAsync(roslynSymbol, false),
                fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(roslynSymbol),
                location = GetDeclarationLocationInfo(roslynSymbol).FirstOrDefault(),
                implementations
            });
        }, logger, nameof(ListImplementations), cancellationToken);
    }

    private static async Task<string> HandlePartialTypeDefinitionAsync(
        ISymbol roslynSymbol,
        Solution? solution,
        ICodeAnalysisService codeAnalysisService,
        ILogger<AnalysisToolsLogCategory> logger,
        CancellationToken cancellationToken)
    {
        INamedTypeSymbol namedTypeSymbol = (INamedTypeSymbol)roslynSymbol;
        List<string> allSourceCode = [];
        List<string> allFiles = [];

        // Generate reference context for the type
        string referenceContext = await ContextInjectors.CreateTypeReferenceContextAsync(codeAnalysisService, logger, namedTypeSymbol, cancellationToken);

        foreach (SyntaxReference syntaxRef in roslynSymbol.DeclaringSyntaxReferences)
        {
            if (syntaxRef.SyntaxTree?.FilePath == null)
            {
                continue;
            }

            Document? document = solution?.GetDocument(syntaxRef.SyntaxTree);
            if (document == null)
            {
                logger.LogWarning("Could not find document for partial declaration in file: {FilePath}", syntaxRef.SyntaxTree.FilePath);
                continue;
            }

            SyntaxNode node = await syntaxRef.GetSyntaxAsync(cancellationToken);

            // Find the appropriate parent node that represents the full definition
            SyntaxNode? definitionNode = node;
            while (definitionNode != null &&
                (definitionNode is MemberDeclarationSyntax) == false &&
                (definitionNode is TypeDeclarationSyntax) == false)
            {
                definitionNode = definitionNode.Parent;
            }

            if (definitionNode == null)
            {
                logger.LogWarning("Could not find definition syntax for partial declaration in file: {FilePath}", syntaxRef.SyntaxTree.FilePath);
                continue;
            }

            string result = definitionNode.ToString();
            string filePath = document.FilePath ?? "unknown location";
            FileLinePositionSpan lineInfo = definitionNode.GetLocation().GetLineSpan();

            // Remove leading whitespace from each line
            string[] lines = result.Split(s_separator, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = TrimLeadingWhitespace(lines[i]);
            }
            result = string.Join(Environment.NewLine, lines);

            allFiles.Add($"{filePath} (Lines {lineInfo.StartLinePosition.Line + 1} - {lineInfo.EndLinePosition.Line + 1})");
            allSourceCode.Add($"<code partialDefinitionFile='{filePath}' lines='{lineInfo.StartLinePosition.Line + 1} - {lineInfo.EndLinePosition.Line + 1}'>\n{result}\n</code>");
        }

        string combinedSource = string.Join("\n\n", allSourceCode);
        string fileList = string.Join(", ", allFiles);

        return $"<definition>\n{referenceContext}\n// Partial type found in {allFiles.Count} files: {fileList}\n\n{combinedSource}\n</definition>";
    }

    private static async Task<string> HandleSingleDefinitionAsync(
        ISymbol roslynSymbol,
        Solution? solution,
        Location sourceLocation,
        ICodeAnalysisService codeAnalysisService,
        ILogger<AnalysisToolsLogCategory> logger,
        CancellationToken cancellationToken)
    {
        if (sourceLocation.SourceTree == null)
        {
            throw new McpException($"Source tree not available for symbol '{roslynSymbol.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal)}'.");
        }

        Document? document = (solution?.GetDocument(sourceLocation.SourceTree)) ?? throw new McpException($"Could not find document for symbol '{roslynSymbol.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal)}'.");
        SyntaxNode symbolSyntax = await sourceLocation.SourceTree.GetRootAsync(cancellationToken);
        SyntaxNode node = symbolSyntax.FindNode(sourceLocation.SourceSpan) ?? throw new McpException($"Could not find syntax node for symbol '{roslynSymbol.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal)}'.");

        // Find the appropriate parent node that represents the full definition
        SyntaxNode? definitionNode = node;
        if (roslynSymbol is IMethodSymbol || roslynSymbol is IPropertySymbol ||
            roslynSymbol is IFieldSymbol || roslynSymbol is IEventSymbol ||
            roslynSymbol is INamedTypeSymbol)
        {
            while (definitionNode != null &&
                (definitionNode is MemberDeclarationSyntax) == false &&
                (definitionNode is TypeDeclarationSyntax) == false)
            {
                definitionNode = definitionNode.Parent;
            }
        }

        if (definitionNode == null)
        {
            throw new McpException($"Could not find definition syntax for symbol '{roslynSymbol.ToDisplayString(ToolHelpers.FullyQualifiedFormatWithoutGlobal)}'.");
        }

        string result = definitionNode.ToString();
        string filePath = document.FilePath ?? "unknown location";
        FileLinePositionSpan lineInfo = definitionNode.GetLocation().GetLineSpan();

        // Generate reference context based on symbol type
        string referenceContext = roslynSymbol switch
        {
            INamedTypeSymbol type => await ContextInjectors.CreateTypeReferenceContextAsync(codeAnalysisService, logger, type, cancellationToken),
            IMethodSymbol method => await ContextInjectors.CreateCallGraphContextAsync(codeAnalysisService, logger, method, cancellationToken),
            _ => string.Empty // TODO: consider adding context for properties, fields, events, etc.
        };

        // Remove leading whitespace from each line
        string[] lines = result.Split(s_separator, StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = TrimLeadingWhitespace(lines[i]);
        }
        result = string.Join(Environment.NewLine, lines);

        return $"<definition>\n{referenceContext}\n<code file='{filePath}' lines='{lineInfo.StartLinePosition.Line + 1} - {lineInfo.EndLinePosition.Line + 1}'>\n{result}\n</code>\n</definition>";
    }

    public class LocationInfo
    {
        public string FilePath { get; set; } = string.Empty;
        public int StartLine { get; set; }
        public int EndLine { get; set; }
    }

    private static List<LocationInfo> GetDeclarationLocationInfo(ISymbol symbol)
    {
        List<LocationInfo> locations = [];

        foreach (SyntaxReference syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            if (syntaxRef.SyntaxTree?.FilePath == null)
            {
                continue;
            }

            SyntaxNode node = syntaxRef.GetSyntax();
            FileLinePositionSpan fullSpan = node switch
            {
                TypeDeclarationSyntax typeNode => typeNode.GetLocation().GetLineSpan(),
                MethodDeclarationSyntax methodNode => methodNode.GetLocation().GetLineSpan(),
                PropertyDeclarationSyntax propertyNode => propertyNode.GetLocation().GetLineSpan(),
                EventDeclarationSyntax eventNode => eventNode.GetLocation().GetLineSpan(),
                LocalFunctionStatementSyntax localFuncNode => localFuncNode.GetLocation().GetLineSpan(),
                ConstructorDeclarationSyntax ctorNode => ctorNode.GetLocation().GetLineSpan(),
                DestructorDeclarationSyntax dtorNode => dtorNode.GetLocation().GetLineSpan(),
                OperatorDeclarationSyntax opNode => opNode.GetLocation().GetLineSpan(),
                ConversionOperatorDeclarationSyntax convNode => convNode.GetLocation().GetLineSpan(),
                FieldDeclarationSyntax fieldNode => fieldNode.GetLocation().GetLineSpan(),
                DelegateDeclarationSyntax delegateNode => delegateNode.GetLocation().GetLineSpan(),
                // For any other symbol, use its direct location span
                _ => node.GetLocation().GetLineSpan()
            };

            locations.Add(new LocationInfo
            {
                FilePath = syntaxRef.SyntaxTree.FilePath,
                StartLine = fullSpan.StartLinePosition.Line + 1,
                EndLine = fullSpan.EndLinePosition.Line + 1
            });
        }

        // If we couldn't get any locations from DeclaringSyntaxReferences (rare), fall back to Locations
        if (locations.Count == 0)
        {
            foreach (Location location in symbol.Locations.Where(l => l.IsInSource))
            {
                if (location.SourceTree?.FilePath == null)
                {
                    continue;
                }

                FileLinePositionSpan lineSpan = location.GetLineSpan();
                locations.Add(new LocationInfo
                {
                    FilePath = location.SourceTree.FilePath,
                    StartLine = lineSpan.StartLinePosition.Line + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1
                });
            }
        }

        return locations;
    }

    [McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(FindReferences), Idempotent = true, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Finds all references to a specified symbol with surrounding context. Indentation is omitted to save space. Critical for understanding symbol usage patterns across the codebase before editing the target.")]
    public static async Task<object> FindReferences(
        ISolutionManager solutionManager,
        ICodeAnalysisService codeAnalysisService,
        ILogger<AnalysisToolsLogCategory> logger,
        [Description("The FQN of the symbol.")] string fullyQualifiedSymbolName,
        CancellationToken cancellationToken)
    {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () =>
        {
            ErrorHandlingHelpers.ValidateStringParameter(fullyQualifiedSymbolName, nameof(fullyQualifiedSymbolName), logger);
            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(FindReferences));

            logger.LogInformation("Executing '{FindReferences}' for: {SymbolName}",
                nameof(FindReferences), fullyQualifiedSymbolName);

            ISymbol symbol = await ToolHelpers.GetRoslynSymbolOrThrowAsync(solutionManager, fullyQualifiedSymbolName, cancellationToken);
            IEnumerable<ReferencedSymbol> referencedSymbols = await codeAnalysisService.FindReferencesAsync(symbol, cancellationToken);

            List<object> references = [];
            int maxToShow = 20;
            int count = 0;

            try
            {
                foreach (ReferencedSymbol refGroup in referencedSymbols)
                {
                    foreach (ReferenceLocation location in refGroup.Locations)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (count >= maxToShow)
                        {
                            break;
                        }

                        if (location.Document != null && location.Location.IsInSource)
                        {
                            try
                            {
                                SyntaxTree? sourceTree = location.Location.SourceTree;
                                if (sourceTree == null)
                                {
                                    logger.LogWarning("Null source tree for reference location in {FilePath}",
                                        location.Document.FilePath ?? "unknown file");
                                    continue;
                                }

                                SourceText sourceText = await sourceTree.GetTextAsync(cancellationToken);
                                FileLinePositionSpan lineSpan = location.Location.GetLineSpan();

                                List<string> contextLines = [];
                                const int linesAround = 2;
                                for (int i = Math.Max(0, lineSpan.StartLinePosition.Line - linesAround);
                                    i <= Math.Min(sourceText.Lines.Count - 1, lineSpan.EndLinePosition.Line + linesAround);
                                    i++)
                                {
                                    contextLines.Add(TrimLeadingWhitespace(sourceText.Lines[i].ToString()));
                                }

                                string parentMember = "N/A";
                                try
                                {
                                    SyntaxNode syntaxRoot = await sourceTree.GetRootAsync(cancellationToken);
                                    SyntaxToken token = syntaxRoot.FindToken(location.Location.SourceSpan.Start);

                                    if (token.Parent != null)
                                    {
                                        MemberDeclarationSyntax? memberDecl = token.Parent
                                            .AncestorsAndSelf()
                                            .OfType<MemberDeclarationSyntax>()
                                            .FirstOrDefault();

                                        if (memberDecl != null)
                                        {
                                            SemanticModel? semanticModel = await solutionManager.GetSemanticModelAsync(location.Document.Id, cancellationToken);
                                            ISymbol? parentSymbol = semanticModel?.GetDeclaredSymbol(memberDecl, cancellationToken);
                                            if (parentSymbol != null)
                                            {
                                                parentMember = CodeAnalysisService.GetFormattedSignatureAsync(parentSymbol, false) +
                                                    $" //FQN: {FuzzyFqnLookupService.GetSearchableString(parentSymbol)}";
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger.LogWarning(ex, "Error getting parent member for reference in {FilePath}",
                                        location.Document.FilePath ?? "unknown file");
                                }

                                references.Add(new
                                {
                                    location = new
                                    {
                                        filePath = location.Document.FilePath,
                                        startLine = lineSpan.StartLinePosition.Line + 1,
                                        endLine = lineSpan.EndLinePosition.Line + 1,
                                    },
                                    context = string.Join(Environment.NewLine, contextLines),
                                    parentMember
                                });
                                count++;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                logger.LogWarning(ex, "Error processing reference location in {FilePath}",
                                    location.Document.FilePath ?? "unknown file");
                            }
                        }
                    }
                    if (count >= maxToShow)
                    {
                        break;
                    }
                }

                int totalReferences = referencedSymbols.Sum(rs => rs.Locations.Count());
                return ToolHelpers.ToJson(new
                {
                    kind = ToolHelpers.GetSymbolKindString(symbol),
                    signature = CodeAnalysisService.GetFormattedSignatureAsync(symbol, false),
                    fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(symbol),
                    location = GetDeclarationLocationInfo(symbol).FirstOrDefault(),
                    totalReferences,
                    displayedReferences = count,
                    references = references.OrderBy(r => ((dynamic)r).location.filePath)
                        .ThenBy(r => ((dynamic)r).location.startLine)
                        .ToList()
                });
            }
            catch (Exception ex) when ((ex is McpException || ex is OperationCanceledException) == false)
            {
                logger.LogError(ex, "Error collecting references for symbol {SymbolName}", fullyQualifiedSymbolName);

                if (references.Count > 0)
                {
                    logger.LogInformation("Returning partial references ({Count}) for {SymbolName}",
                        references.Count, fullyQualifiedSymbolName);

                    return ToolHelpers.ToJson(new
                    {
                        kind = ToolHelpers.GetSymbolKindString(symbol),
                        signature = CodeAnalysisService.GetFormattedSignatureAsync(symbol, false),
                        fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(symbol),
                        location = GetDeclarationLocationInfo(symbol).FirstOrDefault(),
                        totalReferences = references.Count,
                        displayedReferences = references.Count,
                        references = references.OrderBy(r => ((dynamic)r).location.filePath)
                            .ThenBy(r => ((dynamic)r).location.startLine)
                            .ToList(),
                        partialResults = true,
                        errorMessage = $"Only partial results available due to error: {ex.Message}"
                    });
                }

                throw new McpException($"Failed to find references for symbol '{fullyQualifiedSymbolName}': {ex.Message}");
            }
        }, logger, nameof(FindReferences), cancellationToken);
    }

    //Disabled for now, didn't seem useful, should be partially included in view definition
    //[McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(ViewInheritanceChain), Idempotent = true, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Shows the inheritance hierarchy for a class or interface (base types and derived types). Essential for understanding type relationships and architecture.")]
    public static async Task<object> ViewInheritanceChain(
        ISolutionManager solutionManager,
        ICodeAnalysisService codeAnalysisService,
        ILogger<AnalysisToolsLogCategory> logger,
        [Description("The fully qualified name of the type.")] string fullyQualifiedTypeName,
        CancellationToken cancellationToken)
    {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () =>
        {
            ErrorHandlingHelpers.ValidateStringParameter(fullyQualifiedTypeName, nameof(fullyQualifiedTypeName), logger);
            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(ViewInheritanceChain));

            logger.LogInformation("Executing '{ViewInheritanceChain}' for: {TypeName}",
                nameof(ViewInheritanceChain), fullyQualifiedTypeName);

            List<object> baseTypes = [];
            List<object> derivedTypes = [];
            bool hasPartialResults = false;
            string? errorMessage = null;

            try
            {
                INamedTypeSymbol roslynSymbol = await ToolHelpers.GetRoslynNamedTypeSymbolOrThrowAsync(solutionManager, fullyQualifiedTypeName, cancellationToken);
                try
                {
                    // Get base types
                    INamedTypeSymbol? currentType = roslynSymbol.BaseType;
                    while (currentType != null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        baseTypes.Add(new
                        {
                            kind = ToolHelpers.GetSymbolKindString(currentType),
                            signature = CodeAnalysisService.GetFormattedSignatureAsync(currentType, false),
                            fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(currentType),
                            location = GetDeclarationLocationInfo(currentType).FirstOrDefault()
                        });
                        currentType = currentType.BaseType;
                    }

                    // Get interfaces
                    try
                    {
                        foreach (INamedTypeSymbol iface in roslynSymbol.Interfaces)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            baseTypes.Add(new
                            {
                                kind = ToolHelpers.GetSymbolKindString(iface),
                                signature = CodeAnalysisService.GetFormattedSignatureAsync(iface, false),
                                fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(iface),
                                location = GetDeclarationLocationInfo(iface).FirstOrDefault(),
                                isInterface = true
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error retrieving interfaces for type {TypeName}", fullyQualifiedTypeName);
                        hasPartialResults = true;
                        errorMessage = $"Could not retrieve all interfaces: {ex.Message}";
                    }

                    // Get derived types
                    try
                    {
                        if (roslynSymbol.TypeKind == TypeKind.Class || roslynSymbol.TypeKind == TypeKind.Interface)
                        {
                            bool isInterface = roslynSymbol.TypeKind == TypeKind.Interface;
                            IEnumerable<ISymbol> derived = isInterface
                                ? await codeAnalysisService.FindImplementationsAsync(roslynSymbol, cancellationToken)
                                : await codeAnalysisService.FindDerivedClassesAsync(roslynSymbol, cancellationToken);

                            foreach (INamedTypeSymbol derivedSymbol in derived.OfType<INamedTypeSymbol>())
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                derivedTypes.Add(new
                                {
                                    kind = ToolHelpers.GetSymbolKindString(derivedSymbol),
                                    signature = CodeAnalysisService.GetFormattedSignatureAsync(derivedSymbol, false),
                                    fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(derivedSymbol),
                                    location = GetDeclarationLocationInfo(derivedSymbol).FirstOrDefault()
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error retrieving derived types for {TypeName}", fullyQualifiedTypeName);
                        hasPartialResults = true;
                        errorMessage = errorMessage == null
                            ? $"Could not retrieve all derived types: {ex.Message}"
                            : $"{errorMessage}; Could not retrieve all derived types: {ex.Message}";
                    }

                    baseTypes = [.. baseTypes.OrderBy(t => ((dynamic)t).signature)];
                    derivedTypes = [.. derivedTypes.OrderBy(t => ((dynamic)t).signature)];

                    return ToolHelpers.ToJson(new
                    {
                        kind = ToolHelpers.GetSymbolKindString(roslynSymbol),
                        signature = CodeAnalysisService.GetFormattedSignatureAsync(roslynSymbol, false),
                        fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(roslynSymbol),
                        location = GetDeclarationLocationInfo(roslynSymbol).FirstOrDefault(),
                        baseTypes,
                        derivedTypes,
                        hasPartialResults,
                        errorMessage
                    });
                }
                catch (Exception ex) when ((ex is McpException || ex is OperationCanceledException) == false)
                {
                    logger.LogError(ex, "Error analyzing inheritance chain for Roslyn type {TypeName}", fullyQualifiedTypeName);
                    throw new McpException($"Error analyzing inheritance chain: {ex.Message}");
                }
            }
            catch (McpException ex)
            {
                logger.LogDebug(ex, "Roslyn symbol not found for {TypeName} or error occurred, trying reflection.", fullyQualifiedTypeName);
                // Fall through to reflection if Roslyn symbol not found or other McpException related to Roslyn.
            }

            // Try reflection type if Roslyn symbol processing failed
            try
            {
                Type reflectionType = await ToolHelpers.GetReflectionTypeOrThrowAsync(solutionManager, fullyQualifiedTypeName, cancellationToken);
                try
                {
                    // Get base types
                    Type? currentBase = reflectionType.BaseType;
                    while (currentBase != null && currentBase != typeof(object))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        baseTypes.Add(new
                        {
                            kind = ToolHelpers.GetReflectionTypeKindString(currentBase),
                            signature = ToolHelpers.GetReflectionTypeModifiersString(currentBase) + " " + currentBase.FullName,
                            assemblyName = currentBase.Assembly.GetName().Name
                        });
                        currentBase = currentBase.BaseType;
                    }
                    if (reflectionType.BaseType == typeof(object) ||
                        (reflectionType.IsClass && reflectionType.BaseType == null && reflectionType != typeof(object)))
                    {
                        baseTypes.Add(new
                        {
                            kind = "Class",
                            signature = "public class System.Object",
                            assemblyName = typeof(object).Assembly.GetName().Name
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error retrieving base types for reflection type {TypeName}", fullyQualifiedTypeName);
                    hasPartialResults = true;
                    errorMessage = $"Could not retrieve all base types: {ex.Message}";
                }

                try
                {
                    // Get interfaces
                    foreach (Type iface in reflectionType.GetInterfaces())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        baseTypes.Add(new
                        {
                            kind = "Interface",
                            signature = ToolHelpers.GetReflectionTypeModifiersString(iface) + " " + iface.FullName,
                            assemblyName = iface.Assembly.GetName().Name,
                            isInterface = true
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error retrieving interfaces for reflection type {TypeName}", fullyQualifiedTypeName);
                    hasPartialResults = true;
                    errorMessage = errorMessage == null
                        ? $"Could not retrieve all interfaces: {ex.Message}"
                        : $"{errorMessage}; Could not retrieve all interfaces: {ex.Message}";
                }

                derivedTypes.Add(new
                {
                    kind = "Note",
                    signature = "Derived type discovery for pure reflection types is limited in this tool version."
                });

                baseTypes = [.. baseTypes.OrderBy(t => ((dynamic)t).signature)];

                return ToolHelpers.ToJson(new
                {
                    kind = reflectionType.IsInterface ? "Interface" : (reflectionType.IsEnum ? "Enum" : "Class"),
                    signature = ToolHelpers.GetReflectionTypeModifiersString(reflectionType) + " " + reflectionType.FullName,
                    assemblyName = reflectionType.Assembly.GetName().Name,
                    baseTypes,
                    derivedTypes,
                    hasPartialResults,
                    errorMessage
                });
            }
            catch (Exception ex) when ((ex is McpException || ex is OperationCanceledException) == false)
            {
                logger.LogError(ex, "Error analyzing inheritance chain for reflection type {TypeName}", fullyQualifiedTypeName);
                throw new McpException($"Error analyzing inheritance chain via reflection: {ex.Message}");
            }
        }, logger, nameof(ViewInheritanceChain), cancellationToken);
    }

    //Disabled for now as it should be included in ViewDefinition
    //[McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(ViewCallGraph), Idempotent = true, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Displays methods that call a specific method (incoming) and methods called by it (outgoing). Critical for understanding control flow and method relationships across the codebase.")]
    public static async Task<object> ViewCallGraph(
        ISolutionManager solutionManager,
        ICodeAnalysisService codeAnalysisService,
        ILogger<AnalysisToolsLogCategory> logger,
        [Description("The FQN of the method.")] string fullyQualifiedMethodName,
        CancellationToken cancellationToken)
    {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () =>
        {
            ErrorHandlingHelpers.ValidateStringParameter(fullyQualifiedMethodName, nameof(fullyQualifiedMethodName), logger);
            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(ViewCallGraph));

            logger.LogInformation("Executing '{ViewCallGraph}' for: {MethodName}",
                nameof(ViewCallGraph), fullyQualifiedMethodName);

            ISymbol symbol = await ToolHelpers.GetRoslynSymbolOrThrowAsync(solutionManager, fullyQualifiedMethodName, cancellationToken);
            if (symbol is not IMethodSymbol methodSymbol)
            {
                throw new McpException($"Symbol '{fullyQualifiedMethodName}' is not a method.");
            }

            List<object> incomingCalls = [];
            List<object> outgoingCalls = [];
            bool hasPartialResults = false;
            string? errorMessage = null;

            try
            {
                // Get incoming calls (callers)
                IEnumerable<SymbolCallerInfo> callers = await codeAnalysisService.FindCallersAsync(methodSymbol, cancellationToken);
                foreach (SymbolCallerInfo callerInfo in callers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var callLocations = callerInfo.Locations
                            .Where(l => l.IsInSource && l.SourceTree != null)
                            .Select(l =>
                            {
                                FileLinePositionSpan lineSpan = l.GetLineSpan();
                                return new
                                {
                                    line = lineSpan.StartLinePosition.Line + 1,
                                };
                            })
                            .ToList();

                        incomingCalls.Add(new
                        {
                            kind = ToolHelpers.GetSymbolKindString(callerInfo.CallingSymbol),
                            signature = CodeAnalysisService.GetFormattedSignatureAsync(callerInfo.CallingSymbol, false),
                            fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(callerInfo.CallingSymbol),
                            location = GetDeclarationLocationInfo(callerInfo.CallingSymbol).FirstOrDefault(),
                            callLocations
                        });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Error processing caller {CallerSymbol} for method {MethodName}",
                            callerInfo.CallingSymbol.Name, fullyQualifiedMethodName);
                        hasPartialResults = true;
                    }
                }

                // Get outgoing calls (callees)
                IEnumerable<ISymbol> outgoingSymbols = await codeAnalysisService.FindOutgoingCallsAsync(methodSymbol, cancellationToken);
                foreach (ISymbol callee in outgoingSymbols)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        outgoingCalls.Add(new
                        {
                            kind = ToolHelpers.GetSymbolKindString(callee),
                            calleeSignature = CodeAnalysisService.GetFormattedSignatureAsync(callee, false),
                            fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(callee),
                            location = GetDeclarationLocationInfo(callee).FirstOrDefault()
                        });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Error processing callee {CalleeSymbol} for method {MethodName}",
                            callee.Name, fullyQualifiedMethodName);
                        hasPartialResults = true;
                    }
                }

                incomingCalls = [.. incomingCalls.OrderBy(c => ((dynamic)c).signature)];

                outgoingCalls = [.. outgoingCalls.OrderBy(c => ((dynamic)c).signature)];
            }
            catch (Exception ex) when ((ex is McpException || ex is OperationCanceledException) == false)
            {
                logger.LogWarning(ex, "Error finding call graph for method {MethodName}", fullyQualifiedMethodName);
                hasPartialResults = true;
                errorMessage = $"Could not retrieve complete call graph: {ex.Message}";
            }

            return ToolHelpers.ToJson(new
            {
                kind = ToolHelpers.GetSymbolKindString(methodSymbol),
                signature = CodeAnalysisService.GetFormattedSignatureAsync(methodSymbol, false),
                fullyQualifiedName = FuzzyFqnLookupService.GetSearchableString(methodSymbol),
                location = GetDeclarationLocationInfo(methodSymbol).FirstOrDefault(),
                incomingCalls,
                outgoingCalls,
                hasPartialResults,
                errorMessage
            });
        }, logger, nameof(ViewCallGraph), cancellationToken);
    }

    [McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(SearchDefinitions), Idempotent = true, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Dual-engine pattern search across source code AND compiled assemblies for public APIs. Perfect for finding all implementations of a pattern - e.g., finding all async methods with 'ConfigureAwait', or all classes implementing IDisposable. Searches declarations, signatures, and type hierarchies.")]
    public static async Task<object> SearchDefinitions(
        ISolutionManager solutionManager,
        ILogger<AnalysisToolsLogCategory> logger,
        [Description("The regex pattern to match against full declaration text (multiline) and symbol names.")] string regexPattern,
        CancellationToken cancellationToken)
    {
        // Maximum number of search results to return
        const int MaxSearchResults = 20;

        static bool IsGeneratedCode(string signature)
        {
            return signature.Contains("+<")                  // Generated closures
                || signature.Contains("<>")                  // Generated closures and async state machines
                || signature.Contains("+d__")               // Async state machines
                || signature.Contains("__Generated")        // Generated code marker
                || signature.Contains("<Clone>")            // Generated clone methods
                || signature.Contains("<BackingField>")     // Generated backing fields
                || signature.Contains(".+")                 // Generated nested types
                || signature.Contains('$')                  // Generated interop types
                || signature.Contains("__Backing__")        // Generated backing fields
                || (signature.Contains('_') && signature.Contains('<'))  // Generated async methods
                || (signature.Contains('+') && signature.Contains('`')); // Generic factory-created types
        }

        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () =>
        {
            ErrorHandlingHelpers.ValidateStringParameter(regexPattern, nameof(regexPattern), logger);
            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(SearchDefinitions));

            logger.LogInformation("Executing '{SearchDefinitions}' with pattern: {RegexPattern}",
                nameof(SearchDefinitions), regexPattern);

            Regex regex;
            try
            {
                regex = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
            }
            catch (ArgumentException ex)
            {
                throw new McpException($"Invalid regular expression pattern: {ex.Message}");
            }

            var matches = new ConcurrentBag<dynamic>();
            bool hasPartialResults = false;
            ConcurrentBag<string> errors = [];
            int projectsProcessed = 0;
            int projectsSkipped = 0;
            ConcurrentDictionary<string, HashSet<TextSpan>> matchedNodeSpans = new();
            int totalMatchesFound = 0;

            try
            {
                IEnumerable<Task> projectTasks = solutionManager.GetProjects().Select(project => Task.Run(async () =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Compilation? compilation = await solutionManager.GetCompilationAsync(project.Id, cancellationToken);
                        if (compilation == null)
                        {
                            Interlocked.Increment(ref projectsSkipped);
                            logger.LogWarning("Skipping project {ProjectName}, compilation is null", project.Name);
                            return;
                        }

                        foreach (SyntaxTree syntaxTree in compilation.SyntaxTrees)
                        {
                            try
                            {
                                // Check if we've already exceeded the result limit
                                if (Interlocked.CompareExchange(ref totalMatchesFound, 0, 0) >= MaxSearchResults)
                                {
                                    hasPartialResults = true;
                                    break;
                                }

                                cancellationToken.ThrowIfCancellationRequested();
                                SemanticModel semanticModel = compilation.GetSemanticModel(syntaxTree);
                                SourceText sourceText = await syntaxTree.GetTextAsync(cancellationToken);
                                string filePath = syntaxTree.FilePath ?? "unknown file";

                                SyntaxNode root = await syntaxTree.GetRootAsync(cancellationToken);
                                Dictionary<SyntaxNode, List<Match>> matchedNodesInFile = [];

                                // First pass: Find all nodes with regex matches
                                foreach (SyntaxNode node in root.DescendantNodes()
                                    .Where(n => n is MemberDeclarationSyntax or VariableDeclaratorSyntax))
                                {
                                    cancellationToken.ThrowIfCancellationRequested();

                                    // Check if we've already exceeded the result limit
                                    if (Interlocked.CompareExchange(ref totalMatchesFound, 0, 0) >= MaxSearchResults)
                                    {
                                        hasPartialResults = true;
                                        break;
                                    }

                                    try
                                    {
                                        string declText = node.ToString();
                                        MatchCollection declMatches = regex.Matches(declText);
                                        if (declMatches.Count > 0)
                                        {
                                            matchedNodesInFile.Add(node, [.. declMatches.Cast<Match>()]);
                                            if (matchedNodeSpans.TryGetValue(filePath, out var spans) == false)
                                            {
                                                spans = [];
                                                matchedNodeSpans[filePath] = spans;
                                            }
                                            spans.Add(node.Span);
                                        }
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException)
                                    {
                                        logger.LogTrace(ex, "Error examining node in {FilePath}", filePath);
                                        hasPartialResults = true;
                                    }
                                }

                                // Second pass: Process only nodes that don't have a matched child
                                foreach (var (node, nodeMatches) in matchedNodesInFile)
                                {
                                    // Check if we've already exceeded the result limit
                                    if (Interlocked.CompareExchange(ref totalMatchesFound, 0, 0) >= MaxSearchResults)
                                    {
                                        hasPartialResults = true;
                                        break;
                                    }

                                    try
                                    {
                                        if (matchedNodeSpans.TryGetValue(filePath, out var spans) &&
                                            node.DescendantNodes().Any(child => spans.Contains(child.Span) && child != node))
                                        {
                                            continue;
                                        }

                                        ISymbol? symbol = node switch
                                        {
                                            MemberDeclarationSyntax mds => semanticModel.GetDeclaredSymbol(mds, cancellationToken),
                                            VariableDeclaratorSyntax vds => semanticModel.GetDeclaredSymbol(vds, cancellationToken),
                                            _ => null
                                        };

                                        if (symbol != null)
                                        {
                                            string signature = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                                            if (IsGeneratedCode(signature))
                                            {
                                                continue;
                                            }

                                            // Get containing type
                                            INamedTypeSymbol? containingType = symbol.ContainingType;
                                            ISymbol containingSymbol = symbol.ContainingSymbol;
                                            string parentFqn;

                                            if (containingType != null)
                                            {
                                                // For members inside a type
                                                parentFqn = FuzzyFqnLookupService.GetSearchableString(containingType);
                                                if (IsGeneratedCode(parentFqn))
                                                {
                                                    continue;
                                                }
                                            }
                                            else if (containingSymbol != null && containingSymbol.Kind == SymbolKind.Namespace)
                                            {
                                                // For top-level types in a namespace
                                                parentFqn = FuzzyFqnLookupService.GetSearchableString(containingSymbol);
                                            }
                                            else
                                            {
                                                // Fallback for other cases
                                                parentFqn = "global";
                                            }

                                            // Process each match in the declaration
                                            foreach (Match match in nodeMatches)
                                            {
                                                // Check if we've already exceeded the result limit
                                                if (Interlocked.CompareExchange(ref totalMatchesFound, 0, 0) >= MaxSearchResults)
                                                {
                                                    hasPartialResults = true;
                                                    break;
                                                }

                                                int matchStartPos = node.SpanStart + match.Index;
                                                LinePosition matchLinePos = sourceText.Lines.GetLinePosition(matchStartPos);
                                                int matchLineNumber = matchLinePos.Line + 1;
                                                string matchLine = sourceText.Lines[matchLinePos.Line].ToString().Trim();

                                                if (Interlocked.Increment(ref totalMatchesFound) <= MaxSearchResults)
                                                {
                                                    matches.Add(new
                                                    {
                                                        kind = ToolHelpers.GetSymbolKindString(symbol),
                                                        parentFqn,
                                                        signature,
                                                        match = matchLine,
                                                        location = new
                                                        {
                                                            filePath,
                                                            line = matchLineNumber
                                                        }
                                                    });
                                                }
                                                else
                                                {
                                                    hasPartialResults = true;
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException)
                                    {
                                        logger.LogTrace(ex, "Error processing syntax node in {FilePath}", filePath);
                                        hasPartialResults = true;
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                logger.LogWarning(ex, "Error processing syntax tree {FilePath}",
                                    syntaxTree.FilePath ?? "unknown file");
                                hasPartialResults = true;
                            }
                        }

                        Interlocked.Increment(ref projectsProcessed);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref projectsSkipped);
                        logger.LogWarning(ex, "Error processing project {ProjectName}", project.Name);
                        hasPartialResults = true;
                        errors.Add($"Error in project {project.Name}: {ex.Message}");
                    }
                }, cancellationToken));

                await Task.WhenAll(projectTasks);
            }
            catch (Exception ex) when ((ex is McpException || ex is OperationCanceledException) == false)
            {
                logger.LogError(ex, "Error searching Roslyn symbols with pattern {Pattern}", regexPattern);
                hasPartialResults = true;
                errors.Add($"Error searching source code symbols: {ex.Message}");
            }

            // Process reflection types in parallel with timeout
            Task reflectionSearchTask = Task.Run(async () =>
            {
                try
                {
                    // If already at limit, skip reflection search
                    if (Interlocked.CompareExchange(ref totalMatchesFound, 0, 0) >= MaxSearchResults)
                    {
                        hasPartialResults = true;
                        return;
                    }

                    //help in common case where it is looking for a class definition, but reflections don't have the 'class' keyword
                    string reflectionPattern = ClassRegex().Replace(regexPattern, string.Empty);

                    Regex reflectionRegex = new(reflectionPattern,
                        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

                    IEnumerable<Type> allTypes = await solutionManager.SearchReflectionTypesAsync(".*", cancellationToken);
                    List<Type> typesToProcess = [.. allTypes];
                    int parallelism = Math.Max(1, Environment.ProcessorCount / 2);
                    int partitionCount = Math.Min(typesToProcess.Count, parallelism);

                    if (partitionCount > 0)
                    {
                        int partitionSize = typesToProcess.Count / partitionCount;
                        List<Task> partitionTasks = [];

                        for (int i = 0; i < partitionCount; i++)
                        {
                            int startIdx = i * partitionSize;
                            int endIdx = (i == partitionCount - 1) ? typesToProcess.Count : (i + 1) * partitionSize;

                            partitionTasks.Add(Task.Run(() =>
                            {
                                for (int j = startIdx; j < endIdx && cancellationToken.IsCancellationRequested == false; j++)
                                {
                                    // Check if we've already exceeded the result limit
                                    if (Interlocked.CompareExchange(ref totalMatchesFound, 0, 0) >= MaxSearchResults)
                                    {
                                        hasPartialResults = true;
                                        break;
                                    }

                                    Type type = typesToProcess[j];
                                    try
                                    {
                                        if (IsGeneratedCode(type.FullName ?? type.Name))
                                        {
                                            continue;
                                        }

                                        bool hasMatchedMembers = false;

                                        BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.Instance |
                                            BindingFlags.Static | BindingFlags.DeclaredOnly;
                                        foreach (MemberInfo memberInfo in type.GetMembers(bindingFlags))
                                        {
                                            if (cancellationToken.IsCancellationRequested)
                                            {
                                                break;
                                            }

                                            // Check if we've already exceeded the result limit
                                            if (Interlocked.CompareExchange(ref totalMatchesFound, 0, 0) >= MaxSearchResults)
                                            {
                                                hasPartialResults = true;
                                                break;
                                            }

                                            if (memberInfo is MethodInfo mi && mi.IsSpecialName &&
                                                (mi.Name.StartsWith("get_") || mi.Name.StartsWith("set_") ||
                                                mi.Name.StartsWith("add_") || mi.Name.StartsWith("remove_")))
                                            {
                                                continue;
                                            }

                                            if (reflectionRegex.IsMatch(memberInfo.ToString() ?? memberInfo.Name))
                                            {
                                                string signature = memberInfo.ToString()!;
                                                if (IsGeneratedCode(signature))
                                                {
                                                    continue;
                                                }

                                                hasMatchedMembers = true;
                                                if (memberInfo is MethodInfo method)
                                                {
                                                    string parameters = string.Join(", ",
                                                        method.GetParameters().Select(p => p.ParameterType.Name));
                                                    signature = $"{type.FullName}.{memberInfo.Name}({parameters})";
                                                }

                                                string assemblyLocation = type.Assembly.Location;

                                                if (Interlocked.Increment(ref totalMatchesFound) <= MaxSearchResults)
                                                {
                                                    matches.Add(new
                                                    {
                                                        kind = ToolHelpers.GetReflectionMemberTypeKindString(memberInfo),
                                                        parentFqn = type.FullName ?? type.Name,
                                                        signature,
                                                        match = memberInfo.Name,
                                                        location = new
                                                        {
                                                            filePath = assemblyLocation,
                                                            line = 0 // No line numbers for reflection matches
                                                        }
                                                    });
                                                }
                                                else
                                                {
                                                    hasPartialResults = true;
                                                    break;
                                                }
                                            }
                                        }

                                        if (reflectionRegex.IsMatch(type.FullName ?? type.Name) && hasMatchedMembers == false)
                                        {
                                            // Check if we've already exceeded the result limit
                                            if (Interlocked.CompareExchange(ref totalMatchesFound, 0, 0) >= MaxSearchResults)
                                            {
                                                hasPartialResults = true;
                                                break;
                                            }

                                            string assemblyLocation = type.Assembly.Location;

                                            if (Interlocked.Increment(ref totalMatchesFound) <= MaxSearchResults)
                                            {
                                                matches.Add(new
                                                {
                                                    kind = ToolHelpers.GetReflectionTypeKindString(type),
                                                    parentFqn = type.Namespace ?? "global",
                                                    signature = type.FullName ?? type.Name,
                                                    match = type.FullName ?? type.Name,
                                                    location = new
                                                    {
                                                        filePath = assemblyLocation,
                                                        line = 0 // No line numbers for reflection matches
                                                    }
                                                });
                                            }
                                            else
                                            {
                                                hasPartialResults = true;
                                                break;
                                            }
                                        }
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException)
                                    {
                                        logger.LogWarning(ex, "Error processing reflection type {TypeName}",
                                            type.FullName ?? type.Name);
                                        hasPartialResults = true;
                                    }
                                }
                            }, cancellationToken));
                        }

                        await Task.WhenAll(partitionTasks);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error searching reflection members with pattern {Pattern}", regexPattern);
                    hasPartialResults = true;
                    errors.Add($"Error searching reflection members: {ex.Message}");
                }
            }, cancellationToken);

            try
            {
                await Task.WhenAny(reflectionSearchTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
                if (reflectionSearchTask.IsCompleted == false)
                {
                    logger.LogWarning("Reflection search timed out after 5 seconds. Returning partial results.");
                    hasPartialResults = true;
                    errors.Add("Reflection search timed out after 5 seconds, returning partial results.");
                }
                else if (reflectionSearchTask.IsFaulted && reflectionSearchTask.Exception != null)
                {
                    throw reflectionSearchTask.Exception.InnerException ?? reflectionSearchTask.Exception;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            // Group matches first by file, then by parent type/namespace, then by kind
            var groupedMatches = matches
                .OrderBy(m => ((dynamic)m).location.filePath)
                .ThenBy(m => ((dynamic)m).parentFqn)
                .ThenBy(m => ((dynamic)m).kind)
                .GroupBy(m => ((dynamic)m).location.filePath)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(m => ((dynamic)m).parentFqn)
                        .ToDictionary(
                            pg => ToolHelpers.RemoveGlobalPrefix(pg.Key),
                            pg => pg.GroupBy(m => ((dynamic)m).kind)
                                .ToDictionary(
                                    kg => kg.Key,
                                    kg => kg
                                        .Where(m => IsGeneratedCode(((dynamic)m).match) == false)
                                        .DistinctBy(m => ((dynamic)m).match)
                                        .Select(m => new
                                        {
                                            ((dynamic)m).match,
                                            line = ((dynamic)m).location.line > 0 ? ((dynamic)m).location.line : null,
                                        }).OrderBy(m => m.line).ToList()
                                )
                        )
                );

            // Prepare a message for omitted results if we hit the limit
            string? resultsLimitMessage = null;
            if (hasPartialResults)
            {
                resultsLimitMessage = $"Some search results omitted for brevity, try narrowing your search if you didn't find what you needed.";
                logger.LogInformation("Search results limited");
            }

            return ToolHelpers.ToJson(new
            {
                pattern = regexPattern,
                matchesByFile = groupedMatches,
                resultsLimitMessage,
                errors = errors.Any() ? errors.ToList() : null,
                totalMatchesFound
            });
        }, logger, nameof(SearchDefinitions), cancellationToken);
    }

    [McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(ManageUsings), Idempotent = true, ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Reads or writes using directives in a document.")]
    public static async Task<object> ManageUsings(
        ISolutionManager solutionManager,
        ICodeModificationService modificationService,
        ILogger<AnalysisToolsLogCategory> logger,
        [Description("'read' or 'write'. For 'read', set codeToWrite to 'None'.")] string operation,
        [Description("For 'read', must be 'None'. For 'write', provide all using directives that should exist in the file. This will replace all existing usings.")] string codeToWrite,
        [Description("The absolute path to the file to manage usings in")] string filePath,
        CancellationToken cancellationToken)
    {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () =>
        {
            // Validate parameters
            ErrorHandlingHelpers.ValidateStringParameter(operation, nameof(operation), logger);
            ErrorHandlingHelpers.ValidateStringParameter(filePath, nameof(filePath), logger);
            ErrorHandlingHelpers.ValidateFileExists(filePath, logger);

            if (operation != "read" && operation != "write")
            {
                throw new McpException($"Invalid operation '{operation}'. Must be 'read' or 'write'.");
            }

            if (operation == "read" && codeToWrite != "None")
            {
                throw new McpException("For read operations, codeToWrite must be 'None'");
            }

            if (operation == "write" && (codeToWrite == "None" || string.IsNullOrEmpty(codeToWrite)))
            {
                throw new McpException("For write operations, codeToWrite must contain the complete list of using directives");
            }

            // Ensure solution is loaded
            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(ManageUsings));
            Solution solution = solutionManager.CurrentSolution ?? throw new McpException("Current solution is null.");

            Document document = solution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => d.FilePath == filePath) ?? throw new McpException($"File '{filePath}' not found in solution.");

            SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken) ?? throw new McpException($"Could not get syntax root for file '{filePath}'.");

            // Find the global usings file for the project
            Document? globalUsingsFile = document.Project.Documents
                .FirstOrDefault(d => d.Name.Equals("GlobalUsings.cs", StringComparison.OrdinalIgnoreCase));

            if (operation == "read")
            {
                List<string> usingDirectives = [.. root.DescendantNodes()
                    .OfType<UsingDirectiveSyntax>()
                    .Select(u => u.ToFullString().Trim())];

                // Handle global usings separately
                List<string> globalUsings = [];
                if (globalUsingsFile != null)
                {
                    SyntaxNode? globalRoot = await globalUsingsFile.GetSyntaxRootAsync(cancellationToken);
                    if (globalRoot != null)
                    {
                        globalUsings = [.. globalRoot.DescendantNodes()
                            .OfType<UsingDirectiveSyntax>()
                            .Select(u => u.ToFullString().Trim())];
                    }
                }

                return ToolHelpers.ToJson(new
                {
                    file = filePath,
                    usings = string.Join("\n", usingDirectives),
                    globalUsings = string.Join("\n", globalUsings)
                });
            }

            // Write operation
            bool isGlobalUsings = document.FilePath!.EndsWith("GlobalUsings.cs", StringComparison.OrdinalIgnoreCase);

            // Parse and normalize directives
            List<string> directives = [.. codeToWrite.Split('\n')
                .Select(line => line.Trim())
                .Where(line => string.IsNullOrWhiteSpace(line) == false)
                .Select(line => isGlobalUsings && line.StartsWith("global ") == false ? $"global {line}" : line)];

            // Create compilation unit with new directives
            CompilationUnitSyntax? newRoot;
            try
            {
                string tempCode = string.Join("\n", directives);
                newRoot = (isGlobalUsings
                    ? CSharpSyntaxTree.ParseText(tempCode).GetRoot() as CompilationUnitSyntax
                    : ((CompilationUnitSyntax)root).WithUsings(SyntaxFactory.List(
                        CSharpSyntaxTree.ParseText(tempCode + "\nnamespace N { class C { } }")
                            .GetRoot()
                            .DescendantNodes()
                            .OfType<UsingDirectiveSyntax>()
                    ))) ?? throw new FormatException("Failed to create valid syntax tree.");
            }
            catch (Exception ex)
            {
                throw new McpException($"Failed to parse using directives: {ex.Message}");
            }

            // Apply changes
            Document newDocument = document.WithSyntaxRoot(newRoot);
            Document formatted = await modificationService.FormatDocumentAsync(newDocument, cancellationToken);
            await modificationService.ApplyChangesAsync(formatted.Project.Solution, cancellationToken, "Manage Usings");

            // Verify changes were successful
            string diffResult = ContextInjectors.CreateCodeDiff(
                root.ToFullString(),
                newRoot.ToFullString());

            if (diffResult.Trim() == "// No changes detected.")
            {
                return "Using update was successful but no difference was detected.";
            }

            return "Successfully updated usings. Diff:\n" + diffResult;
        }, logger, nameof(ManageUsings), cancellationToken);
    }

    [McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(ManageAttributes), Idempotent = true, ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Reads or writes all attributes on a declaration.")]
    public static async Task<object> ManageAttributes(
        ISolutionManager solutionManager,
        ICodeModificationService modificationService,
        ILogger<AnalysisToolsLogCategory> logger,
        [Description("'read' or 'write'. For 'read', set codeToWrite to 'None'.")] string operation,
        [Description("For 'read', must be 'None'. For 'write', specify all attributes that should exist on the target declaration. This will replace all existing attributes.")] string codeToWrite,
        [Description("The FQN of the target declaration to manage attributes for")] string targetDeclaration,
        CancellationToken cancellationToken)
    {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () =>
        {
            // Validate parameters
            ErrorHandlingHelpers.ValidateStringParameter(operation, nameof(operation), logger);
            ErrorHandlingHelpers.ValidateStringParameter(targetDeclaration, nameof(targetDeclaration), logger);

            if (operation != "read" && operation != "write")
            {
                throw new McpException($"Invalid operation '{operation}'. Must be 'read' or 'write'.");
            }

            if (operation == "read" && codeToWrite != "None")
            {
                throw new McpException("For read operations, codeToWrite must be 'None'");
            }

            if (operation == "write" && codeToWrite == "None")
            {
                throw new McpException("For write operations, codeToWrite must contain the attributes to set");
            }

            // Ensure solution is loaded and get target symbol
            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(ManageAttributes));
            ISymbol symbol = await ToolHelpers.GetRoslynSymbolOrThrowAsync(solutionManager, targetDeclaration, cancellationToken);

            if (symbol.DeclaringSyntaxReferences.Any() == false)
            {
                throw new McpException($"Symbol '{targetDeclaration}' has no declaring syntax references.");
            }

            SyntaxReference syntaxRef = symbol.DeclaringSyntaxReferences.First();
            SyntaxNode node = await syntaxRef.GetSyntaxAsync(cancellationToken);

            if (operation == "read")
            {
                // Get only the attributes on this node, not nested ones
                SyntaxList<AttributeListSyntax> attributeLists = node switch
                {
                    MemberDeclarationSyntax mDecl => mDecl.AttributeLists,
                    StatementSyntax stmt => stmt.AttributeLists,
                    _ => SyntaxFactory.List<AttributeListSyntax>()
                };

                string attributes = string.Join("\n", attributeLists.Select(al => al.ToString().Trim()));
                FileLinePositionSpan lineSpan = node.GetLocation().GetLineSpan();

                if (string.IsNullOrEmpty(attributes))
                {
                    attributes = "No attributes found.";
                }

                return ToolHelpers.ToJson(new
                {
                    file = syntaxRef.SyntaxTree.FilePath,
                    line = lineSpan.StartLinePosition.Line + 1,
                    attributes
                });
            }

            // Write operation
            if (node is not MemberDeclarationSyntax memberDecl)
            {
                throw new McpException("Target declaration is not a valid member declaration.");
            }

            SyntaxList<AttributeListSyntax> newAttributeLists;
            try
            {
                // Parse the attributes by wrapping in minimal valid syntax
                string tempCode = $"{(codeToWrite.Length == 0 ? "" : codeToWrite + "\n")}public class C {{ }}";
                newAttributeLists = CSharpSyntaxTree.ParseText(tempCode)
                    .GetRoot()
                    .DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .First()
                    .AttributeLists;
            }
            catch (Exception ex)
            {
                throw new McpException($"Failed to parse attributes: {ex.Message}");
            }

            // Create updated declaration with new attributes
            MemberDeclarationSyntax newMember = memberDecl.WithAttributeLists(newAttributeLists);
            Document document = solutionManager.CurrentSolution?.GetDocument(syntaxRef.SyntaxTree)
                ?? throw new McpException("Could not find document for syntax tree.");

            // Apply the changes
            Solution newSolution = await modificationService.ReplaceNodeAsync(document.Id, memberDecl, newMember, cancellationToken);
            Document formatted = await modificationService.FormatDocumentAsync(
                newSolution.GetDocument(document.Id)!, cancellationToken);
            await modificationService.ApplyChangesAsync(formatted.Project.Solution, cancellationToken, "Manage Attributes");

            // Return updated state to verify the change
            string diffResult = ContextInjectors.CreateCodeDiff(
                memberDecl.AttributeLists.ToFullString(),
                newMember.AttributeLists.ToFullString());

            if (diffResult.Trim() == "// No changes detected.")
            {
                return "Attribute update was successful but no difference was detected.";
            }

            return "Successfully updated attributes. Diff:\n" + diffResult;

        }, logger, nameof(ManageAttributes), cancellationToken);
    }

    private static readonly string[] sourceArray = ["method", "class", "project"];

    [McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(AnalyzeComplexity), Idempotent = true, ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Deep analysis of code complexity metrics including cyclomatic complexity, cognitive complexity, method stats, coupling, and inheritance depth. Scans methods, classes, or entire projects to identify maintenance risks and guide refactoring decisions.")]
    public static async Task<string> AnalyzeComplexity(
        ISolutionManager solutionManager,
        IComplexityAnalysisService complexityAnalysisService,
        ILogger<AnalysisToolsLogCategory> logger,
        [Description("The scope to analyze: 'method', 'class', or 'project'")] string scope,
        [Description("The fully qualified name of the method/class, or project name to analyze")] string target,
        CancellationToken cancellationToken)
    {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () =>
        {
            // Validate parameters
            ErrorHandlingHelpers.ValidateStringParameter(scope, nameof(scope), logger);
            ErrorHandlingHelpers.ValidateStringParameter(target, nameof(target), logger);

            if (sourceArray.Contains(scope.ToLower()) == false)
            {
                throw new McpException($"Invalid scope '{scope}'. Must be 'method', 'class', or 'project'.");
            }

            // Ensure solution is loaded
            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(AnalyzeComplexity));

            // Track metrics for the final report
            Dictionary<string, object> metrics = [];
            List<string> recommendations = [];

            switch (scope.ToLower())
            {
                case "method":
                    IMethodSymbol? methodSymbol = await ToolHelpers.GetRoslynSymbolOrThrowAsync(solutionManager, target, cancellationToken) as IMethodSymbol ?? throw new McpException($"Target '{target}' is not a method.");
                    await complexityAnalysisService.AnalyzeMethodAsync(methodSymbol, metrics, recommendations, cancellationToken);
                    break;

                case "class":
                    INamedTypeSymbol? typeSymbol = await ToolHelpers.GetRoslynSymbolOrThrowAsync(solutionManager, target, cancellationToken) as INamedTypeSymbol ?? throw new McpException($"Target '{target}' is not a class or interface.");
                    await complexityAnalysisService.AnalyzeTypeAsync(typeSymbol, metrics, recommendations, false, cancellationToken);
                    break;

                case "project":
                    Project? project = solutionManager.GetProjectByName(target) ?? throw new McpException($"Project '{target}' not found.");
                    await complexityAnalysisService.AnalyzeProjectAsync(project, metrics, recommendations, false, cancellationToken);
                    break;
            }

            // Format the results nicely
            return ToolHelpers.ToJson(new
            {
                scope,
                target,
                metrics,
                recommendations = recommendations.Distinct().OrderBy(r => r).ToList()
            });
        }, logger, nameof(AnalyzeComplexity), cancellationToken);
    }

    // Disabled for now, not super useful
    //[McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(FindPotentialDuplicates), Idempotent = true, Destructive = false, OpenWorld = false, ReadOnly = true)]
    [Description("Finds groups of semantically similar methods within the solution based on a similarity threshold.")]
    public static async Task<string> FindPotentialDuplicates(
        ISolutionManager solutionManager,
        ISemanticSimilarityService semanticSimilarityService,
        ILogger<AnalysisToolsLogCategory> logger,
        [Description("The minimum similarity score (0.0 to 1.0) for methods to be considered similar. (start with 0.75)")] double similarityThreshold,
        CancellationToken cancellationToken)
    {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () =>
        {
            ToolHelpers.EnsureSolutionLoadedWithDetails(solutionManager, logger, nameof(FindPotentialDuplicates));
            logger.LogInformation("Executing '{ToolName}' with threshold {Threshold}", nameof(FindPotentialDuplicates), similarityThreshold);

            if (similarityThreshold < 0.0 || similarityThreshold > 1.0)
            {
                throw new McpException("Similarity threshold must be between 0.0 and 1.0.");
            }

            TimeSpan timeout = TimeSpan.FromSeconds(30);
            CancellationTokenSource cancellationTokenSource = new(timeout);
            cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancellationTokenSource.Token).Token;
            List<MethodSimilarityResult> similarityResults = await semanticSimilarityService.FindSimilarMethodsAsync(similarityThreshold, cancellationToken);

            if (similarityResults.Count == 0)
            {
                return "No semantically similar method groups found with the given threshold.";
            }

            return ToolHelpers.ToJson(similarityResults);

        }, logger, nameof(FindPotentialDuplicates), cancellationToken);
    }

    [GeneratedRegex(@"\s*\bclass\b\s*")]
    private static partial Regex ClassRegex();
}
