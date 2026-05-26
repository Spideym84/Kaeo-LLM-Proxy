using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
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
    private readonly Label _lblOllamaName = new();
    private readonly Label _lblOllamaNameValue = new();
    private readonly Label _lblInstructionSet = new();
    private readonly ComboBox _cmbInstructionSet = new();
    private readonly CheckBox _chkRedactRequestBodies = new();
    private readonly CheckBox _chkRedactResponseBodies = new();
    private readonly CheckBox _chkRedactSensitiveJsonFields = new();
    private readonly FlowLayoutPanel _flpButtons = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnCancel = new();

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

    private void InitializeUi()
    {
        SuspendLayout();

        _tlpMain.ColumnCount = 2;
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpMain.RowCount = 6;
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.Dock = DockStyle.Fill;
        _tlpMain.Padding = new Padding(8);

        _tlpMain.Controls.Add(_lblOllamaName, 0, 0);
        _tlpMain.Controls.Add(_lblOllamaNameValue, 1, 0);
        _tlpMain.Controls.Add(_lblInstructionSet, 0, 1);
        _tlpMain.Controls.Add(_cmbInstructionSet, 1, 1);
        _tlpMain.SetColumnSpan(_chkRedactRequestBodies, 2);
        _tlpMain.Controls.Add(_chkRedactRequestBodies, 0, 2);
        _tlpMain.SetColumnSpan(_chkRedactResponseBodies, 2);
        _tlpMain.Controls.Add(_chkRedactResponseBodies, 0, 3);
        _tlpMain.SetColumnSpan(_chkRedactSensitiveJsonFields, 2);
        _tlpMain.Controls.Add(_chkRedactSensitiveJsonFields, 0, 4);
        _tlpMain.SetColumnSpan(_flpButtons, 2);
        _tlpMain.Controls.Add(_flpButtons, 0, 5);

        _lblOllamaName.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblOllamaName.AutoSize = true;
        _lblOllamaName.Margin = new Padding(0, 4, 8, 4);
        _lblOllamaName.Text = "Model:";

        _lblOllamaNameValue.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblOllamaNameValue.AutoSize = true;
        _lblOllamaNameValue.Margin = new Padding(0, 4, 0, 4);
        _lblOllamaNameValue.Font = new Font(Font, FontStyle.Bold);

        _lblInstructionSet.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblInstructionSet.AutoSize = true;
        _lblInstructionSet.Margin = new Padding(0, 8, 8, 4);
        _lblInstructionSet.Text = "Instruction Set:";

        _cmbInstructionSet.Dock = DockStyle.Fill;
        _cmbInstructionSet.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbInstructionSet.Margin = new Padding(0, 4, 0, 4);

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
        ClientSize = new Size(520, 260);
        Controls.Add(_tlpMain);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Configure Model";

        ResumeLayout(false);
    }

    /// <summary>
    /// Shows the modal dialog for the supplied <paramref name="mapping"/>. The dialog is
    /// modal — the owner cannot be activated until the user closes it. Returns true and
    /// writes the user's changes back to <paramref name="mapping"/> when accepted.
    /// </summary>
    public static bool ShowConfigureDialog(
        IWin32Window owner,
        ModelMapping mapping,
        IEnumerable<InstructionSet> instructionSets)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(instructionSets);

        using ModelMappingDialog dlg = new();
        dlg.PopulateInstructionSets(instructionSets);
        dlg._lblOllamaNameValue.Text = string.IsNullOrWhiteSpace(mapping.OllamaName)
            ? "(unnamed)"
            : mapping.OllamaName;
        dlg.InstructionSetName = mapping.InstructionSetName;
        dlg.RedactRequestBodies = mapping.RedactRequestBodies;
        dlg.RedactResponseBodies = mapping.RedactResponseBodies;
        dlg.RedactSensitiveJsonFields = mapping.RedactSensitiveJsonFields;

        if (dlg.ShowDialog(owner) != DialogResult.OK)
            return false;

        mapping.InstructionSetName = dlg.InstructionSetName;
        mapping.RedactRequestBodies = dlg.RedactRequestBodies;
        mapping.RedactResponseBodies = dlg.RedactResponseBodies;
        mapping.RedactSensitiveJsonFields = dlg.RedactSensitiveJsonFields;
        return true;
    }
}
