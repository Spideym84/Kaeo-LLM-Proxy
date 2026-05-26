using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Kaeo.LlmProxy.Core.Models;

namespace Kaeo.LlmProxy;

/// <summary>
/// Modal dialog for editing advanced per-model configuration that is not
/// displayed in the main mappings grid. The dialog blocks the main window
/// while open (ShowDialog).
/// </summary>
internal sealed class ModelMappingDialog : Form
{
    private const string NoneLabel = "(None)";

    private readonly TableLayoutPanel _tlpMain = new();
    private readonly Label _lblProxyName = new();
    private readonly Label _lblProxyNameValue = new();
    private readonly Label _lblUpstreamUrl = new();
    private readonly Label _lblUpstreamUrlValue = new();
    private readonly Label _lblModelName = new();
    private readonly ComboBox _cmbModelName = new();
    private readonly Button _btnFetchModels = new();
    private readonly Label _lblInstructionSet = new();
    private readonly ComboBox _cmbInstructionSet = new();
    private readonly Label _lblUpstreamTimeout = new();
    private readonly TextBox _txtUpstreamTimeout = new();
    private readonly CheckBox _chkEnableThinkingCompatibility = new();
    private readonly CheckBox _chkRedactRequestBodies = new();
    private readonly CheckBox _chkRedactResponseBodies = new();
    private readonly CheckBox _chkRedactSensitiveJsonFields = new();
    private readonly FlowLayoutPanel _flpButtons = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnCancel = new();

    private string _upstreamUrl = string.Empty;

    public ModelMappingDialog()
    {
        InitializeUi();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private string? InstructionSetName
    {
        get
        {
            string? value = _cmbInstructionSet.SelectedItem?.ToString();
            return string.Equals(value, NoneLabel, StringComparison.OrdinalIgnoreCase)
                ? null
                : value;
        }
        set
        {
            string target = string.IsNullOrWhiteSpace(value) ? NoneLabel : value!;
            int idx = _cmbInstructionSet.FindStringExact(target);
            _cmbInstructionSet.SelectedIndex = idx >= 0 ? idx : 0;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private bool RedactRequestBodies
    {
        get => _chkRedactRequestBodies.Checked;
        set => _chkRedactRequestBodies.Checked = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private bool RedactResponseBodies
    {
        get => _chkRedactResponseBodies.Checked;
        set => _chkRedactResponseBodies.Checked = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private bool RedactSensitiveJsonFields
    {
        get => _chkRedactSensitiveJsonFields.Checked;
        set => _chkRedactSensitiveJsonFields.Checked = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private bool EnableThinkingCompatibility
    {
        get => _chkEnableThinkingCompatibility.Checked;
        set => _chkEnableThinkingCompatibility.Checked = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private int UpstreamTimeoutSeconds
    {
        get => int.TryParse(_txtUpstreamTimeout.Text, out int v) && v > 0 ? v : 300;
        set => _txtUpstreamTimeout.Text = value <= 0 ? "300" : value.ToString();
    }

    private void PopulateInstructionSets(IEnumerable<InstructionSet> instructionSets)
    {
        _cmbInstructionSet.Items.Clear();
        _cmbInstructionSet.Items.Add(NoneLabel);
        foreach (InstructionSet set in instructionSets)
        {
            _cmbInstructionSet.Items.Add(set.Name);
        }
        _cmbInstructionSet.SelectedIndex = 0;
    }

    private void PopulateModelItems(IEnumerable<string> models, string? selected)
    {
        _cmbModelName.Items.Clear();
        foreach (string m in models)
        {
            if (!string.IsNullOrWhiteSpace(m) && !_cmbModelName.Items.Contains(m))
                _cmbModelName.Items.Add(m);
        }

        if (!string.IsNullOrWhiteSpace(selected))
        {
            if (!_cmbModelName.Items.Contains(selected))
                _cmbModelName.Items.Add(selected);

            _cmbModelName.SelectedItem = selected;
        }
    }

    private void InitializeUi()
    {
        SuspendLayout();

        _tlpMain.ColumnCount = 3;
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpMain.RowCount = 10;
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.Dock = DockStyle.Fill;
        _tlpMain.Padding = new Padding(8);

        _tlpMain.Controls.Add(_lblProxyName, 0, 0);
        _tlpMain.SetColumnSpan(_lblProxyNameValue, 2);
        _tlpMain.Controls.Add(_lblProxyNameValue, 1, 0);

        _tlpMain.Controls.Add(_lblUpstreamUrl, 0, 1);
        _tlpMain.SetColumnSpan(_lblUpstreamUrlValue, 2);
        _tlpMain.Controls.Add(_lblUpstreamUrlValue, 1, 1);

        _tlpMain.Controls.Add(_lblModelName, 0, 2);
        _tlpMain.Controls.Add(_cmbModelName, 1, 2);
        _tlpMain.Controls.Add(_btnFetchModels, 2, 2);

        _tlpMain.Controls.Add(_lblInstructionSet, 0, 3);
        _tlpMain.SetColumnSpan(_cmbInstructionSet, 2);
        _tlpMain.Controls.Add(_cmbInstructionSet, 1, 3);

        _tlpMain.Controls.Add(_lblUpstreamTimeout, 0, 4);
        _tlpMain.SetColumnSpan(_txtUpstreamTimeout, 2);
        _tlpMain.Controls.Add(_txtUpstreamTimeout, 1, 4);

        _tlpMain.SetColumnSpan(_chkEnableThinkingCompatibility, 3);
        _tlpMain.Controls.Add(_chkEnableThinkingCompatibility, 0, 5);
        _tlpMain.SetColumnSpan(_chkRedactRequestBodies, 3);
        _tlpMain.Controls.Add(_chkRedactRequestBodies, 0, 6);
        _tlpMain.SetColumnSpan(_chkRedactResponseBodies, 3);
        _tlpMain.Controls.Add(_chkRedactResponseBodies, 0, 7);
        _tlpMain.SetColumnSpan(_chkRedactSensitiveJsonFields, 3);
        _tlpMain.Controls.Add(_chkRedactSensitiveJsonFields, 0, 8);
        _tlpMain.SetColumnSpan(_flpButtons, 3);
        _tlpMain.Controls.Add(_flpButtons, 0, 9);

        _lblProxyName.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblProxyName.AutoSize = true;
        _lblProxyName.Margin = new Padding(0, 4, 8, 4);
        _lblProxyName.Text = "Proxy Name:";

        _lblProxyNameValue.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblProxyNameValue.AutoSize = true;
        _lblProxyNameValue.Margin = new Padding(0, 4, 0, 4);
        _lblProxyNameValue.Font = new Font(Font, FontStyle.Bold);

        _lblUpstreamUrl.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblUpstreamUrl.AutoSize = true;
        _lblUpstreamUrl.Margin = new Padding(0, 4, 8, 4);
        _lblUpstreamUrl.Text = "Upstream URL:";

        _lblUpstreamUrlValue.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblUpstreamUrlValue.AutoSize = true;
        _lblUpstreamUrlValue.Margin = new Padding(0, 4, 0, 4);

        _lblModelName.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblModelName.AutoSize = true;
        _lblModelName.Margin = new Padding(0, 8, 8, 4);
        _lblModelName.Text = "Model Name:";

        _cmbModelName.Dock = DockStyle.Fill;
        _cmbModelName.Margin = new Padding(0, 4, 4, 4);

        _btnFetchModels.Anchor = AnchorStyles.Right;
        _btnFetchModels.AutoSize = true;
        _btnFetchModels.Margin = new Padding(0, 4, 0, 4);
        _btnFetchModels.MinimumSize = new Size(110, 24);
        _btnFetchModels.Text = "Fetch Models \u2193";
        _btnFetchModels.Click += BtnFetchModels_Click;

        _lblInstructionSet.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblInstructionSet.AutoSize = true;
        _lblInstructionSet.Margin = new Padding(0, 8, 8, 4);
        _lblInstructionSet.Text = "Instruction Set:";

        _cmbInstructionSet.Dock = DockStyle.Fill;
        _cmbInstructionSet.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbInstructionSet.Margin = new Padding(0, 4, 0, 4);

        _lblUpstreamTimeout.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblUpstreamTimeout.AutoSize = true;
        _lblUpstreamTimeout.Margin = new Padding(0, 8, 8, 4);
        _lblUpstreamTimeout.Text = "Upstream Timeout (s):";

        _txtUpstreamTimeout.Dock = DockStyle.Fill;
        _txtUpstreamTimeout.Margin = new Padding(0, 4, 0, 4);

        _chkEnableThinkingCompatibility.AutoSize = true;
        _chkEnableThinkingCompatibility.Margin = new Padding(0, 8, 0, 2);
        _chkEnableThinkingCompatibility.Text = "Enable thinking compatibility (strip assistant response-prefill turns)";

        _chkRedactRequestBodies.AutoSize = true;
        _chkRedactRequestBodies.Margin = new Padding(0, 8, 0, 2);
        _chkRedactRequestBodies.Text = "Redact captured request bodies";

        _chkRedactResponseBodies.AutoSize = true;
        _chkRedactResponseBodies.Margin = new Padding(0, 2, 0, 2);
        _chkRedactResponseBodies.Text = "Redact captured response bodies";

        _chkRedactSensitiveJsonFields.AutoSize = true;
        _chkRedactSensitiveJsonFields.Margin = new Padding(0, 2, 0, 8);
        _chkRedactSensitiveJsonFields.Text = "Redact sensitive JSON fields (api keys, prompts, messages)";

        _flpButtons.AutoSize = true;
        _flpButtons.Controls.Add(_btnCancel);
        _flpButtons.Controls.Add(_btnOk);
        _flpButtons.Dock = DockStyle.Fill;
        _flpButtons.FlowDirection = FlowDirection.RightToLeft;
        _flpButtons.Margin = new Padding(0, 8, 0, 0);

        _btnOk.AutoSize = true;
        _btnOk.DialogResult = DialogResult.OK;
        _btnOk.MinimumSize = new Size(80, 28);
        _btnOk.Text = "OK";

        _btnCancel.AutoSize = true;
        _btnCancel.DialogResult = DialogResult.Cancel;
        _btnCancel.Margin = new Padding(0, 0, 8, 0);
        _btnCancel.MinimumSize = new Size(80, 28);
        _btnCancel.Text = "Cancel";

        AcceptButton = _btnOk;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        CancelButton = _btnCancel;
        ClientSize = new Size(560, 380);
        Controls.Add(_tlpMain);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Configure Model";

        ResumeLayout(false);
    }

    private async void BtnFetchModels_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_upstreamUrl) ||
            !Uri.TryCreate(_upstreamUrl, UriKind.Absolute, out _))
        {
            MessageBox.Show(this,
                "This model mapping does not have a valid upstream URL configured. " +
                "Set the upstream URL in the main mappings grid first.",
                "Fetch Models", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _btnFetchModels.Enabled = false;
        string originalText = _btnFetchModels.Text;
        _btnFetchModels.Text = "Fetching\u2026";

        try
        {
            List<string> models = await FetchUpstreamModelsAsync(_upstreamUrl);

            if (models.Count == 0)
            {
                MessageBox.Show(this,
                    $"Failed to fetch models from '{_upstreamUrl}'. Check that the server is reachable.",
                    "Fetch Models", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string? current = _cmbModelName.SelectedItem?.ToString() ?? _cmbModelName.Text;

            _cmbModelName.Items.Clear();
            _cmbModelName.Items.AddRange([.. models.Cast<object>()]);

            if (!string.IsNullOrWhiteSpace(current) && models.Contains(current))
                _cmbModelName.SelectedItem = current;
            else if (_cmbModelName.Items.Count > 0)
                _cmbModelName.SelectedIndex = 0;
        }
        finally
        {
            _btnFetchModels.Enabled = true;
            _btnFetchModels.Text = originalText;
        }
    }

    /// <summary>
    /// Fetches the model list from the specified upstream URL and returns the ids, or an empty list on failure.
    /// </summary>
    internal static async Task<List<string>> FetchUpstreamModelsAsync(string upstreamUrl)
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

    /// <summary>
    /// Shows the modal dialog for the supplied <paramref name="mapping"/>. The dialog is
    /// modal — the owner cannot be activated until the user closes it. Returns true and
    /// writes the user's changes back to <paramref name="mapping"/> when accepted.
    /// </summary>
    /// <param name="existingModelItems">Models currently listed in the row's combo cell, used to seed the model picker.</param>
    /// <param name="updatedModelItems">Receives the current list of model items after the dialog closes (whether OK or Cancel).</param>
    public static bool ShowConfigureDialog(
        IWin32Window owner,
        ModelMapping mapping,
        IEnumerable<InstructionSet> instructionSets,
        IEnumerable<string> existingModelItems,
        out List<string> updatedModelItems)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(instructionSets);
        ArgumentNullException.ThrowIfNull(existingModelItems);

        using ModelMappingDialog dlg = new();
        dlg.PopulateInstructionSets(instructionSets);
        dlg._lblProxyNameValue.Text = string.IsNullOrWhiteSpace(mapping.ProxyName)
            ? "(unnamed)"
            : mapping.ProxyName;
        dlg._upstreamUrl = mapping.UpstreamUrl ?? string.Empty;
        dlg._lblUpstreamUrlValue.Text = string.IsNullOrWhiteSpace(dlg._upstreamUrl)
            ? "(not set)"
            : dlg._upstreamUrl;
        dlg.PopulateModelItems(existingModelItems, mapping.ModelName);
        dlg.InstructionSetName = mapping.InstructionSetName;
        dlg.EnableThinkingCompatibility = mapping.EnableThinkingCompatibility;
        dlg.UpstreamTimeoutSeconds = mapping.UpstreamTimeoutSeconds;
        dlg.RedactRequestBodies = mapping.RedactRequestBodies;
        dlg.RedactResponseBodies = mapping.RedactResponseBodies;
        dlg.RedactSensitiveJsonFields = mapping.RedactSensitiveJsonFields;

        DialogResult result = dlg.ShowDialog(owner);

        updatedModelItems = [.. dlg._cmbModelName.Items.Cast<object>().Select(o => o?.ToString() ?? string.Empty)];

        if (result != DialogResult.OK)
            return false;

        mapping.ModelName = (dlg._cmbModelName.SelectedItem?.ToString() ?? dlg._cmbModelName.Text ?? string.Empty).Trim();
        mapping.InstructionSetName = dlg.InstructionSetName;
        mapping.EnableThinkingCompatibility = dlg.EnableThinkingCompatibility;
        mapping.UpstreamTimeoutSeconds = dlg.UpstreamTimeoutSeconds;
        mapping.RedactRequestBodies = dlg.RedactRequestBodies;
        mapping.RedactResponseBodies = dlg.RedactResponseBodies;
        mapping.RedactSensitiveJsonFields = dlg.RedactSensitiveJsonFields;
        return true;
    }
}
