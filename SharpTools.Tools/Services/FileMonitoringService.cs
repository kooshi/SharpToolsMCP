using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpTools.Tools.Interfaces;

namespace SharpTools.Tools.Services
{
    public sealed class FileMonitoringService : IFileMonitoringService
    {
        private readonly ILogger<FileMonitoringService> _logger;
        private readonly object _lock = new();
        
        // _listener has its own lock. To prevent deadlock we never pick up _lock while _listener
        // might be locked. We may hold _lock while calling into _listener, which is necessary for
        // threadsafe cleanup.
        
        private FileChangeListener? _listener;
        private bool _disposed;
        
        /* ChangeCount is exported so tests can wait for events to come in */
        public int ChangeCount {
            get {
                var listener = ThreadSafeGetCurrentListener();
                
                return listener?.ChangeCount ?? -1;
            }
        }
        
        public FileMonitoringService(ILogger<FileMonitoringService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void StartMonitoring(string directory)
        {
            lock (_lock)
            {
                StopMonitoring();

                _logger.LogInformation("Starting file monitoring for directory: {Directory}", directory);
                _listener = new FileChangeListener(_logger, directory);
            }
        }

        public void StopMonitoring()
        {
            lock (_lock)
            {
                var listenerToDisable = _listener;
                _listener = null;
                
                // StopMonitoring is called from StartMonitoring within the lock so there is no point in doing
                // this cleanup outside the lock.
                if (listenerToDisable != null)
                {
                    listenerToDisable.Disable();
                    listenerToDisable.Dispose();
                }
            }
        }
        
        private FileChangeListener? ThreadSafeGetCurrentListener()
        {
            FileChangeListener? listener;
            lock (_lock) {
                listener = _listener;
            }
            return listener;
        }

        public void SetKnownFilePaths(ISet<string> filePathsToWatch) {
            FileChangeListener? listener = ThreadSafeGetCurrentListener();

            listener?.SetKnownFilePaths(filePathsToWatch);
        }

        public async Task<bool> AssessIfReloadNecessary()
        {
            FileChangeListener? listener = ThreadSafeGetCurrentListener();

            if (listener is null)
                return true;

            return await listener.AssessIfReloadNecessary();
        }

        public void RegisterExpectedChange(string filePath, string fileContents)
        {
            FileChangeListener? listener = ThreadSafeGetCurrentListener();
            
            listener?.RegisterExpectedChange(filePath, fileContents);
        }

        public void Dispose()
        {
            if (_disposed) return;

            StopMonitoring();
            _disposed = true;
        }
    }
}