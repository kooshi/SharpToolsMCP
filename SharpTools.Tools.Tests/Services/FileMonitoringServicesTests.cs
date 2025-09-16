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

}