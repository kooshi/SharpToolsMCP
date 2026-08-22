using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SharpTools.Tools.Services;
using Xunit;

namespace SharpTools.Tools.Tests.Services;

// Each test gets a throwaway two-file solution on disk and a SolutionManager loaded against it.
public sealed class SolutionManagerDiskSyncTests : IAsyncLifetime {
    private static readonly string[] Preamble = ["namespace Probe;", ""];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sharptools-tests", Guid.NewGuid().ToString("N"));
    private readonly SolutionManager _manager = new(NullLogger<SolutionManager>.Instance, new FuzzyFqnLookupService(NullLogger<FuzzyFqnLookupService>.Instance));
    private string _a = null!;
    private string _b = null!;

    static SolutionManagerDiskSyncTests() {
        MsBuildLocatorBootstrapper.EnsureRegistered(_ => { }, _ => { });
    }

    public async Task InitializeAsync() {
        var lib = Path.Combine(_root, "Lib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(_root, "Probe.slnx"), "<Solution>\r\n  <Project Path=\"Lib/Lib.csproj\" />\r\n</Solution>\r\n");
        // A global.json further up the temp path can pin an older SDK; insist on one that can target net8.0.
        File.WriteAllText(Path.Combine(_root, "global.json"), "{ \"sdk\": { \"version\": \"10.0.100\", \"rollForward\": \"latestMajor\" } }");
        File.WriteAllText(Path.Combine(lib, "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n  <PropertyGroup>\r\n    <TargetFramework>net10.0</TargetFramework>\r\n  </PropertyGroup>\r\n</Project>\r\n");
        _a = Path.Combine(lib, "A.cs");
        _b = Path.Combine(lib, "B.cs");
        File.WriteAllText(_a, Source("public class A { public int One() => 1; }"));
        File.WriteAllText(_b, Source("public class B { public int Two() => 2; }"));
        Run("dotnet", "restore", _root);
        await _manager.LoadSolutionAsync(Path.Combine(_root, "Probe.slnx"), CancellationToken.None);
    }

    public Task DisposeAsync() {
        _manager.Dispose();
        try {
            Directory.Delete(_root, recursive: true);
        } catch (IOException) {
            // MSBuild occasionally keeps a handle a little longer; leaving temp files behind is harmless.
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task OutsideEdit_IsVisibleAfterSync() {
        Touch(_a, Source("public class A { public int One() => 11; }"));

        var sync = await _manager.SyncWithDiskAsync(CancellationToken.None);

        Assert.False(sync.Reloaded);
        Assert.Equal([_a], sync.RefreshedFiles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("=> 11", await TextOf(_a));
    }

    [Fact]
    public async Task SecondSync_ReportsNothing() {
        Touch(_a, Source("public class A { public int One() => 11; }"));
        await _manager.SyncWithDiskAsync(CancellationToken.None);

        var sync = await _manager.SyncWithDiskAsync(CancellationToken.None);

        Assert.False(sync.Any);
    }

    [Fact]
    public async Task OutsideEdit_SurvivesWriteToSameFile() {
        Touch(_a, Source("// outside", "public class A { public int One() => 1; }"));
        await _manager.SyncWithDiskAsync(CancellationToken.None);

        await Edit(_a, text => text.Replace("=> 1;", "=> 100;"));

        var onDisk = File.ReadAllText(_a);
        Assert.Contains("// outside", onDisk);
        Assert.Contains("=> 100;", onDisk);
    }

    [Fact]
    public async Task UntouchedStaleFile_IsNotRewritten() {
        Touch(_b, Source("// outside", "public class B { public int Two() => 2; }"));
        var before = File.GetLastWriteTimeUtc(_b);
        await _manager.SyncWithDiskAsync(CancellationToken.None);

        await Edit(_a, text => text.Replace("=> 1;", "=> 100;"));

        Assert.Equal(before, File.GetLastWriteTimeUtc(_b));
        Assert.Contains("// outside", File.ReadAllText(_b));
        Assert.Contains("// outside", await TextOf(_b));
        Assert.Contains("=> 100;", File.ReadAllText(_a));
    }

    [Fact]
    public async Task NewSourceFile_TriggersReload() {
        File.WriteAllText(Path.Combine(_root, "Lib", "C.cs"), Source("public class C { }"));

        var sync = await _manager.SyncWithDiskAsync(CancellationToken.None);

        Assert.True(sync.Reloaded);
        Assert.Contains("C.cs", sync.ReloadReason);
        Assert.Contains(_manager.CurrentSolution!.Projects.Single().Documents, d => d.Name == "C.cs");
    }

    [Fact]
    public async Task ProjectFileChange_TriggersReload() {
        var csproj = Path.Combine(_root, "Lib", "Lib.csproj");
        Touch(csproj, File.ReadAllText(csproj).Replace("</PropertyGroup>", "  <LangVersion>latest</LangVersion>\r\n  </PropertyGroup>"));

        var sync = await _manager.SyncWithDiskAsync(CancellationToken.None);

        Assert.True(sync.Reloaded);
        Assert.Contains("Lib.csproj", sync.ReloadReason);
    }

    [Fact]
    public async Task Write_DoesNotLookLikeAnOutsideEdit() {
        await Edit(_a, text => text.Replace("=> 1;", "=> 100;"));

        var sync = await _manager.SyncWithDiskAsync(CancellationToken.None);

        Assert.False(sync.Any);
    }

    [Fact]
    public async Task SequentialEdits_ToSameDocument_AreApplied() {
        await Edit(_a, text => text.Replace("=> 1;", "=> 100;"));
        await Edit(_a, text => text.Replace("=> 100;", "=> 300;"));
        await Edit(_a, text => text.Replace("public class A", "public sealed class A"));

        var onDisk = File.ReadAllText(_a);
        Assert.Contains("=> 300;", onDisk);
        Assert.Contains("public sealed class A", onDisk);
        Assert.False((await _manager.SyncWithDiskAsync(CancellationToken.None)).Any);
    }

    [Fact]
    public async Task StaleSolution_EditingADriftedDocument_IsRejected() {
        var stale = _manager.CurrentSolution!;
        await Edit(_a, text => text.Replace("=> 1;", "=> 100;"));

        var fromStale = stale.WithDocumentText(DocumentId(stale, _a), SourceText.From(Source("public class A { public int One() => 200; }")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.ApplyChangesAsync(fromStale, CancellationToken.None, baseSolution: stale));
        Assert.Contains("=> 100;", File.ReadAllText(_a));
    }

    [Fact]
    public async Task StaleSolution_EditingAnotherDocument_IsApplied() {
        var stale = _manager.CurrentSolution!;
        await Edit(_a, text => text.Replace("=> 1;", "=> 100;"));

        var fromStale = stale.WithDocumentText(DocumentId(stale, _b), SourceText.From(Source("public class B { public int Two() => 22; }")));
        await _manager.ApplyChangesAsync(fromStale, CancellationToken.None, baseSolution: stale);

        Assert.Contains("=> 22;", File.ReadAllText(_b));
        Assert.Contains("=> 100;", File.ReadAllText(_a));
        Assert.Contains("=> 100;", await TextOf(_a));
    }

    [Fact]
    public async Task StaleSolution_EditingAnOutsideEditedDocument_IsRejected() {
        var stale = _manager.CurrentSolution!;
        Touch(_a, Source("public class A { public int One() => 11; }"));
        await _manager.SyncWithDiskAsync(CancellationToken.None);

        var fromStale = stale.WithDocumentText(DocumentId(stale, _a), SourceText.From(Source("public class A { public int One() => 200; }")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.ApplyChangesAsync(fromStale, CancellationToken.None, baseSolution: stale));
        Assert.Contains("=> 11;", File.ReadAllText(_a));
    }

    private async Task Edit(string path, Func<string, string> change) {
        var solution = _manager.CurrentSolution!;
        var id = DocumentId(solution, path);
        var text = await solution.GetDocument(id)!.GetTextAsync();
        await _manager.ApplyChangesAsync(solution.WithDocumentText(id, SourceText.From(change(text.ToString()), text.Encoding)), CancellationToken.None, baseSolution: solution);
    }

    private async Task<string> TextOf(string path) =>
        (await _manager.CurrentSolution!.GetDocument(DocumentId(_manager.CurrentSolution!, path))!.GetTextAsync()).ToString();

    private static DocumentId DocumentId(Solution solution, string path) =>
        solution.GetDocumentIdsWithFilePath(path).Single();

    private static string Source(params string[] lines) => string.Join("\r\n", Preamble.Concat(lines)) + "\r\n";

    // Make sure the mtime actually moves even on coarse file systems.
    private static void Touch(string path, string content) {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
    }

    private static void Run(string file, string args, string workingDirectory) {
        var startInfo = new ProcessStartInfo(file, args) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        // MSBuildLocator pins this process to an SDK the test runtime can host (not necessarily a current one) via
        // MSBuild* environment variables; the child dotnet must resolve its own SDK from global.json instead.
        foreach (var key in startInfo.Environment.Keys.Where(k => k.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase) || k.Equals("DOTNET_HOST_PATH", StringComparison.OrdinalIgnoreCase)).ToList()) {
            startInfo.Environment.Remove(key);
        }
        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        if (process.ExitCode != 0) {
            throw new InvalidOperationException($"{file} {args} failed: {process.StandardError.ReadToEnd()}{process.StandardOutput.ReadToEnd()}");
        }
    }
}
