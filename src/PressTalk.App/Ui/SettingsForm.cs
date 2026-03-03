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
        Text = "PressTalk 设置";
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

        panel.Controls.Add(MakeTitle("热键"), 0, 0);
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
        panel.Controls.Add(MakeHint("建议使用单个功能键。默认 F8，误触更少。"), 0, 2);

        panel.Controls.Add(MakeTitle("行为"), 0, 3);
        _stickySemanticCheckBox = MakeCheckBox(
            "长文模式启用轻量语义润色",
            currentConfig.EnableStickyDictationSemantic);
        panel.Controls.Add(_stickySemanticCheckBox, 0, 4);

        _manualSemanticCheckBox = MakeCheckBox(
            "普通模式也启用语义润色",
            currentConfig.EnableManualSemanticLlm);
        panel.Controls.Add(_manualSemanticCheckBox, 0, 5);

        _liveCaptionCheckBox = MakeCheckBox(
            "录音时显示实时预览",
            currentConfig.EnableLiveCaption);
        panel.Controls.Add(_liveCaptionCheckBox, 0, 6);

        _alwaysOnTopCheckBox = MakeCheckBox(
            "悬浮按钮始终置顶",
            currentConfig.AlwaysOnTop);
        panel.Controls.Add(_alwaysOnTopCheckBox, 0, 7);

        panel.Controls.Add(MakeHint("按住热键 + 空格进入长文模式，再按一次热键结束。"), 0, 8);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 12, 0, 0)
        };

        var saveButton = new Button
        {
            Text = "保存",
            DialogResult = DialogResult.OK,
            AutoSize = true
        };
        var cancelButton = new Button
        {
            Text = "取消",
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
