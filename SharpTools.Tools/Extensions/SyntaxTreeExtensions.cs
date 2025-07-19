
namespace SharpTools.Tools.Extensions;

public static class SyntaxTreeExtensions
{
    public static Project GetRequiredProject(this SyntaxTree tree, Solution solution)
    {
        var projectIds = solution.Projects
            .Where(p => p.Documents.Any(d => d.FilePath == tree.FilePath))
            .Select(p => p.Id)
            .ToList();

        if (projectIds.Count == 0)
            throw new InvalidOperationException($"Could not find project containing file {tree.FilePath}");
        
        if (projectIds.Count > 1)
            throw new InvalidOperationException($"File {tree.FilePath} belongs to multiple projects");
        
        var project = solution.GetProject(projectIds[0]);
        if (project == null)
            throw new InvalidOperationException($"Could not get project with ID {projectIds[0]}");
        
        return project;
    }
}