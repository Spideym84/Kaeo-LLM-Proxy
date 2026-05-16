using System.Diagnostics;

namespace Kaeo.LlmProxy.Core.Services;

/// <summary>
/// Periodically samples CPU and private-memory usage of the current process
/// and exposes them for the dashboard UI.
/// </summary>
internal sealed class PerformanceService : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly System.Threading.Timer _timer;

    private TimeSpan _lastCpuTime = TimeSpan.Zero;
    private DateTime _lastSampleTime = DateTime.UtcNow;

    /// <summary>CPU usage as a percentage (0–100) sampled over the last interval.</summary>
    public double CpuPercent { get; private set; }

    /// <summary>Private memory set of the current process in megabytes.</summary>
    public double MemoryMb { get; private set; }

    /// <summary>Raised on the thread-pool after each sample interval.</summary>
    public event EventHandler? Sampled;

    public PerformanceService(int intervalMs = 2000)
    {
        _timer = new System.Threading.Timer(Sample, null, intervalMs, intervalMs);
    }

    private void Sample(object? _)
    {
        try
        {
            _process.Refresh();

            DateTime now = DateTime.UtcNow;
            TimeSpan cpuNow = _process.TotalProcessorTime;

            double elapsed = (now - _lastSampleTime).TotalSeconds;
            if (elapsed > 0)
            {
                double cpuUsed = (cpuNow - _lastCpuTime).TotalSeconds;
                CpuPercent = Math.Min(100.0, cpuUsed / (elapsed * Environment.ProcessorCount) * 100.0);
            }

            _lastCpuTime = cpuNow;
            _lastSampleTime = now;

            MemoryMb = _process.PrivateMemorySize64 / (1024.0 * 1024.0);

            Sampled?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Non-fatal: sampling can fail if the process is exiting.
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _process.Dispose();
    }
}
