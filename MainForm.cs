using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Services;
using Kaeo.LlmProxy.Infrastructure;

namespace Kaeo.LlmProxy;

internal partial class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly StatisticsService _stats;
    private readonly ProxyServer _server;
    private readonly OllamaProxyHandler _handler;
    private readonly PerformanceService _perfService;

    internal event EventHandler? MinimizedToTray;

    private static readonly JsonSerializerOptions _indentedJsonOptions = new() { WriteIndented = true };

    public MainForm(AppSettings settings, StatisticsService stats, ProxyServer server, OllamaProxyHandler handler, PerformanceService perfService)
    {
        _settings = settings;
        _stats = stats;
        _server = server;
        _handler = handler;
        _perfService = perfService;

        InitializeComponent();
        Icon = Program.GetApplicationIcon();

        _stats.StatsChanged += OnStatsChanged;
        _server.StatusChanged += OnServerStatusChanged;
        _perfService.Sampled += OnPerfSampled;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        LoadSettingsToForm();
        RefreshStatus();
        RefreshStats();
        RefreshLogs();
        _cmbRefreshInterval.SelectedIndex = 1; // default: 2 s
        _refreshTimer.Start();
        _tabControl.SelectedIndexChanged += TabControl_SelectedIndexChanged;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Hide to tray instead of closing when user clicks X.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            MinimizedToTray?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refreshTimer.Stop();
        _stats.StatsChanged -= OnStatsChanged;
        _server.StatusChanged -= OnServerStatusChanged;
        _perfService.Sampled -= OnPerfSampled;
        base.OnFormClosed(e);
    }

    // ── Status ──────────────────────────────────────────────────────────────

    private void RefreshStatus()
    {
        bool running = _server.IsRunning;
        _lblStatusValue.Text = running ? $"Running ({_settings.ListenAddress}:{_settings.ListenPort})" : "Stopped";
        _lblStatusValue.ForeColor = running ? Color.Green : Color.Red;
        _btnStart.Enabled = !running;
        _btnStop.Enabled = running;
        _btnRestart.Enabled = running;
        _btnDashStart.Enabled = !running;
        _btnDashStop.Enabled = running;
        _btnDashRestart.Enabled = running;
    }

    private void OnServerStatusChanged(object? sender, string status)
    {
        if (InvokeRequired)
        {
            BeginInvoke(RefreshStatus);
            return;
        }
        RefreshStatus();
    }

    private void BtnStart_Click(object? sender, EventArgs e)
    {
        try
        {
            _server.Start(_settings.ListenPort, _settings.ListenAddress);
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void BtnStop_Click(object? sender, EventArgs e)
    {
        try
        {
            await _server.StopAsync();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error stopping: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async void BtnRestart_Click(object? sender, EventArgs e)
    {
        try
        {
            await _server.RestartAsync(_settings.ListenPort, _settings.ListenAddress);
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error restarting: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ── Stats ────────────────────────────────────────────────────────────────

    private void RefreshStats()
    {
        _lblTotalRequestsValue.Text = _stats.TotalRequests.ToString("N0");
        _lblTotalErrorsValue.Text = _stats.TotalErrors.ToString("N0");
        _lblPromptTokensValue.Text = _stats.TotalPromptTokens.ToString("N0");
        _lblCompletionTokensValue.Text = _stats.TotalCompletionTokens.ToString("N0");
        _lblRpsValue.Text = _stats.RequestsPerSecond.ToString("F2");
    }

    private void OnStatsChanged(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(RefreshStats);
            return;
        }
        RefreshStats();
    }

    private void OnPerfSampled(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(UpdatePerfLabels);
            return;
        }
        UpdatePerfLabels();
    }

    private void UpdatePerfLabels()
    {
        _lblCpuValue.Text = $"{_perfService.CpuPercent:F1}%";
        _lblRamValue.Text = $"{_perfService.MemoryMb:F0} MB";
    }

    private void BtnResetStats_Click(object? sender, EventArgs e)
    {
        _stats.Reset();
        RefreshStats();
        RefreshLogs();
    }

    // ── Logs ─────────────────────────────────────────────────────────────────

    private void RefreshLogs()
    {
        IReadOnlyList<RequestLog> logs = _stats.GetRecentLogs();

        _lstLogs.BeginUpdate();
        _lstLogs.Items.Clear();

        foreach (RequestLog log in logs)
        {
            var item = new ListViewItem(log.Timestamp.ToString("HH:mm:ss"));
            item.SubItems.Add(log.Method);
            item.SubItems.Add(log.OllamaPath);
            item.SubItems.Add(log.Model);
            item.SubItems.Add(log.Status.ToString());
            item.SubItems.Add($"{log.DurationMs:F0}");
            item.SubItems.Add($"{log.PromptTokens}+{log.CompletionTokens}");
            item.SubItems.Add(FormatBytes(log.RequestBytes, log.ResponseBytes));
            item.Tag = log;

            item.ForeColor = log.Status switch
            {
                RequestStatus.Error => Color.Red,
                RequestStatus.Cancelled => Color.DarkOrange,
                _ => SystemColors.WindowText,
            };

            _lstLogs.Items.Add(item);
        }

        _lstLogs.EndUpdate();
    }

    private void BtnClearLogs_Click(object? sender, EventArgs e)
    {
        _stats.Reset();
        _lstLogs.Items.Clear();
    }

    private void BtnRefreshLogs_Click(object? sender, EventArgs e) => RefreshLogs();

    private void BtnLogDetails_Click(object? sender, EventArgs e)
    {
        if (_lstLogs.SelectedItems.Count == 0)
        {
            MessageBox.Show("Select a log entry first.", "No selection",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_lstLogs.SelectedItems[0].Tag is RequestLog log)
            ShowLogDetails(log);
    }

    private void LstLogs_DoubleClick(object? sender, EventArgs e)
    {
        if (_lstLogs.SelectedItems.Count > 0 && _lstLogs.SelectedItems[0].Tag is RequestLog log)
            ShowLogDetails(log);
    }

    private void ShowLogDetails(RequestLog log)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Timestamp : {log.Timestamp:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"Method    : {log.Method}");
        sb.AppendLine($"Path      : {log.OllamaPath}");
        sb.AppendLine($"Upstream  : {log.UpstreamPath}");
        sb.AppendLine($"Model     : {log.Model}");
        sb.AppendLine($"Status    : {log.Status} ({log.StatusCode})");
        sb.AppendLine($"Streaming : {log.Streaming}");
        sb.AppendLine($"Duration  : {log.DurationMs:F1} ms");
        sb.AppendLine($"Tokens    : {log.PromptTokens} prompt + {log.CompletionTokens} completion");
        sb.AppendLine($"Bytes     : {FormatBytes(log.RequestBytes, log.ResponseBytes)} (request / response)");

        if (!string.IsNullOrEmpty(log.ErrorMessage))
        {
            sb.AppendLine();
            sb.AppendLine("── Error ──────────────────────────────────────────────────────");
            sb.AppendLine(log.ErrorMessage);
        }

        if (log.RequestBody is not null)
        {
            sb.AppendLine();
            sb.AppendLine("── Request Body ───────────────────────────────────────────────");
            AppendBody(sb, log.RequestBody);
        }

        if (log.ExceptionId.HasValue)
        {
            ExceptionDetail? ex = _stats.GetException(log.ExceptionId.Value);
            if (ex is not null)
            {
                sb.AppendLine();
                sb.AppendLine("── Exception ──────────────────────────────────────────────────");
                sb.AppendLine($"Type    : {ex.ExceptionType}");
                sb.AppendLine($"Message : {ex.Message}");

                if (ex.InnerExceptions.Count > 0)
                {
                    sb.AppendLine("Inner   :");
                    foreach (string inner in ex.InnerExceptions)
                        sb.AppendLine($"  {inner}");
                }

                if (!string.IsNullOrEmpty(ex.StackTrace))
                {
                    sb.AppendLine();
                    sb.AppendLine("Stack Trace:");
                    sb.AppendLine(ex.StackTrace);
                }
            }
        }

        using var detailForm = new Form
        {
            Text = $"Log Details — {log.Timestamp:HH:mm:ss} {log.OllamaPath}",
            Size = new Size(780, 540),
            MinimumSize = new Size(500, 300),
            MaximizeBox = true,
            FormBorderStyle = FormBorderStyle.Sizable,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
        };

        TabControl tabControl = new()
        {
            Dock = DockStyle.Fill,
            Name = "_tabLogDetails",
        };

        TabPage summaryTab = new()
        {
            Name = "_tabLogSummary",
            Padding = new Padding(8),
            Text = "Summary",
        };

        TextBox summaryText = CreateLogDetailsTextBox(sb.ToString());
        summaryTab.Controls.Add(summaryText);
        tabControl.Controls.Add(summaryTab);

        if (log.ResponseBody is not null)
        {
            TabPage responseTab = new()
            {
                Name = "_tabLogResponseBody",
                Padding = new Padding(8),
                Text = "Response Body",
            };

            TextBox responseText = CreateLogDetailsTextBox(FormatBody(log.ResponseBody));
            responseTab.Controls.Add(responseText);
            tabControl.Controls.Add(responseTab);
        }

        detailForm.Controls.Add(tabControl);
        detailForm.ShowDialog(this);
    }

    private static TextBox CreateLogDetailsTextBox(string text)
    {
        return new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9F),
            Text = text,
        };
    }

    private static string FormatBytes(long requestBytes, long responseBytes)
    {
        static string Fmt(long b) => b switch
        {
            < 0 => "?",
            < 1024 => $"{b} B",
            < 1024 * 1024 => $"{b / 1024.0:F1} KB",
            _ => $"{b / (1024.0 * 1024):F2} MB",
        };
        return $"{Fmt(requestBytes)} / {Fmt(responseBytes)}";
    }

    private static void AppendBody(StringBuilder sb, string body)
    {
        sb.AppendLine(FormatBody(body));
    }

    private static string FormatBody(string body)
    {
        if (TryFormatJson(body, out string? formattedJson))
            return formattedJson;

        if (LooksLikeServerSentEvents(body))
            return FormatServerSentEvents(body);

        return body;
    }

    private static bool TryFormatJson(string body, out string formattedJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            formattedJson = JsonSerializer.Serialize(doc, _indentedJsonOptions);
            return true;
        }
        catch (JsonException)
        {
            formattedJson = string.Empty;
            return false;
        }
    }

    private static bool LooksLikeServerSentEvents(string body)
    {
        ReadOnlySpan<char> span = body.AsSpan().TrimStart();
        return span.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("event:", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("retry:", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatServerSentEvents(string body)
    {
        var sb = new StringBuilder();
        using var reader = new StringReader(body);

        while (reader.ReadLine() is string line)
        {
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                string payload = line[5..].TrimStart();
                if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
                {
                    sb.AppendLine("data: [DONE]");
                    continue;
                }

                if (TryFormatJson(payload, out string? formattedPayload))
                {
                    sb.AppendLine("data:");
                    using var payloadReader = new StringReader(formattedPayload);
                    while (payloadReader.ReadLine() is string payloadLine)
                        sb.Append("  ").AppendLine(payloadLine);
                    continue;
                }
            }

            sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_tabControl.SelectedTab == _tabLogs && _chkAutoRefresh.Checked)
            RefreshLogs();
    }

    private void CmbRefreshInterval_SelectedIndexChanged(object? sender, EventArgs e)
    {
        int intervalMs = _cmbRefreshInterval.SelectedIndex switch
        {
            0 => 1_000,
            1 => 2_000,
            2 => 5_000,
            3 => 10_000,
            4 => 30_000,
            _ => 2_000,
        };
        _refreshTimer.Interval = intervalMs;
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    private void LoadSettingsToForm()
    {
        _txtListenPort.Text = _settings.ListenPort.ToString();
        _txtMaxLogs.Text = _settings.MaxLogEntries.ToString();
        _chkAutoStart.Checked = _settings.AutoStartProxy;
        _chkStartWithDashboard.Checked = _settings.StartWithDashboardOpen;
        _chkCollectDetails.Checked = _settings.CollectRequestDetails;
        _chkCollectResponseDetails.Checked = _settings.CollectResponseDetails;
        _chkStreamingHeartbeats.Checked = _settings.EnableStreamingHeartbeats;
        _txtHeartbeatInterval.Text = _settings.StreamingHeartbeatIntervalSeconds.ToString();

        _dgvMappings.Rows.Clear();
        foreach (ModelMapping mapping in _settings.ModelMappings)
        {
            int idx = _dgvMappings.Rows.Add(
                mapping.OllamaName,
                string.Empty,
                mapping.EnableThinkingCompatibility,
                mapping.UpstreamUrl,
                mapping.UpstreamTimeoutSeconds == 0 ? string.Empty : mapping.UpstreamTimeoutSeconds.ToString(),
                mapping.UpstreamType.ToString());

            DataGridViewRow row = _dgvMappings.Rows[idx];

            // Carry per-row advanced configuration (instruction set + redaction)
            // on the row Tag — these fields are edited in the modal Configure dialog.
            row.Tag = new ModelMapping
            {
                OllamaName = mapping.OllamaName,
                LlamaCppName = mapping.LlamaCppName,
                EnableThinkingCompatibility = mapping.EnableThinkingCompatibility,
                UpstreamUrl = mapping.UpstreamUrl,
                UpstreamTimeoutSeconds = mapping.UpstreamTimeoutSeconds,
                UpstreamType = mapping.UpstreamType,
                EnableAutoSummarization = mapping.EnableAutoSummarization,
                PreserveRecentMessageCount = mapping.PreserveRecentMessageCount,
                MaxSummarizationRetries = mapping.MaxSummarizationRetries,
                InstructionSetName = mapping.InstructionSetName,
                RedactRequestBodies = mapping.RedactRequestBodies,
                RedactResponseBodies = mapping.RedactResponseBodies,
                RedactSensitiveJsonFields = mapping.RedactSensitiveJsonFields,
            };

            // The combo cell needs the item to exist before we can set a value.
            DataGridViewComboBoxCell cell =
                (DataGridViewComboBoxCell)row.Cells[1];

            if (!cell.Items.Contains(mapping.LlamaCppName))
                cell.Items.Add(mapping.LlamaCppName);

            cell.Value = mapping.LlamaCppName;
        }

        // Load instructions list
        RefreshInstructionsList();

        // Logging settings
        _txtLogDir.Text = _settings.Logging.LogDirectory;
        int levelIndex = _cmbMinLevel.FindStringExact(_settings.Logging.MinimumLevel);
        _cmbMinLevel.SelectedIndex = levelIndex >= 0 ? levelIndex : 2; // default Information
        _txtAppLogSize.Text = _settings.Logging.AppLogFileSizeLimitMb.ToString();
        _txtAppLogRetain.Text = _settings.Logging.AppLogRetainedFileCount.ToString();
        _txtReqLogSize.Text = _settings.Logging.RequestLogFileSizeLimitMb.ToString();
        _txtLogRetention.Text = _settings.Logging.LogRetentionHours.ToString();
    }

    private void BtnSaveSettings_Click(object? sender, EventArgs e)
    {
        if (!int.TryParse(_txtListenPort.Text, out int port) || port < 1 || port > 65535)
        {
            MessageBox.Show("Listen port must be a number between 1 and 65535.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!int.TryParse(_txtMaxLogs.Text, out int maxLogs) || maxLogs < 1)
        {
            MessageBox.Show("Max log entries must be a positive number.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_txtLogDir.Text))
        {
            MessageBox.Show("Log directory cannot be empty.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!int.TryParse(_txtAppLogSize.Text, out int appLogSize) || appLogSize < 1)
        {
            MessageBox.Show("App log file size limit must be a positive number.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!int.TryParse(_txtAppLogRetain.Text, out int appLogRetain) || appLogRetain < 1)
        {
            MessageBox.Show("App log files to keep must be a positive number.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!int.TryParse(_txtReqLogSize.Text, out int reqLogSize) || reqLogSize < 1)
        {
            MessageBox.Show("Request DB file size limit must be a positive number.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!int.TryParse(_txtLogRetention.Text, out int logRetentionHours) || logRetentionHours < 0)
        {
            MessageBox.Show("Log retention must be 0 (keep forever) or a positive number of hours.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!int.TryParse(_txtHeartbeatInterval.Text, out int heartbeatIntervalSeconds)
            || heartbeatIntervalSeconds < 5
            || heartbeatIntervalSeconds > 300)
        {
            MessageBox.Show("Heartbeat interval must be a number between 5 and 300 seconds.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings.ListenPort = port;
        _settings.MaxLogEntries = maxLogs;
        _settings.AutoStartProxy = _chkAutoStart.Checked;
        _settings.StartWithDashboardOpen = _chkStartWithDashboard.Checked;
        _settings.CollectRequestDetails = _chkCollectDetails.Checked;
        _settings.CollectResponseDetails = _chkCollectResponseDetails.Checked;
        _settings.EnableStreamingHeartbeats = _chkStreamingHeartbeats.Checked;
        _settings.StreamingHeartbeatIntervalSeconds = heartbeatIntervalSeconds;

        _settings.Logging.LogDirectory = _txtLogDir.Text.Trim();
        _settings.Logging.MinimumLevel = _cmbMinLevel.SelectedItem?.ToString() ?? "Information";
        _settings.Logging.AppLogFileSizeLimitMb = appLogSize;
        _settings.Logging.AppLogRetainedFileCount = appLogRetain;
        _settings.Logging.RequestLogFileSizeLimitMb = reqLogSize;
        _settings.Logging.LogRetentionHours = logRetentionHours;

        _settings.ModelMappings.Clear();
        HashSet<string> seenOllamaNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _dgvMappings.Rows)
        {
            string? ollamaName  = row.Cells[0].Value?.ToString();
            string? llamaName   = row.Cells[1].Value?.ToString();
            bool enableThinkingCompatibility = row.Cells[2].Value as bool? ?? true;
            string? upstreamUrl = row.Cells[3].Value?.ToString() ?? string.Empty;
            string? timeoutStr  = row.Cells[4].Value?.ToString();
            string? upstreamStr = row.Cells[5].Value?.ToString();

            // Advanced per-model settings live on the row Tag and are edited via the Configure dialog.
            ModelMapping? advanced = row.Tag as ModelMapping;

            if (!string.IsNullOrWhiteSpace(ollamaName) && !string.IsNullOrWhiteSpace(llamaName))
            {
                string trimmedOllama = ollamaName.Trim();

                if (!seenOllamaNames.Add(trimmedOllama))
                {
                    MessageBox.Show(
                        $"Duplicate Ollama model name '{trimmedOllama}'. Model names must be unique.",
                        "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Validate upstream URL is required
                if (string.IsNullOrWhiteSpace(upstreamUrl) ||
                    !Uri.TryCreate(upstreamUrl, UriKind.Absolute, out _))
                {
                    MessageBox.Show($"Model mapping '{trimmedOllama}' requires a valid upstream URL.", "Validation",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                UpstreamType upstream = Enum.TryParse(upstreamStr, out UpstreamType parsed)
                    ? parsed
                    : UpstreamType.LlamaCpp;

                if (!int.TryParse(timeoutStr, out int timeoutSec))
                    timeoutSec = 300;  // Default timeout

                _settings.ModelMappings.Add(new ModelMapping
                {
                    OllamaName             = trimmedOllama,
                    LlamaCppName           = llamaName.Trim(),
                    EnableThinkingCompatibility = enableThinkingCompatibility,
                    UpstreamUrl            = upstreamUrl.Trim(),
                    UpstreamTimeoutSeconds = timeoutSec,
                    UpstreamType           = upstream,
                    InstructionSetName     = advanced?.InstructionSetName,
                    RedactRequestBodies    = advanced?.RedactRequestBodies ?? true,
                    RedactResponseBodies   = advanced?.RedactResponseBodies ?? true,
                    RedactSensitiveJsonFields = advanced?.RedactSensitiveJsonFields ?? true,
                });
            }
        }

        _settings.Save();
        _stats.UpdateMaxEntries(maxLogs);
        _stats.UpdateRetentionHours(logRetentionHours);
        _handler.UpdateSettings(_settings);

        // Re-apply logging config immediately so the new level/size/dir is active.
        AppLogger.Initialize(_settings.Logging);

        MessageBox.Show("Settings saved. Restart the proxy for port changes to take effect.",
            "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);

        RefreshStatus();
    }

    private void BtnAddMapping_Click(object? sender, EventArgs e)
    {
        // Seed the llama.cpp combo with whatever models are already loaded.
        // Columns: [0] OllamaName, [1] LlamaCppName, [2] ThinkingCompatibility, [3] UpstreamUrl, [4] Timeout, [5] UpstreamType
        // Advanced settings (instruction set, redaction) live on row.Tag and are edited via the Configure dialog.
        int idx = _dgvMappings.Rows.Add(string.Empty, string.Empty, true, string.Empty, string.Empty, "LlamaCpp");

        DataGridViewRow row = _dgvMappings.Rows[idx];
        row.Tag = new ModelMapping();

        // Ensure the value is valid inside the combo items.
        DataGridViewComboBoxCell modelCell =
            (DataGridViewComboBoxCell)row.Cells[1];

        if (modelCell.Items.Count > 0 && modelCell.Value is null)
            modelCell.Value = modelCell.Items[0];

        _dgvMappings.CurrentCell = row.Cells[0];
        _dgvMappings.BeginEdit(true);
    }

    private void BtnConfigureMapping_Click(object? sender, EventArgs e)
    {
        DataGridViewRow? row = GetSelectedMappingRow();
        if (row is null)
        {
            MessageBox.Show("Select a model mapping row to configure.", "Configure Model",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ConfigureMappingRow(row);
    }

    private void DgvMappings_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _dgvMappings.Rows.Count)
            return;

        // Only react to double-clicks on the row header or non-editable areas to avoid
        // hijacking normal in-cell edits like the combo dropdown.
        if (e.ColumnIndex >= 0)
            return;

        ConfigureMappingRow(_dgvMappings.Rows[e.RowIndex]);
    }

    private void ConfigureMappingRow(DataGridViewRow row)
    {
        if (row.Tag is not ModelMapping mapping)
        {
            mapping = new ModelMapping();
            row.Tag = mapping;
        }

        // Reflect the current Ollama name in the dialog header.
        mapping.OllamaName = row.Cells[0].Value?.ToString() ?? string.Empty;

        if (ModelMappingDialog.ShowConfigureDialog(this, mapping, _settings.InstructionSets))
        {
            // Tag already updated by the dialog. Nothing else to do here.
        }
    }

    private DataGridViewRow? GetSelectedMappingRow()
    {
        foreach (DataGridViewRow row in _dgvMappings.SelectedRows)
        {
            if (!row.IsNewRow)
                return row;
        }

        if (_dgvMappings.CurrentRow is { IsNewRow: false } current)
            return current;

        return null;
    }

    private void BtnRemoveMapping_Click(object? sender, EventArgs e)
    {
        foreach (DataGridViewRow row in _dgvMappings.SelectedRows)
        {
            if (!row.IsNewRow)
                _dgvMappings.Rows.Remove(row);
        }
    }

    // ── Model fetching ────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the model list from the specified upstream URL and returns the ids, or an empty list on failure.
    /// </summary>
    private static async Task<List<string>> FetchUpstreamModelsAsync(string upstreamUrl)
    {
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri(upstreamUrl),
                Timeout = TimeSpan.FromSeconds(10),
            };

            using HttpResponseMessage resp = await client.GetAsync("/v1/models");

            if (!resp.IsSuccessStatusCode)
                return [];

            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            JsonElement data = doc.RootElement.GetProperty("data");

            var models = new List<string>();

            foreach (JsonElement item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out JsonElement id))
                {
                    string? name = id.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        models.Add(name);
                }
            }

            return models;
        }
        catch
        {
            return [];
        }
    }

    private async void BtnFetchModels_Click(object? sender, EventArgs e)
    {
        // Get selected rows or all rows if none selected
        List<DataGridViewRow> rowsToFetch = _dgvMappings.SelectedRows.Count > 0
            ? [.. _dgvMappings.SelectedRows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow)]
            : [.. _dgvMappings.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow)];

        if (rowsToFetch.Count == 0)
        {
            MessageBox.Show("No model mappings to fetch from. Add a row with an upstream URL first.",
                "No Rows", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _btnFetchModels.Enabled = false;
        _btnFetchModels.Text = "Fetching…";

        try
        {
            int successCount = 0;
            int failCount = 0;

            foreach (DataGridViewRow row in rowsToFetch)
            {
                string? upstreamUrl = row.Cells[2].Value?.ToString();

                if (string.IsNullOrWhiteSpace(upstreamUrl))
                {
                    failCount++;
                    continue;
                }

                List<string> models = await FetchUpstreamModelsAsync(upstreamUrl);

                if (models.Count == 0)
                {
                    failCount++;
                    continue;
                }

                // Update this row's llama.cpp combo cell
                DataGridViewComboBoxCell cell = (DataGridViewComboBoxCell)row.Cells[1];
                string? current = cell.Value?.ToString();

                cell.Items.Clear();
                cell.Items.AddRange([.. models]);

                cell.Value = (current is not null && models.Contains(current))
                    ? current
                    : (models.Count > 0 ? models[0] : null);

                successCount++;
            }

            if (successCount > 0 && failCount > 0)
            {
                MessageBox.Show($"Fetched models for {successCount} row(s). {failCount} row(s) failed.",
                    "Partial Success", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (successCount > 0)
            {
                MessageBox.Show($"Successfully fetched models for {successCount} row(s).",
                    "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("Failed to fetch models from all selected rows. Check upstream URLs.",
                    "Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            _btnFetchModels.Enabled = true;
            _btnFetchModels.Text = "Fetch Models ↓";
        }
    }

    // ── Instruction Sets ──────────────────────────────────────────────────────

    private void RefreshInstructionsList()
    {
        _lstInstructions.BeginUpdate();
        _lstInstructions.Items.Clear();

        foreach (InstructionSet instructionSet in _settings.InstructionSets)
        {
            var item = new ListViewItem(instructionSet.Name);
            item.SubItems.Add(instructionSet.Description ?? string.Empty);
            item.Tag = instructionSet;
            _lstInstructions.Items.Add(item);
        }

        _lstInstructions.EndUpdate();
        RefreshInstructionPreview();
    }

    private void RefreshInstructionPreview()
    {
        if (_lstInstructions.SelectedItems.Count > 0 && _lstInstructions.SelectedItems[0].Tag is InstructionSet selected)
        {
            _txtInstructionPreview.Text = selected.Instructions;
        }
        else
        {
            _txtInstructionPreview.Text = string.Empty;
        }
    }

    private void RefreshInstructionDropdowns()
    {
        // Instruction set selection has moved to the modal ModelMappingDialog,
        // which populates its own combo from _settings.InstructionSets each time
        // it is opened. No grid-level dropdown to refresh.
    }

    private void LstInstructions_SelectedIndexChanged(object? sender, EventArgs e)
    {
        RefreshInstructionPreview();
    }

    private void LstInstructions_DoubleClick(object? sender, EventArgs e)
    {
        BtnEditInstruction_Click(sender, e);
    }

    private void BtnAddInstruction_Click(object? sender, EventArgs e)
    {
        InstructionSet? newSet = InstructionSetDialog.ShowAddEditDialog(this);
        if (newSet is null)
            return;

        // Check for duplicate name
        if (_settings.InstructionSets.Any(i => string.Equals(i.Name, newSet.Name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show($"An instruction set named '{newSet.Name}' already exists.", "Duplicate Name",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings.InstructionSets.Add(newSet);
        _settings.Save();
        RefreshInstructionsList();
        RefreshInstructionDropdowns();
    }

    private void BtnEditInstruction_Click(object? sender, EventArgs e)
    {
        if (_lstInstructions.SelectedItems.Count == 0)
            return;

        if (_lstInstructions.SelectedItems[0].Tag is not InstructionSet existing)
            return;

        InstructionSet? edited = InstructionSetDialog.ShowAddEditDialog(this, existing);
        if (edited is null)
            return;

        string oldName = existing.Name;

        // Check for duplicate name (excluding the one being edited)
        if (_settings.InstructionSets.Any(i => i != existing && 
            string.Equals(i.Name, edited.Name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show($"An instruction set named '{edited.Name}' already exists.", "Duplicate Name",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Update in place
        existing.Name = edited.Name;
        existing.Description = edited.Description;
        existing.Instructions = edited.Instructions;

        if (!string.Equals(oldName, edited.Name, StringComparison.OrdinalIgnoreCase))
        {
            foreach (ModelMapping mapping in _settings.ModelMappings)
            {
                if (string.Equals(mapping.InstructionSetName, oldName, StringComparison.OrdinalIgnoreCase))
                    mapping.InstructionSetName = edited.Name;
            }
        }

        _settings.Save();
        LoadSettingsToForm();
        RefreshInstructionsList();
        RefreshInstructionDropdowns();
    }

    private void BtnRemoveInstruction_Click(object? sender, EventArgs e)
    {
        if (_lstInstructions.SelectedItems.Count == 0)
            return;

        if (_lstInstructions.SelectedItems[0].Tag is not InstructionSet toRemove)
            return;

        DialogResult result = MessageBox.Show(
            $"Are you sure you want to remove the instruction set '{toRemove.Name}'?",
            "Confirm Removal", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (result != DialogResult.Yes)
            return;

        int clearedMappings = 0;
        foreach (ModelMapping mapping in _settings.ModelMappings)
        {
            if (string.Equals(mapping.InstructionSetName, toRemove.Name, StringComparison.OrdinalIgnoreCase))
            {
                mapping.InstructionSetName = null;
                clearedMappings++;
            }
        }

        _settings.InstructionSets.Remove(toRemove);
        _settings.Save();
        LoadSettingsToForm();
        RefreshInstructionsList();
        RefreshInstructionDropdowns();

        if (clearedMappings > 0)
        {
            MessageBox.Show($"Removed instruction set and cleared it from {clearedMappings} model mapping(s).",
                "Instruction Set Removed", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // ── Test Console ──────────────────────────────────────────────────────────

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = LoadTestModelsAsync();
    }

    private void TabControl_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_tabControl.SelectedTab == _tabTest)
            _ = LoadTestModelsAsync();
    }

    /// <summary>Populates the test console model combo from the upstream.</summary>
    private async Task LoadTestModelsAsync()
    {
        _lblTestStatus.Text = "Loading models…";

        try
        {
            // Gather unique upstream URLs from all mappings
            var upstreamUrls = _settings.ModelMappings
                .Where(m => !string.IsNullOrWhiteSpace(m.UpstreamUrl))
                .Select(m => m.UpstreamUrl)
                .Distinct()
                .ToList();

            _cmbTestModel.Items.Clear();

            if (upstreamUrls.Count == 0)
            {
                _cmbTestModel.Items.Add("(No model mappings configured)");
                if (_cmbTestModel.Items.Count > 0)
                    _cmbTestModel.SelectedIndex = 0;
                _lblTestStatus.Text = "Configure model mappings in Settings first.";
                return;
            }

            var allModels = new HashSet<string>();

            foreach (string url in upstreamUrls)
            {
                List<string> models = await FetchUpstreamModelsAsync(url);
                foreach (string model in models)
                    allModels.Add(model);
            }

            if (allModels.Count == 0)
            {
                _lblTestStatus.Text = "No models found. Check upstream URLs in Settings.";
                return;
            }

            foreach (string m in allModels)
                _cmbTestModel.Items.Add(m);

            _cmbTestModel.SelectedIndex = 0;
            _lblTestStatus.Text = $"Loaded {allModels.Count} model(s) from {upstreamUrls.Count} upstream(s). Ready.";
        }
        catch (Exception ex)
        {
            _lblTestStatus.Text = $"Model load failed: {ex.Message}";
        }
    }

    private async void BtnTestSend_Click(object? sender, EventArgs e)
    {
        string prompt = _txtTestPrompt.Text.Trim();

        if (string.IsNullOrEmpty(prompt))
        {
            _lblTestStatus.Text = "Enter a prompt first.";
            return;
        }

        string model = _cmbTestModel.SelectedItem?.ToString() ?? string.Empty;

        if (string.IsNullOrEmpty(model))
        {
            _lblTestStatus.Text = "Select a model first.";
            return;
        }

        _btnTestSend.Enabled = false;
        _lblTestStatus.Text = "Sending\u2026";
        _txtTestResponse.Clear();

        try
        {
            double temperature = (double)_nudTestTemp.Value;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            await foreach (string token in StreamChatAsync(model, prompt, temperature))
            {
                _txtTestResponse.AppendText(token);
            }

            sw.Stop();
            _lblTestStatus.Text = $"Done in {sw.Elapsed.TotalSeconds:F2}s.";
        }
        catch (OperationCanceledException)
        {
            _lblTestStatus.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            _lblTestStatus.Text = $"Error: {ex.Message}";
            _txtTestResponse.AppendText($"\r\n\r\n[ERROR]\r\n{ex}");
        }
        finally
        {
            _btnTestSend.Enabled = true;
        }
    }

    /// <summary>
    /// Streams tokens from the upstream /v1/chat/completions endpoint using SSE,
    /// yielding each content delta as it arrives.
    /// </summary>
    private async IAsyncEnumerable<string> StreamChatAsync(
        string model, string prompt, double temperature,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Find the mapping for this model to get the correct upstream URL
        var mapping = _settings.ModelMappings.FirstOrDefault(m =>
            string.Equals(m.OllamaName, model, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.LlamaCppName, model, StringComparison.OrdinalIgnoreCase));

        if (mapping is null || string.IsNullOrWhiteSpace(mapping.UpstreamUrl))
        {
            yield return "[ERROR: No upstream URL configured for this model]";
            yield break;
        }

        int timeout = mapping.UpstreamTimeoutSeconds > 0 ? mapping.UpstreamTimeoutSeconds : 300;

        using var client = new HttpClient
        {
            BaseAddress = new Uri(mapping.UpstreamUrl),
            Timeout = TimeSpan.FromSeconds(timeout),
        };

        var payload = new
        {
            model,
            stream = true,
            temperature,
            messages = new[]
            {
                new { role = "user", content = prompt },
            },
        };

        var reqMsg = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(payload),
        };

        using HttpResponseMessage resp = await client.SendAsync(
            reqMsg, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Upstream returned {(int)resp.StatusCode}: {body}");
        }

        using var reader = new System.IO.StreamReader(
            await resp.Content.ReadAsStreamAsync(ct));

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            string data = line["data:".Length..].Trim();

            if (data == "[DONE]")
                yield break;

            JsonElement root;

            try
            {
                root = JsonDocument.Parse(data).RootElement;
            }
            catch (JsonException)
            {
                continue;
            }

            if (!root.TryGetProperty("choices", out JsonElement choices))
                continue;

            foreach (JsonElement choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("delta", out JsonElement delta))
                    continue;

                if (delta.TryGetProperty("content", out JsonElement content))
                {
                    string? text = content.GetString();

                    if (!string.IsNullOrEmpty(text))
                        yield return text;
                }
            }
        }
    }

    private void BtnTestClear_Click(object? sender, EventArgs e)
    {
        _txtTestPrompt.Clear();
        _txtTestResponse.Clear();
        _lblTestStatus.Text = "Ready.";
    }
}
