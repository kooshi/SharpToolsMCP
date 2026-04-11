namespace SharpTools.Tools.Interfaces;

public interface IGitService
{
    Task<bool> IsRepositoryAsync(string solutionPath, CancellationToken cancellationToken = default);
    Task<bool> IsOnSharpToolsBranchAsync(string solutionPath, CancellationToken cancellationToken = default);
    Task EnsureSharpToolsBranchAsync(string solutionPath, CancellationToken cancellationToken = default);
    Task CommitChangesAsync(
        string solutionPath,
        IEnumerable<string> changedFilePaths,
        string commitMessage,
        CancellationToken cancellationToken = default);
    Task<(bool success, string diff)> RevertLastCommitAsync(
        string solutionPath,
        CancellationToken cancellationToken = default);
    Task<string> GetBranchOriginCommitAsync(string solutionPath, CancellationToken cancellationToken = default);
    Task<string> CreateUndoBranchAsync(string solutionPath, CancellationToken cancellationToken = default);
    Task<string> GetDiffAsync(
        string solutionPath,
        string oldCommitSha,
        string newCommitSha,
        CancellationToken cancellationToken = default);
}
