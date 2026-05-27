using System.Net;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Services;

namespace Kaeo.LlmProxy.Infrastructure;

/// <summary>
/// HTTP listener that accepts incoming Ollama-compatible requests and dispatches
/// them to <see cref="OllamaProxyHandler"/>.
/// </summary>
internal sealed class ProxyServer(OllamaProxyHandler handler) : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private readonly OllamaProxyHandler _handler = handler;
    private bool _disposed;

    public bool IsRunning { get; private set; }

    public event EventHandler<string>? StatusChanged;

    public void Start(int port, string listenAddress = "localhost")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
            return;

        _listener = new HttpListener();

        // Configure timeouts for long-running AI requests (e.g., extended thinking)
        // These need to be set BEFORE calling Start()
        _listener.TimeoutManager.IdleConnection = TimeSpan.FromMinutes(30);
        _listener.TimeoutManager.HeaderWait = TimeSpan.FromMinutes(5);
        _listener.TimeoutManager.EntityBody = TimeSpan.FromMinutes(30);
        _listener.TimeoutManager.DrainEntityBody = TimeSpan.FromMinutes(5);
        _listener.TimeoutManager.RequestQueue = TimeSpan.FromMinutes(5);

        // Normalize the listen address
        string host = listenAddress.Trim();

        // Handle special cases
        if (string.IsNullOrWhiteSpace(host))
            host = "localhost";

        // Convert "0.0.0.0" to "+" for HttpListener (which means all interfaces)
        if (host == "0.0.0.0")
            host = "+";

        // Build prefix - using "+" or specific IPs may require admin or netsh urlacl reservation
        string prefix = $"http://{host}:{port}/";

        _listener.Prefixes.Add(prefix);
        _listener.Start();

        _cts = new CancellationTokenSource();
        _listenTask = AcceptLoopAsync(_cts.Token);
        IsRunning = true;

        // Display friendly address for status
        string displayHost = host == "+" ? "0.0.0.0" : host;
        StatusChanged?.Invoke(this, $"Listening on {displayHost}:{port}");
    }

    public async Task RestartAsync(int port, string listenAddress = "localhost")
    {
        await StopAsync().ConfigureAwait(false);
        Start(port, listenAddress);
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
            return;

        IsRunning = false;

        CancellationTokenSource? cts = _cts;
        HttpListener? listener = _listener;
        Task? listenTask = _listenTask;

        cts?.Cancel();

        listener?.Stop();
        listener?.Close();

        if (listenTask is not null)
        {
            try { await listenTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        cts?.Dispose();
        _cts = null;
        _listener = null;
        _listenTask = null;

        StatusChanged?.Invoke(this, "Stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener!.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            // Fire-and-forget each request on the thread pool — we track errors inside the handler.
            _ = Task.Run(() => _handler.HandleAsync(context, ct), ct);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        IsRunning = false;

        CancellationTokenSource? cts = _cts;
        _cts = null;
        _listenTask = null;

        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
        cts?.Dispose();

        _listener?.Close();
        _listener = null;
    }
}
