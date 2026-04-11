namespace SharpTools.Tools.Interfaces;

public interface IEditorConfigProvider
{
    Task InitializeAsync(string solutionDirectory, CancellationToken cancellationToken);
    string? GetRootEditorConfigPath();
    // OptionSet retrieval is primarily handled by Document.GetOptionsAsync(),
    // but this provider can offer workspace-wide defaults or specific lookups if needed.
}
