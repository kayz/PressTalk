using System.Drawing;
using System.Linq;
using PressTalk.App.Configuration;
using PressTalk.App.Hotkey;

namespace PressTalk.App.Ui;

public sealed class SettingsForm : Form
{
    private readonly ComboBox _hotkeyComboBox;
    private readonly CheckBox _liveCaptionCheckBox;
    private readonly CheckBox _manualSemanticCheckBox;
    private readonly CheckBox _stickySemanticCheckBox;
    private readonly CheckBox _alwaysOnTopCheckBox;

    public SettingsForm(AppUserConfig currentConfig)
    {
        Text = "PressTalk Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        Width = 420;
        Height = 340;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 10
        };
        panel.RowStyles.Clear();
        for (var i = 0; i < 9; i++)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        Controls.Add(panel);

        panel.Controls.Add(MakeTitle("Hotkey"), 0, 0);
        _hotkeyComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 220
        };
        foreach (var preset in HoldKeyPresetCatalog.All)
        {
            _hotkeyComboBox.Items.Add(preset);
        }
        _hotkeyComboBox.DisplayMember = nameof(HoldKeyPreset.DisplayName);
        var selectedPreset = HoldKeyPresetCatalog.Resolve(currentConfig.HoldKeyVirtualKey);
        _hotkeyComboBox.SelectedItem = HoldKeyPresetCatalog.All.First(p => p.VirtualKey == selectedPreset.VirtualKey);
        panel.Controls.Add(_hotkeyComboBox, 0, 1);
        panel.Controls.Add(MakeHint("Use one function key. F8 is the default and avoids accidental triggers."), 0, 2);

        panel.Controls.Add(MakeTitle("Behavior"), 0, 3);
        _stickySemanticCheckBox = MakeCheckBox(
            "Long dictation mode uses light semantic cleanup",
            currentConfig.EnableStickyDictationSemantic);
        panel.Controls.Add(_stickySemanticCheckBox, 0, 4);

        _manualSemanticCheckBox = MakeCheckBox(
            "Normal mode also uses semantic cleanup",
            currentConfig.EnableManualSemanticLlm);
        panel.Controls.Add(_manualSemanticCheckBox, 0, 5);

        _liveCaptionCheckBox = MakeCheckBox(
            "Show live caption preview while recording",
            currentConfig.EnableLiveCaption);
        panel.Controls.Add(_liveCaptionCheckBox, 0, 6);

        _alwaysOnTopCheckBox = MakeCheckBox(
            "Keep floating button always on top",
            currentConfig.AlwaysOnTop);
        panel.Controls.Add(_alwaysOnTopCheckBox, 0, 7);

        panel.Controls.Add(MakeHint("Long dictation mode is entered with hold-key + Space, then press the hold-key again to stop."), 0, 8);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 12, 0, 0)
        };

        var saveButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            AutoSize = true
        };
        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true
        };
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        panel.Controls.Add(buttons, 0, 9);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    public AppUserConfig BuildUpdatedConfig(AppUserConfig currentConfig)
    {
        var selectedPreset = (HoldKeyPreset)_hotkeyComboBox.SelectedItem!;
        return new AppUserConfig
        {
            SchemaVersion = currentConfig.SchemaVersion,
            HoldKeyName = selectedPreset.DisplayName,
            HoldKeyVirtualKey = selectedPreset.VirtualKey,
            EnableLiveCaption = _liveCaptionCheckBox.Checked,
            EnableManualSemanticLlm = _manualSemanticCheckBox.Checked,
            EnableStickyDictationSemantic = _stickySemanticCheckBox.Checked,
            AlwaysOnTop = _alwaysOnTopCheckBox.Checked,
            FloatingWindowX = currentConfig.FloatingWindowX,
            FloatingWindowY = currentConfig.FloatingWindowY
        };
    }

    private static Label MakeTitle(string text)
    {
        return new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
            Text = text,
            Margin = new Padding(0, 4, 0, 6)
        };
    }

    private static Label MakeHint(string text)
    {
        return new Label
        {
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            ForeColor = Color.FromArgb(90, 90, 90),
            Text = text,
            Margin = new Padding(0, 0, 0, 8)
        };
    }

    private static CheckBox MakeCheckBox(string text, bool value)
    {
        return new CheckBox
        {
            AutoSize = true,
            Checked = value,
            Text = text,
            Margin = new Padding(0, 0, 0, 8)
        };
    }
}
