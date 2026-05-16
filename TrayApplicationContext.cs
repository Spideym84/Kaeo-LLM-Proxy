using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Services;
using Kaeo.LlmProxy.Infrastructure;
using Serilog;

namespace Kaeo.LlmProxy;

/// <summary>
/// Manages the system tray icon, the proxy server lifetime, and the main form visibility.
/// The application runs entirely from the tray — no taskbar entry is shown.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly AppSettings _settings;
    private readonly StatisticsService _stats;
    private readonly PerformanceService _perfService;
    private readonly OllamaProxyHandler _handler;
    private readonly ProxyServer _server;
    private readonly RequestLogStore _logStore;
    private MainForm? _mainForm;

    public TrayApplicationContext()
    {
        _settings = AppSettings.Load();

        // Initialize Serilog first so all subsequent code can log.
        AppLogger.Initialize(_settings.Logging);
        Log.Information("Kaeo LLM Proxy starting. ListenAddress={Address} ListenPort={Port} MappingsCount={Count}",
            _settings.ListenAddress, _settings.ListenPort, _settings.ModelMappings.Count);

        _logStore = new RequestLogStore(_settings.Logging);
        _stats = new StatisticsService(_settings.MaxLogEntries, _logStore, _settings.Logging.LogRetentionHours);
        _perfService = new PerformanceService();
        _handler = new OllamaProxyHandler(_settings, _stats);
        _server = new ProxyServer(_handler);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Kaeo LLM Proxy",
            Visible = true,
            ContextMenuStrip = BuildContextMenu(),
        };

        _trayIcon.DoubleClick += OnTrayDoubleClick;
        _server.StatusChanged += OnServerStatusChanged;

        if (_settings.AutoStartProxy)
            StartProxy();

        if (_settings.StartWithDashboardOpen)
            ShowMainForm();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, OnOpenDashboard);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start Proxy", null, OnStartProxy);
        menu.Items.Add("Stop Proxy", null, OnStopProxy);
        menu.Items.Add("Restart Proxy", null, OnRestartProxy);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, OnExit);
        return menu;
    }

    private void StartProxy()
    {
        try
        {
            _server.Start(_settings.ListenPort, _settings.ListenAddress);
            _trayIcon.Text = $"Kaeo LLM Proxy — Listening {_settings.ListenAddress}:{_settings.ListenPort}";
            Log.Information("Proxy started on {Address}:{Port}", _settings.ListenAddress, _settings.ListenPort);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start proxy on {Address}:{Port}", _settings.ListenAddress, _settings.ListenPort);
            _trayIcon.Text = "Kaeo LLM Proxy — Error";
            MessageBox.Show($"Failed to start proxy: {ex.Message}", "Kaeo LLM Proxy",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnServerStatusChanged(object? sender, string status)
    {
        _trayIcon.Text = $"Kaeo LLM Proxy — {status}";
    }

    private void OnTrayDoubleClick(object? sender, EventArgs e) => ShowMainForm();

    private void OnOpenDashboard(object? sender, EventArgs e) => ShowMainForm();

    private void OnStartProxy(object? sender, EventArgs e)
    {
        if (!_server.IsRunning)
            StartProxy();
    }

    private async void OnStopProxy(object? sender, EventArgs e)
    {
        try
        {
            await _server.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error while stopping proxy");
            MessageBox.Show($"Error stopping proxy: {ex.Message}", "Kaeo LLM Proxy",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async void OnRestartProxy(object? sender, EventArgs e)
    {
        try
        {
            await _server.RestartAsync(_settings.ListenPort, _settings.ListenAddress);
            _trayIcon.Text = $"Kaeo LLM Proxy — Listening {_settings.ListenAddress}:{_settings.ListenPort}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restart proxy");
            MessageBox.Show($"Error restarting proxy: {ex.Message}", "Kaeo LLM Proxy",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowMainForm()
    {
        if (_mainForm is null || _mainForm.IsDisposed)
        {
            _mainForm = new MainForm(_settings, _stats, _server, _handler, _perfService);
            _mainForm.FormClosed += OnMainFormClosed;
        }

        _mainForm.Show();
        _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.Activate();
    }

    private void OnMainFormClosed(object? sender, FormClosedEventArgs e)
    {
        _mainForm = null;
    }

    private async void OnExit(object? sender, EventArgs e)
    {
        Log.Information("Kaeo LLM Proxy shutting down");
        _trayIcon.Visible = false;
        await _server.StopAsync();
        _trayIcon.Dispose();
        _server.Dispose();
        _logStore.Dispose();
        _perfService.Dispose();
        AppLogger.Shutdown();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
            _server.Dispose();
            _logStore.Dispose();
            _perfService.Dispose();
            AppLogger.Shutdown();
        }
        base.Dispose(disposing);
    }
}
