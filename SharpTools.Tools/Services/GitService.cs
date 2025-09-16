using LibGit2Sharp;

namespace SharpTools.Tools.Services;

public class GitService : IGitService {
    private readonly ILogger<GitService> _logger;
    private const string SharpToolsBranchPrefix = "sharptools/";
    private const string SharpToolsUndoBranchPrefix = "sharptools/undo/";

    public GitService(ILogger<GitService> logger) {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> IsRepositoryAsync(string solutionPath, CancellationToken cancellationToken = default) {
        return await Task.Run(() => {
            try {
                var solutionDirectory = Path.GetDirectoryName(solutionPath);
                if (string.IsNullOrEmpty(solutionDirectory)) {
                    return false;
                }

                var repositoryPath = Repository.Discover(solutionDirectory);
                return !string.IsNullOrEmpty(repositoryPath);
            } catch (Exception ex) {
                _logger.LogDebug("Error checking if path is a Git repository: {Error}", ex.Message);
                return false;
            }
        }, cancellationToken);
    }

    public async Task<bool> IsOnSharpToolsBranchAsync(string solutionPath, CancellationToken cancellationToken = default) {
        return await Task.Run(() => {
            try {
                var repositoryPath = GetRepositoryPath(solutionPath);
                if (repositoryPath == null) {
                    return false;
                }

                using var repository = new Repository(repositoryPath);
                var currentBranch = repository.Head.FriendlyName;
                var isOnSharpToolsBranch = currentBranch.StartsWith(SharpToolsBranchPrefix, StringComparison.OrdinalIgnoreCase);

                _logger.LogDebug("Current branch: {BranchName}, IsSharpToolsBranch: {IsSharpToolsBranch}",
                    currentBranch, isOnSharpToolsBranch);

                return isOnSharpToolsBranch;
            } catch (Exception ex) {
                _logger.LogWarning("Error checking current branch: {Error}", ex.Message);
                return false;
            }
        }, cancellationToken);
    }

    public async Task EnsureSharpToolsBranchAsync(string solutionPath, CancellationToken cancellationToken = default) {
        await Task.Run(() => {
            try {
                var repositoryPath = GetRepositoryPath(solutionPath);
                if (repositoryPath == null) {
                    _logger.LogWarning("No Git repository found for solution at {SolutionPath}", solutionPath);
                    return;
                }

                using var repository = new Repository(repositoryPath);
                var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd/HH-mm-ss");
                var branchName = $"{SharpToolsBranchPrefix}{timestamp}";

                // Check if we're already on a sharptools branch
                var currentBranch = repository.Head.FriendlyName;
                if (currentBranch.StartsWith(SharpToolsBranchPrefix, StringComparison.OrdinalIgnoreCase)) {
                    _logger.LogDebug("Already on SharpTools branch: {BranchName}", currentBranch);
                    return;
                }

                // Create and checkout the new branch
                var newBranch = repository.CreateBranch(branchName);
                Commands.Checkout(repository, newBranch);

                _logger.LogInformation("Created and switched to SharpTools branch: {BranchName}", branchName);
            } catch (Exception ex) {
                _logger.LogError(ex, "Error ensuring SharpTools branch for solution at {SolutionPath}", solutionPath);
                throw;
            }
        }, cancellationToken);
    }

    public async Task CommitChangesAsync(string solutionPath, IEnumerable<string> changedFilePaths,
        string commitMessage, CancellationToken cancellationToken = default) {
        await Task.Run(() => {
            try {
                var repositoryPath = GetRepositoryPath(solutionPath);
                if (repositoryPath == null) {
                    _logger.LogWarning("No Git repository found for solution at {SolutionPath}", solutionPath);
                    return;
                }

                using var repository = new Repository(repositoryPath);

                // Stage the changed files
                var stagedFiles = new List<string>();
                foreach (var filePath in changedFilePaths) {
                    try {
                        // Convert absolute path to relative path from repository root
                        var relativePath = Path.GetRelativePath(repository.Info.WorkingDirectory, filePath);

                        // Stage the file
                        Commands.Stage(repository, relativePath);
                        stagedFiles.Add(relativePath);

                        _logger.LogDebug("Staged file: {FilePath}", relativePath);
                    } catch (Exception ex) {
                        _logger.LogWarning("Failed to stage file {FilePath}: {Error}", filePath, ex.Message);
                    }
                }

                if (stagedFiles.Count == 0) {
                    _logger.LogWarning("No files were staged for commit");
                    return;
                }

                // Create commit
                var signature = GetCommitSignature(repository);
                var commit = repository.Commit(commitMessage, signature, signature);

                _logger.LogInformation("Created commit {CommitSha} with {FileCount} files: {CommitMessage}",
                    commit.Sha[..8], stagedFiles.Count, commitMessage);
            } catch (Exception ex) {
                _logger.LogError(ex, "Error committing changes for solution at {SolutionPath}", solutionPath);
                throw;
            }
        }, cancellationToken);
    }

    private string? GetRepositoryPath(string solutionPath) {
        var solutionDirectory = Path.GetDirectoryName(solutionPath);
        return string.IsNullOrEmpty(solutionDirectory) ? null : Repository.Discover(solutionDirectory);
    }

    private Signature GetCommitSignature(Repository repository) {
        try {
            // Try to get user info from Git config
            var config = repository.Config;
            var name = config.Get<string>("user.name")?.Value ?? "SharpTools";
            var email = config.Get<string>("user.email")?.Value ?? "sharptools@localhost";

            return new Signature(name, email, DateTimeOffset.Now);
        } catch {
            // Fallback to default signature
            return new Signature("SharpTools", "sharptools@localhost", DateTimeOffset.Now);
        }
    }

    public async Task<string> CreateUndoBranchAsync(string solutionPath, CancellationToken cancellationToken = default) {
        return await Task.Run(() => {
            try {
                var repositoryPath = GetRepositoryPath(solutionPath);
                if (repositoryPath == null) {
                    _logger.LogWarning("No Git repository found for solution at {SolutionPath}", solutionPath);
                    return string.Empty;
                }

                using var repository = new Repository(repositoryPath);
                var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd/HH-mm-ss");
                var branchName = $"{SharpToolsUndoBranchPrefix}{timestamp}";

                // Create a new branch at the current commit, but don't checkout
                var currentCommit = repository.Head.Tip;
                var newBranch = repository.CreateBranch(branchName, currentCommit);

                _logger.LogInformation("Created undo branch: {BranchName} at commit {CommitSha}",
                    branchName, currentCommit.Sha[..8]);

                return branchName;
            } catch (Exception ex) {
                _logger.LogError(ex, "Error creating undo branch for solution at {SolutionPath}", solutionPath);
                return string.Empty;
            }
        }, cancellationToken);
    }

    public async Task<string> GetDiffAsync(string solutionPath, string oldCommitSha, string newCommitSha, CancellationToken cancellationToken = default) {
        return await Task.Run(() => {
            try {
                var repositoryPath = GetRepositoryPath(solutionPath);
                if (repositoryPath == null) {
                    _logger.LogWarning("No Git repository found for solution at {SolutionPath}", solutionPath);
                    return string.Empty;
                }

                using var repository = new Repository(repositoryPath);
                var oldCommit = repository.Lookup<Commit>(oldCommitSha);
                var newCommit = repository.Lookup<Commit>(newCommitSha);

                if (oldCommit == null || newCommit == null) {
                    _logger.LogWarning("Could not find commits for diff: Old {OldSha}, New {NewSha}",
                        oldCommitSha?[..8] ?? "null", newCommitSha?[..8] ?? "null");
                    return string.Empty;
                }

                // Get the changes between the two commits
                var diffOutput = new StringBuilder();
                diffOutput.AppendLine($"Changes between {oldCommitSha[..8]} and {newCommitSha[..8]}:");
                diffOutput.AppendLine();

                // Compare the trees
                var comparison = repository.Diff.Compare<TreeChanges>(oldCommit.Tree, newCommit.Tree);

                foreach (var change in comparison) {
                    diffOutput.AppendLine($"{change.Status}: {change.Path}");

                    // Get detailed patch for each file
                    var patch = repository.Diff.Compare<Patch>(
                        oldCommit.Tree,
                        newCommit.Tree,
                        new[] { change.Path },
                        new CompareOptions { ContextLines = 0 });

                    diffOutput.AppendLine(patch);
                }

                return diffOutput.ToString();
            } catch (Exception ex) {
                _logger.LogError(ex, "Error getting diff for solution at {SolutionPath}", solutionPath);
                return $"Error generating diff: {ex.Message}";
            }
        }, cancellationToken);
    }

    public async Task<(bool success, string diff)> RevertLastCommitAsync(string solutionPath, CancellationToken cancellationToken = default) {
        return await Task.Run(async () => {
            try {
                var repositoryPath = GetRepositoryPath(solutionPath);
                if (repositoryPath == null) {
                    _logger.LogWarning("No Git repository found for solution at {SolutionPath}", solutionPath);
                    return (false, string.Empty);
                }

                using var repository = new Repository(repositoryPath);
                var currentBranch = repository.Head.FriendlyName;

                // Ensure we're on a sharptools branch
                if (!currentBranch.StartsWith(SharpToolsBranchPrefix, StringComparison.OrdinalIgnoreCase)) {
                    _logger.LogWarning("Not on a SharpTools branch, cannot revert. Current branch: {BranchName}", currentBranch);
                    return (false, string.Empty);
                }

                var currentCommit = repository.Head.Tip;
                if (currentCommit?.Parents?.Any() != true) {
                    _logger.LogWarning("Current commit has no parent, cannot revert");
                    return (false, string.Empty);
                }

                var parentCommit = currentCommit.Parents.First();
                _logger.LogInformation("Reverting from commit {CurrentSha} to parent {ParentSha}",
                    currentCommit.Sha[..8], parentCommit.Sha[..8]);

                // First, create an undo branch at the current commit
                var undoBranchName = await CreateUndoBranchAsync(solutionPath, cancellationToken);
                if (string.IsNullOrEmpty(undoBranchName)) {
                    _logger.LogWarning("Failed to create undo branch");
                }

                // Get the diff before we reset
                var diff = await GetDiffAsync(solutionPath, parentCommit.Sha, currentCommit.Sha, cancellationToken);

                // Reset to the parent commit (hard reset)
                repository.Reset(ResetMode.Hard, parentCommit);

                _logger.LogInformation("Successfully reverted to commit {CommitSha}", parentCommit.Sha[..8]);

                var resultMessage = !string.IsNullOrEmpty(undoBranchName)
                    ? $"The changes have been preserved in branch '{undoBranchName}' for future reference."
                    : string.Empty;

                return (true, diff + "\n\n" + resultMessage);
            } catch (Exception ex) {
                _logger.LogError(ex, "Error reverting last commit for solution at {SolutionPath}", solutionPath);
                return (false, $"Error: {ex.Message}");
            }
        }, cancellationToken);
    }

    public async Task<string> GetBranchOriginCommitAsync(string solutionPath, CancellationToken cancellationToken = default) {
        return await Task.Run(() => {
            try {
                var repositoryPath = GetRepositoryPath(solutionPath);
                if (repositoryPath == null) {
                    _logger.LogWarning("No Git repository found for solution at {SolutionPath}", solutionPath);
                    return string.Empty;
                }

                using var repository = new Repository(repositoryPath);
                var currentBranch = repository.Head.FriendlyName;

                // Ensure we're on a sharptools branch
                if (!currentBranch.StartsWith(SharpToolsBranchPrefix, StringComparison.OrdinalIgnoreCase)) {
                    _logger.LogDebug("Not on a SharpTools branch, returning empty. Current branch: {BranchName}", currentBranch);
                    return string.Empty;
                }

                // Find the commit where this branch diverged from its parent
                // We'll traverse the commit history to find where the sharptools branch was created
                var commit = repository.Head.Tip;
                var branchCreationCommit = commit;

                // Walk back through the commits to find the first commit on this branch
                while (commit?.Parents?.Any() == true) {
                    var parent = commit.Parents.First();

                    // If this is the first commit that mentions sharptools in the branch,
                    // the parent is likely our origin point
                    if (commit.MessageShort.Contains("SharpTools", StringComparison.OrdinalIgnoreCase) ||
                        commit.MessageShort.Contains("branch", StringComparison.OrdinalIgnoreCase)) {
                        branchCreationCommit = parent;
                        break;
                    }

                    commit = parent;
                }

                _logger.LogDebug("Branch origin commit found: {CommitSha}", branchCreationCommit.Sha[..8]);
                return branchCreationCommit.Sha;
            } catch (Exception ex) {
                _logger.LogError(ex, "Error finding branch origin commit for solution at {SolutionPath}", solutionPath);
                return string.Empty;
            }
        }, cancellationToken);
    }
}