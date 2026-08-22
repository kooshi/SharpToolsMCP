namespace SharpTools.Tools.Services;

/// <summary>
/// Outcome of <see cref="ISolutionManager.SyncWithDiskAsync"/>: which documents were re-read because they changed on disk
/// outside SharpTools, or whether the whole solution had to be reloaded (files added/removed, project file changed).
/// </summary>
public sealed record DiskSyncResult(IReadOnlyList<string> RefreshedFiles, bool Reloaded, string? ReloadReason) {
    public static readonly DiskSyncResult None = new(Array.Empty<string>(), false, null);
    public bool Any => Reloaded || RefreshedFiles.Count > 0;
}
