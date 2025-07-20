using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using SharpTools.Tools.Services;

namespace SharpTools.Tools.Mcp;

internal static class ToolHelpers {
    public const string SharpToolPrefix = "SharpTool_";

    public static void EnsureSolutionLoaded(ISolutionManager solutionManager) {
        if (!solutionManager.IsSolutionLoaded) {
            throw new McpException($"No solution is currently loaded. Please use '{SharpToolPrefix}{nameof(Tools.SolutionTools.LoadSolution)}' first.");
        }
    }

    /// <summary>
    /// Safely ensures that a solution is loaded, with detailed error information.
    /// </summary>
    public static void EnsureSolutionLoadedWithDetails(ISolutionManager solutionManager, ILogger logger, string operationName) {
        if (!solutionManager.IsSolutionLoaded) {
            logger.LogError("Attempted to execute {Operation} without a loaded solution", operationName);
            throw new McpException($"No solution is currently loaded. Please use '{SharpToolPrefix}{nameof(Tools.SolutionTools.LoadSolution)}' before calling '{operationName}'.");
        }
    }
    private const string FqnHelpMessage = $" Try `{ToolHelpers.SharpToolPrefix}{nameof(Tools.AnalysisTools.SearchDefinitions)}`, `{ToolHelpers.SharpToolPrefix}{nameof(Tools.AnalysisTools.GetMembers)}`, or `{ToolHelpers.SharpToolPrefix}{nameof(Tools.DocumentTools.ReadTypesFromRoslynDocument)}`  to find what you need.";
    public static async Task<ISymbol> GetRoslynSymbolOrThrowAsync(
        ISolutionManager solutionManager,
        string fullyQualifiedSymbolName,
        CancellationToken cancellationToken) {
        try {
            var symbol = await solutionManager.FindRoslynSymbolAsync(fullyQualifiedSymbolName, cancellationToken);
            return symbol ?? throw new McpException($"Roslyn symbol '{fullyQualifiedSymbolName}' not found in the current solution." + FqnHelpMessage);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) when (!(ex is McpException)) {
            throw new McpException($"Error finding Roslyn symbol '{fullyQualifiedSymbolName}': {ex.Message}");
        }
    }

    public static async Task<INamedTypeSymbol> GetRoslynNamedTypeSymbolOrThrowAsync(
        ISolutionManager solutionManager,
        string fullyQualifiedTypeName,
        CancellationToken cancellationToken) {
        try {
            var symbol = await solutionManager.FindRoslynNamedTypeSymbolAsync(fullyQualifiedTypeName, cancellationToken);
            return symbol ?? throw new McpException($"Roslyn named type symbol '{fullyQualifiedTypeName}' not found in the current solution." + FqnHelpMessage);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) when (!(ex is McpException)) {
            throw new McpException($"Error finding Roslyn named type symbol '{fullyQualifiedTypeName}': {ex.Message}");
        }
    }

    public static async Task<Type> GetReflectionTypeOrThrowAsync(
        ISolutionManager solutionManager,
        string fullyQualifiedTypeName,
        CancellationToken cancellationToken) {
        try {
            var type = await solutionManager.FindReflectionTypeAsync(fullyQualifiedTypeName, cancellationToken);
            return type ?? throw new McpException($"Reflection type '{fullyQualifiedTypeName}' not found in loaded assemblies." + FqnHelpMessage);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) when (!(ex is McpException)) {
            throw new McpException($"Error finding reflection type '{fullyQualifiedTypeName}': {ex.Message}");
        }
    }

    public static Document GetDocumentFromSyntaxNodeOrThrow(Solution solution, SyntaxNode node) {
        try {
            var document = solution.GetDocument(node.SyntaxTree);
            return document ?? throw new McpException("Could not find document for the given syntax node.");
        } catch (Exception ex) when (!(ex is McpException)) {
            throw new McpException($"Error finding document for syntax node: {ex.Message}");
        }
    }

    public static string ToJson(object? data) {
        return JsonSerializer.Serialize(data, new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }

    private static string RoslynAccessibilityToString(Accessibility accessibility) {
        return accessibility switch {
            Accessibility.Private => "private",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.Protected => "protected",
            Accessibility.Internal => "internal",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.Public => "public",
            _ => "" // NotApplicable or others
        };
    }

    public static string GetRoslynSymbolModifiersString(ISymbol symbol) {
        var parts = new List<string>();
        string accessibility = RoslynAccessibilityToString(symbol.DeclaredAccessibility);
        if (!string.IsNullOrEmpty(accessibility)) {
            parts.Add(accessibility);
        }

        if (symbol.IsStatic) parts.Add("static");
        if (symbol.IsAbstract && symbol.Kind != SymbolKind.NamedType) parts.Add("abstract"); // Type abstract handled by TypeKind
        if (symbol.IsSealed && symbol.Kind != SymbolKind.NamedType) parts.Add("sealed");     // Type sealed handled by TypeKind
        if (symbol.IsVirtual) parts.Add("virtual");
        if (symbol.IsOverride) parts.Add("override");
        if (symbol.IsExtern) parts.Add("extern");

        switch (symbol) {
            case IMethodSymbol methodSymbol:
                if (methodSymbol.IsAsync) parts.Add("async");
                break;
            case IFieldSymbol fieldSymbol:
                if (fieldSymbol.IsReadOnly) parts.Add("readonly");
                if (fieldSymbol.IsConst) parts.Add("const"); // Though 'const' implies static
                break;
            case IPropertySymbol propertySymbol:
                if (propertySymbol.IsReadOnly) parts.Add("readonly"); // Getter only, or init-only setter
                break;
            case INamedTypeSymbol typeSymbol: // For types, abstract/sealed are part of their kind
                if (typeSymbol.IsReadOnly) parts.Add("readonly"); // readonly struct/ref struct
                if (typeSymbol.IsRefLikeType) parts.Add("ref");
                // For static classes, IsStatic is true. For abstract/sealed, TypeKind reflects it.
                break;
        }
        return string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    public static string GetRoslynTypeSpecificModifiersString(INamedTypeSymbol typeSymbol) {
        var parts = new List<string>();
        string accessibility = RoslynAccessibilityToString(typeSymbol.DeclaredAccessibility);
        if (!string.IsNullOrEmpty(accessibility)) {
            parts.Add(accessibility);
        }

        if (typeSymbol.IsStatic) { // Covers static classes
            parts.Add("static");
        } else { // Abstract and Sealed are mutually exclusive with static class modifier
            if (typeSymbol.IsAbstract) parts.Add("abstract");
            if (typeSymbol.IsSealed) parts.Add("sealed");
        }
        if (typeSymbol.IsReadOnly) parts.Add("readonly"); // readonly struct/ref struct
        if (typeSymbol.IsRefLikeType) parts.Add("ref"); // ref struct

        return string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }


    private static string ReflectionAccessibilityToString(MethodBase? member) {
        if (member == null) return "";
        if (member.IsPublic) return "public";
        if (member.IsPrivate) return "private";
        if (member.IsFamilyAndAssembly) return "private protected";
        if (member.IsFamilyOrAssembly) return "protected internal";
        if (member.IsFamily) return "protected";
        if (member.IsAssembly) return "internal";
        return "";
    }

    private static string ReflectionAccessibilityToString(FieldInfo? member) {
        if (member == null) return "";
        if (member.IsPublic) return "public";
        if (member.IsPrivate) return "private";
        if (member.IsFamilyAndAssembly) return "private protected";
        if (member.IsFamilyOrAssembly) return "protected internal";
        if (member.IsFamily) return "protected";
        if (member.IsAssembly) return "internal";
        return "";
    }

    private static string ReflectionAccessibilityToString(Type type) {
        if (type.IsPublic || type.IsNestedPublic) return "public";
        if (type.IsNestedPrivate) return "private";
        if (type.IsNestedFamANDAssem) return "private protected";
        if (type.IsNestedFamORAssem) return "protected internal";
        if (type.IsNestedFamily) return "protected";
        if (type.IsNestedAssembly) return "internal";
        if (!type.IsNested) return "internal"; // Top-level non-public is internal by default
        return "";
    }

    public static string GetReflectionMemberModifiersString(MemberInfo memberInfo) {
        var parts = new List<string>();
        string accessibility = memberInfo switch {
            MethodBase mb => ReflectionAccessibilityToString(mb),
            FieldInfo fi => ReflectionAccessibilityToString(fi),
            PropertyInfo pi => ReflectionAccessibilityToString(pi.GetAccessors(true).FirstOrDefault()),
            EventInfo ei => ReflectionAccessibilityToString(ei.GetAddMethod(true)),
            Type ti => ReflectionAccessibilityToString(ti), // For nested types
            _ => ""
        };
        if (!string.IsNullOrEmpty(accessibility)) parts.Add(accessibility);

        switch (memberInfo) {
            case MethodInfo mi:
                if (mi.IsStatic) parts.Add("static");
                if (mi.IsAbstract) parts.Add("abstract");
                if (mi.IsVirtual && !mi.IsFinal && !mi.IsAbstract) parts.Add("virtual");
                if (mi.IsVirtual && mi.IsFinal) parts.Add("sealed override"); // Or just "sealed" if not overriding
                else {
                    // MetadataLoadContext doesn't support GetBaseDefinition()
                    try {
                        if (mi.GetBaseDefinition() != mi && !mi.IsVirtual) parts.Add("override"); // Non-virtual override (interface implementation)
                        else if (mi.GetBaseDefinition() != mi) parts.Add("override");
                    } catch (NotSupportedException) {
                        // For MetadataLoadContext, we can't check GetBaseDefinition
                        // Infer override status from best available information
                        if (mi.IsVirtual && !mi.IsAbstract) {
                            parts.Add("override");
                        }
                    }
                }
                try {
                    if (mi.IsDefined(typeof(AsyncStateMachineAttribute), false)) parts.Add("async");
                } catch (NotSupportedException) {
                    // MetadataLoadContext doesn't support IsDefined
                    // We can't check for async state machine attribute
                }
                if ((mi.MethodImplementationFlags & MethodImplAttributes.InternalCall) != 0 ||
                        (mi.MethodImplementationFlags & MethodImplAttributes.Native) != 0) parts.Add("extern");
                break;
            case ConstructorInfo ci: // Constructors have accessibility and can be static (type initializers)
                if (ci.IsStatic) parts.Add("static");
                break;
            case FieldInfo fi:
                if (fi.IsStatic && !fi.IsLiteral) parts.Add("static"); // const fields are implicitly static
                if (fi.IsInitOnly) parts.Add("readonly");
                if (fi.IsLiteral) parts.Add("const");
                break;
            case PropertyInfo pi:
                var accessor = pi.GetAccessors(true).FirstOrDefault();
                if (accessor != null) {
                    if (accessor.IsStatic) parts.Add("static");
                    if (accessor.IsAbstract) parts.Add("abstract");
                    if (accessor.IsVirtual && !accessor.IsFinal && !accessor.IsAbstract) parts.Add("virtual");
                    if (accessor.IsVirtual && accessor.IsFinal) parts.Add("sealed override");
                    else {
                        // MetadataLoadContext doesn't support GetBaseDefinition()
                        try {
                            if (accessor.GetBaseDefinition() != accessor && !accessor.IsVirtual) parts.Add("override");
                            else if (accessor.GetBaseDefinition() != accessor) parts.Add("override");
                        } catch (NotSupportedException) {
                            // For MetadataLoadContext, we can't check GetBaseDefinition
                            // Infer override status from best available information
                            if (accessor.IsVirtual && !accessor.IsAbstract) {
                                parts.Add("override");
                            }
                        }
                    }
                }
                if (!pi.CanWrite) parts.Add("readonly");
                break;
            case EventInfo ei:
                var addAccessor = ei.GetAddMethod(true);
                if (addAccessor != null) {
                    if (addAccessor.IsStatic) parts.Add("static");
                    if (addAccessor.IsAbstract) parts.Add("abstract");
                    if (addAccessor.IsVirtual && !addAccessor.IsFinal && !addAccessor.IsAbstract) parts.Add("virtual");
                    if (addAccessor.IsVirtual && addAccessor.IsFinal) parts.Add("sealed override");
                    else {
                        // MetadataLoadContext doesn't support GetBaseDefinition()
                        try {
                            if (addAccessor.GetBaseDefinition() != addAccessor && !addAccessor.IsVirtual) parts.Add("override");
                            else if (addAccessor.GetBaseDefinition() != addAccessor) parts.Add("override");
                        } catch (NotSupportedException) {
                            // For MetadataLoadContext, we can't check GetBaseDefinition
                            // Infer override status from best available information
                            if (addAccessor.IsVirtual && !addAccessor.IsAbstract) {
                                parts.Add("override");
                            }
                        }
                    }
                }
                break;
            case Type nestedType: // Modifiers for the nested type itself
                return GetReflectionTypeModifiersString(nestedType);
        }
        return string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p)).Distinct());
    }

    public static string GetReflectionTypeModifiersString(Type type) {
        var parts = new List<string>();
        string accessibility = ReflectionAccessibilityToString(type);
        if (!string.IsNullOrEmpty(accessibility)) parts.Add(accessibility);

        if (type.IsAbstract && type.IsSealed) { // Static class
            parts.Add("static");
        } else {
            if (type.IsAbstract) parts.Add("abstract");
            if (type.IsSealed) parts.Add("sealed");
        }
        if (type.IsValueType && type.IsDefined(typeof(IsReadOnlyAttribute), false)) parts.Add("readonly"); // readonly struct
        if (type.IsByRefLike) parts.Add("ref"); // ref struct

        return string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p)).Distinct());
    }


    public static string GetSymbolKindString(ISymbol symbol) {
        return symbol.Kind switch {
            SymbolKind.Namespace => "Namespace",
            SymbolKind.NamedType => ((INamedTypeSymbol)symbol).TypeKind.ToString(),
            SymbolKind.Method => "Method",
            SymbolKind.Property => "Property",
            SymbolKind.Event => "Event",
            SymbolKind.Field => "Field",
            SymbolKind.Parameter => "Parameter",
            SymbolKind.TypeParameter => "TypeParameter",
            SymbolKind.Local => "LocalVariable",
            _ => symbol.Kind.ToString()
        };
    }

    public static string GetReflectionTypeKindString(Type type) {
        if (type.IsEnum) return "Enum";
        if (type.IsInterface) return "Interface";
        if (type.IsValueType && !type.IsPrimitive && !type.IsEnum && !type.FullName!.StartsWith("System.Nullable")) return "Struct";
        if (type.IsClass) return "Class";
        if (typeof(Delegate).IsAssignableFrom(type)) return "Delegate";
        return type.IsValueType ? "ValueType" : "Type";
    }

    public static string GetReflectionMemberTypeKindString(MemberInfo memberInfo) {
        return memberInfo.MemberType switch {
            MemberTypes.Constructor => "Constructor",
            MemberTypes.Event => "Event",
            MemberTypes.Field => "Field",
            MemberTypes.Method => "Method",
            MemberTypes.Property => "Property",
            MemberTypes.TypeInfo or MemberTypes.NestedType when memberInfo is Type t => GetReflectionTypeKindString(t),
            _ => memberInfo.MemberType.ToString()
        };
    }

    /// <summary>
    /// Returns a SymbolDisplayFormat that produces fully qualified names without the global:: prefix
    /// </summary>
    public static SymbolDisplayFormat FullyQualifiedFormatWithoutGlobal => new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.OmittedAsContaining,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
        parameterOptions: SymbolDisplayParameterOptions.None,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>
    /// Removes global:: prefix from a fully qualified name
    /// </summary>
    public static string RemoveGlobalPrefix(string fullyQualifiedName) {
        if (string.IsNullOrEmpty(fullyQualifiedName)) {
            return fullyQualifiedName;
        }

        return fullyQualifiedName.StartsWith("global::", StringComparison.Ordinal)
            ? fullyQualifiedName.Substring(8)
            : fullyQualifiedName;
    }
    public static bool IsPropertyAccessor(ISymbol symbol) {
        if (symbol is IMethodSymbol methodSymbol) {
            var associatedSymbol = methodSymbol.AssociatedSymbol;
            return associatedSymbol is IPropertySymbol;  // True for both getters and setters
        }
        return false;
    }
    public static string TrimBackslash(this string str) {
        if (str.StartsWith("\\", StringComparison.Ordinal)) {
            return str[1..];
        }
        return str;
    }
    public static string NormalizeEndOfLines(this string str) {
        return str.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}