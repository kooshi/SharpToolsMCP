using Microsoft.Build.Locator;
using System.Diagnostics;

namespace SharpTools.Tools.Services;

public static class MsBuildLocatorBootstrapper {
    private static readonly object SyncRoot = new();
    private static VisualStudioInstance? _registeredInstance;

    public static void EnsureRegistered(Action<string>? logInformation = null, Action<string>? logWarning = null) {
        lock (SyncRoot) {
            if (MSBuildLocator.IsRegistered) {
                logInformation?.Invoke(_registeredInstance == null
                    ? "MSBuildLocator is already registered."
                    : $"MSBuildLocator is already registered for '{Describe(_registeredInstance)}'.");
                return;
            }

            if (!MSBuildLocator.CanRegister) {
                logWarning?.Invoke("MSBuildLocator cannot register in the current process state.");
                return;
            }

            var instances = MSBuildLocator.QueryVisualStudioInstances()
                .OrderBy(instance => IsBuildTools(instance))
                .ThenByDescending(instance => instance.Version)
                .ThenBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (instances.Count > 0) {
                foreach (var instance in instances) {
                    logInformation?.Invoke($"Discovered MSBuild instance '{Describe(instance)}'.");
                }

                _registeredInstance = instances[0];
                MSBuildLocator.RegisterInstance(_registeredInstance);
                logInformation?.Invoke($"Registered MSBuild instance '{Describe(_registeredInstance)}'.");
                return;
            }

            var fallbackPath = FindPreferredMsBuildPath(logInformation);
            if (!string.IsNullOrEmpty(fallbackPath)) {
                MSBuildLocator.RegisterMSBuildPath(fallbackPath);
                logInformation?.Invoke($"Registered MSBuild path '{fallbackPath}'.");
                return;
            }

            try {
                _registeredInstance = MSBuildLocator.RegisterDefaults();
                logInformation?.Invoke($"Registered default MSBuild instance '{Describe(_registeredInstance)}'.");
            } catch (InvalidOperationException ex) {
                logWarning?.Invoke($"MSBuildLocator could not detect an MSBuild instance automatically: {ex.Message}");
            }
        }
    }

    private static bool IsBuildTools(VisualStudioInstance instance) =>
        instance.Name.Contains("Build Tools", StringComparison.OrdinalIgnoreCase);

    private static string Describe(VisualStudioInstance instance) =>
        $"{instance.Name} {instance.Version} at {instance.MSBuildPath}";

    private static string? FindPreferredMsBuildPath(Action<string>? logInformation) {
        var candidates = EnumerateVsInstallMsBuildPaths()
            .Concat(EnumeratePathMsBuildPaths())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => IsBuildToolsPath(path))
            .ThenByDescending(GetMsBuildVersionScore)
            .ThenBy(GetEditionRank)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var candidate in candidates) {
            logInformation?.Invoke($"Discovered MSBuild path '{candidate}'.");
        }

        return candidates.FirstOrDefault();
    }

    private static IEnumerable<string> EnumerateVsInstallMsBuildPaths() {
        foreach (var root in GetVisualStudioRoots()) {
            if (!Directory.Exists(root)) {
                continue;
            }

            foreach (var versionDirectory in Directory.EnumerateDirectories(root)) {
                foreach (var editionDirectory in Directory.EnumerateDirectories(versionDirectory)) {
                    var msbuildBinPath = Path.Combine(editionDirectory, "MSBuild", "Current", "Bin");
                    if (File.Exists(Path.Combine(msbuildBinPath, "MSBuild.exe"))) {
                        yield return msbuildBinPath;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumeratePathMsBuildPaths() {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue)) {
            yield break;
        }

        foreach (var pathEntry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (File.Exists(Path.Combine(pathEntry, "MSBuild.exe"))) {
                yield return pathEntry;
            }
        }
    }

    private static IEnumerable<string> GetVisualStudioRoots() {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles)) {
            yield return Path.Combine(programFiles, "Microsoft Visual Studio");
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86)) {
            yield return Path.Combine(programFilesX86, "Microsoft Visual Studio");
        }
    }

    private static bool IsBuildToolsPath(string path) =>
        path.Contains("BuildTools", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Build Tools", StringComparison.OrdinalIgnoreCase);

    private static long GetMsBuildVersionScore(string path) {
        try {
            var msbuildExePath = Path.Combine(path, "MSBuild.exe");
            var fileVersion = FileVersionInfo.GetVersionInfo(msbuildExePath);
            return ((long)fileVersion.FileMajorPart << 48)
                | ((long)fileVersion.FileMinorPart << 32)
                | ((long)fileVersion.FileBuildPart << 16)
                | (uint)fileVersion.FilePrivatePart;
        } catch {
            return 0;
        }
    }

    private static int GetEditionRank(string path) {
        if (path.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)) {
            return 0;
        }

        if (path.Contains("Professional", StringComparison.OrdinalIgnoreCase)) {
            return 1;
        }

        if (path.Contains("Community", StringComparison.OrdinalIgnoreCase)) {
            return 2;
        }

        if (IsBuildToolsPath(path)) {
            return 3;
        }

        return 4;
    }
}
