using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpTools.Tools.Interfaces;
using SharpTools.Tools.Services;
using System.Reflection;

namespace SharpTools.Tools.Extensions;

/// <summary>
/// Extension methods for IServiceCollection to register SharpTools services.
/// </summary>
public static class ServiceCollectionExtensions {
    /// <summary>
    /// Adds all SharpTools services to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection WithSharpToolsServices(this IServiceCollection services, bool enableGit = true, string? buildConfiguration = null) {
        services.AddSingleton<IFuzzyFqnLookupService, FuzzyFqnLookupService>();
        services.AddSingleton<ISolutionManager>(sp => 
            new SolutionManager(
                sp.GetRequiredService<ILogger<SolutionManager>>(), 
                sp.GetRequiredService<IFuzzyFqnLookupService>(),
                buildConfiguration
            )
        );
        services.AddSingleton<ICodeAnalysisService, CodeAnalysisService>();
        if (enableGit) {
            services.AddSingleton<IGitService, GitService>();
        } else {
            services.AddSingleton<IGitService, NoOpGitService>();
        }
        services.AddSingleton<ICodeModificationService, CodeModificationService>();
        services.AddSingleton<IEditorConfigProvider, EditorConfigProvider>();
        services.AddSingleton<IDocumentOperationsService, DocumentOperationsService>();
        services.AddSingleton<IComplexityAnalysisService, ComplexityAnalysisService>();
        services.AddSingleton<ISemanticSimilarityService, SemanticSimilarityService>();
        services.AddSingleton<ISourceResolutionService, SourceResolutionService>();

        return services;
    }

    /// <summary>
    /// Adds all SharpTools services and tools to the MCP service builder.
    /// </summary>
    /// <param name="builder">The MCP service builder.</param>
    /// <returns>The MCP service builder for chaining.</returns>
    public static IMcpServerBuilder WithSharpTools(this IMcpServerBuilder builder) {
        var toolAssembly = Assembly.Load("SharpTools.Tools");

        return builder
            .WithToolsFromAssembly(toolAssembly)
            .WithPromptsFromAssembly(toolAssembly);
    }
}