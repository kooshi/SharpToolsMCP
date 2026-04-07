using Microsoft.Extensions.Logging;
using SharpTools.Tools.Interfaces;
using SharpTools.Tools.Services;
using Xunit;

namespace SharpTools.Tools.Tests.Services;

public class FileMonitoringServicesTests : IDisposable
{
    private readonly ILogger<FileMonitoringService> _logger;
    private readonly string _testDirectory;
    private readonly FileMonitoringService _service;

    public FileMonitoringServicesTests()
    {
        _logger = new LoggerFactory().CreateLogger<FileMonitoringService>();
        _testDirectory = Path.Combine(Path.GetTempPath(), "FileMonitoringTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        _service = new FileMonitoringService(_logger);
    }

    public void Dispose()
    {
        _service?.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [Fact]
    public async Task AssessIfReloadNecessary_NoChanges_ReturnsFalse()
    {
        // Arrange
        _service.StartMonitoring(_testDirectory);
        _service.SetKnownFilePaths(new HashSet<string>());

        // Act
        var result = await _service.AssessIfReloadNecessary();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task AssessIfReloadNecessary_FileChangedBeforeSetKnownFilePaths_ReturnsTrue()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.txt");
        File.WriteAllText(filePath, "initial content");
        
        _service.StartMonitoring(_testDirectory);
        
        
        File.WriteAllText(filePath, "changed content");
        var knownFilePaths = new HashSet<string> { filePath };

        // Act
        _service.SetKnownFilePaths(knownFilePaths);

        await Retry.UntilPasses(() => Assert.True( _service.ChangeCount >= 1));
        
        var result = await _service.AssessIfReloadNecessary();

        // Assert
        Assert.True(result);
    }
    
    
    [Fact]
    public async Task AssessIfReloadNecessary_FileDeletedBeforeSetKnownFilePaths_ReturnsTrue()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.txt");
        File.WriteAllText(filePath, "initial content");
        
        _service.StartMonitoring(_testDirectory);
        
        File.Delete(filePath);
        
        var knownFilePaths = new HashSet<string> { filePath };

        // Act
        _service.SetKnownFilePaths(knownFilePaths);
        
        await Retry.UntilPasses(() => Assert.True( _service.ChangeCount >= 1));
        
        var result = await _service.AssessIfReloadNecessary();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task AssessIfReloadNecessary_KnownFileChangedAfterSetKnownFilePaths_ReturnsTrue()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.txt");
        
        File.WriteAllText(filePath, "initial content");
        
        var knownFilePaths = new HashSet<string> { filePath };
        
        _service.StartMonitoring(_testDirectory);
        _service.SetKnownFilePaths(knownFilePaths);
        
        Assert.Equal(0, _service.ChangeCount);

        // Act
        File.WriteAllText(filePath, "changed content");
        
        await Retry.UntilPasses(() => Assert.True(_service.ChangeCount >= 1));
        
        var result = await _service.AssessIfReloadNecessary();

        // Assert
        Assert.True(result);
    }
    
    [Fact]
    public async Task AssessIfReloadNecessary_KnownFileDeletedAfterSetKnownFilePaths_ReturnsTrue()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.txt");
        File.WriteAllText(filePath, "initial content");
        var knownFilePaths = new HashSet<string> { filePath };
        
        _service.StartMonitoring(_testDirectory);
        _service.SetKnownFilePaths(knownFilePaths);

        // Act
        File.Delete(filePath);
        
        await Retry.UntilPasses(() => Assert.True(_service.ChangeCount >= 1));
        
        var result = await _service.AssessIfReloadNecessary();

        // Assert
        Assert.True(result);
    }
    
    [Fact]
    public async Task AssessIfReloadNecessary_KnownFileDirectoryDeletedAfterSetKnownFilePaths_ReturnsTrue()
    {
        // Arrange
        var subdirectory = Path.Combine(_testDirectory, "bar");
        Directory.CreateDirectory(subdirectory);
        var filePath = Path.Combine(subdirectory, "test.txt");
        File.WriteAllText(filePath, "initial content");
        
        var knownFilePaths = new HashSet<string> { filePath };
        _service.StartMonitoring(_testDirectory);
        _service.SetKnownFilePaths(knownFilePaths);

        // Act
        Directory.Delete(subdirectory,  true);
        
        Assert.False(File.Exists(filePath));
        
        // Wait for the file watcher to pick up the change
        await Retry.UntilPasses(() => Assert.True(_service.ChangeCount >= 1));
        
        var result = await _service.AssessIfReloadNecessary();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task AssessIfReloadNecessary_KnownFileDirectoryChangedAfterSetKnownFilePaths_ReturnsTrue()
    {
        // Renaming the directory is effectively the same as deleting the file from the monitor's perspective.
        // Rather than look for directory rename events, the AssessIfReloadNecessary method will simply check
        // if any relevant files are missing.
        
        // Arrange
        var subdirectory = Path.Combine(_testDirectory, "bar");
        Directory.CreateDirectory(subdirectory);
        var filePath = Path.Combine(subdirectory, "test.txt");
        File.WriteAllText(filePath, "initial content");
        
        var knownFilePaths = new HashSet<string> { filePath };
        _service.StartMonitoring(_testDirectory);
        _service.SetKnownFilePaths(knownFilePaths);

        // Act
        var newSubDirectory = Path.Combine(_testDirectory, "baz");
        Directory.Move(subdirectory, newSubDirectory);
        
        Assert.True(File.Exists(Path.Combine(newSubDirectory, "test.txt")));
        
        // Wait for the file watcher to pick up the change
        await Retry.UntilPasses(() => Assert.True(_service.ChangeCount >= 1));
        
        var result = await _service.AssessIfReloadNecessary();

        // Assert
        Assert.True(result);
    }
    
    [Fact]
    public async Task AssessIfReloadNecessary_ExpectedChange_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.txt");
        File.WriteAllText(filePath, "initial content");
        var knownFilePaths = new HashSet<string> { filePath };
        _service.StartMonitoring(_testDirectory);
        _service.SetKnownFilePaths(knownFilePaths);
        var newContent = "changed content";
        _service.RegisterExpectedChange(filePath, newContent);

        // Act
        File.WriteAllText(filePath, newContent);
        
        // Wait for the file watcher to pick up the change
        await Retry.UntilPasses(() => Assert.True(_service.ChangeCount >= 1));
        
        var result = await _service.AssessIfReloadNecessary();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task AssessIfReloadNecessary_ExpectedChangeContentMismatch_ReturnsTrue()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.txt");
        File.WriteAllText(filePath, "initial content");
        var knownFilePaths = new HashSet<string> { filePath };
        _service.StartMonitoring(_testDirectory);
        _service.SetKnownFilePaths(knownFilePaths);
        _service.RegisterExpectedChange(filePath, "expected content");

        // Act
        File.WriteAllText(filePath, "unexpected content");
        
        // Wait for the file watcher to pick up the change
        await Retry.UntilPasses(() => Assert.True(_service.ChangeCount >= 1));
        
        var result = await _service.AssessIfReloadNecessary();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task AssessIfReloadNecessary_UnknownFileChanged_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.txt");
        File.WriteAllText(filePath, "initial content");
        _service.StartMonitoring(_testDirectory);
        _service.SetKnownFilePaths(new HashSet<string>());

        // Act
        File.WriteAllText(filePath, "changed content");
        
        // Wait for the file watcher to pick up the change
        await Retry.UntilPasses(() => Assert.True(_service.ChangeCount >= 1));
        
        var result = await _service.AssessIfReloadNecessary();

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData(".git")]
    public async Task AssessIfReloadNecessary_IgnoresChangesInIgnoredDirectories_ReturnsFalse(string ignoredDir)
    {
        // Arrange
        var dir = Path.Combine(_testDirectory, ignoredDir);
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "test.txt");
        File.WriteAllText(filePath, "initial content");

        var knownFilePaths = new HashSet<string> { filePath };
        _service.StartMonitoring(_testDirectory);
        _service.SetKnownFilePaths(knownFilePaths);

        // Act
        File.WriteAllText(filePath, "changed content");
        
        // Wait for the file watcher to pick up the change
        await Retry.UntilPasses(() => Assert.True(_service.ChangeCount >= 1));
        
        var result = await _service.AssessIfReloadNecessary();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task Reload_is_considered_necessary_if_monitoring_is_not_running()
    {
        // Reload is necessary if monitoring hasn't even been started.
        Assert.True(await _service.AssessIfReloadNecessary());
        
        
        _service.StartMonitoring(_testDirectory);

        // Verify starting monitoring changes the value we're testing
        Assert.False(await _service.AssessIfReloadNecessary());
        
        _service.StopMonitoring();

        // Reload is necessary if monitoring has been stopped.
        Assert.True(await _service.AssessIfReloadNecessary());
    }
}