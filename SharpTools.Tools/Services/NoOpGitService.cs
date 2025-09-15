
using SharpTools.Tools.Interfaces;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SharpTools.Tools.Services;

public class NoOpGitService : IGitService
{
    public Task<bool> IsRepositoryAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    public Task<bool> IsOnSharpToolsBranchAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    public Task EnsureSharpToolsBranchAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task CommitChangesAsync(string solutionPath, IEnumerable<string> changedFilePaths, string commitMessage, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<(bool success, string diff)> RevertLastCommitAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        return Task.FromResult((false, string.Empty));
    }

    public Task<string> GetBranchOriginCommitAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(string.Empty);
    }

    public Task<string> CreateUndoBranchAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(string.Empty);
    }

    public Task<string> GetDiffAsync(string solutionPath, string oldCommitSha, string newCommitSha, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(string.Empty);
    }
}
