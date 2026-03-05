using System.Drawing;
using System.Linq;
using PressTalk.App.Configuration;
using PressTalk.App.Hotkey;

namespace PressTalk.App.Ui;

public sealed class SettingsForm : Form
{
    private readonly ComboBox _hotkeyComboBox;
    private readonly ComboBox _modeComboBox;
    private readonly CheckBox _manualSemanticCheckBox;
    private readonly CheckBox _speakerDiarizationCheckBox;
    private readonly CheckBox _alwaysOnTopCheckBox;
    private readonly TextBox _hotwordsTextBox;

    public SettingsForm(AppUserConfig currentConfig)
    {
        Text = "PressTalk 设置";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        Width = 520;
        Height = 600;
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.White;
        Padding = new Padding(1);

        var titleBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = Color.FromArgb(45, 45, 48)
        };

        var titleLabel = new Label
        {
            Text = "⚙ PressTalk 设置",
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = false,
            Width = 400,
            Height = 48,
            TextAlign = ContentAlignment.MiddleLeft,
            Left = 20
        };
        titleBar.Controls.Add(titleLabel);

        var closeBtn = new Button
        {
            Text = "✕",
            Width = 48,
            Height = 48,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 14f),
            Cursor = Cursors.Hand,
            Dock = DockStyle.Right
        };
        closeBtn.FlatAppearance.BorderSize = 0;
        closeBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 17, 35);
        closeBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        titleBar.Controls.Add(closeBtn);

        Controls.Add(titleBar);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 16,
            BackColor = Color.FromArgb(30, 30, 30),
            AutoScroll = true
        };
        panel.RowStyles.Clear();
        for (var i = 0; i < 15; i++)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        Controls.Add(panel);

        panel.Controls.Add(MakeTitle("热键"), 0, 0);
        _hotkeyComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 240,
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.FromArgb(45, 45, 48),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        foreach (var preset in HoldKeyPresetCatalog.All)
        {
            _hotkeyComboBox.Items.Add(preset);
        }
        _hotkeyComboBox.DisplayMember = nameof(HoldKeyPreset.DisplayName);
        var selectedPreset = HoldKeyPresetCatalog.Resolve(currentConfig.HoldKeyVirtualKey);
        _hotkeyComboBox.SelectedItem = HoldKeyPresetCatalog.All.First(p => p.VirtualKey == selectedPreset.VirtualKey);
        panel.Controls.Add(_hotkeyComboBox, 0, 1);
        panel.Controls.Add(MakeHint("建议使用单个功能键。默认 F8，单击热键可开始/停止流式录音。"), 0, 2);

        panel.Controls.Add(MakeTitle("行为"), 0, 3);
        panel.Controls.Add(MakeHint("极速模式优先低延迟落字；格式化模式会做标点与分段。"), 0, 4);
        _modeComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 240,
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.FromArgb(45, 45, 48),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _modeComboBox.Items.Add(new ModeOption("极速输出（推荐）", "fast"));
        _modeComboBox.Items.Add(new ModeOption("格式化输出", "formatted"));
        _modeComboBox.DisplayMember = nameof(ModeOption.DisplayName);
        var selectedMode = string.Equals(currentConfig.TranscriptionMode, "formatted", StringComparison.OrdinalIgnoreCase)
            ? "formatted"
            : "fast";
        _modeComboBox.SelectedItem = _modeComboBox.Items.Cast<ModeOption>().First(x => x.Value == selectedMode);
        panel.Controls.Add(_modeComboBox, 0, 5);

        _manualSemanticCheckBox = MakeCheckBox(
            "结束录音后启用语义润色（最终文本）",
            currentConfig.EnableManualSemanticLlm);
        panel.Controls.Add(_manualSemanticCheckBox, 0, 6);

        _speakerDiarizationCheckBox = MakeCheckBox(
            "启用 CampPlus 说话人分离（会增加结束延迟）",
            currentConfig.EnableSpeakerDiarization);
        panel.Controls.Add(_speakerDiarizationCheckBox, 0, 7);

        panel.Controls.Add(MakeTitle("专业词库（热词）"), 0, 8);
        panel.Controls.Add(MakeHint("每行一个词，流式识别时按热词引导。"), 0, 9);
        _hotwordsTextBox = new TextBox
        {
            Multiline = true,
            Width = 460,
            Height = 140,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 10f),
            BackColor = Color.FromArgb(45, 45, 48),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Text = currentConfig.HotwordConfig.ToMultiline()
        };
        panel.Controls.Add(_hotwordsTextBox, 0, 10);

        _alwaysOnTopCheckBox = MakeCheckBox(
            "悬浮按钮始终置顶",
            currentConfig.AlwaysOnTop);
        panel.Controls.Add(_alwaysOnTopCheckBox, 0, 11);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 20, 0, 0)
        };

        var saveButton = new Button
        {
            Text = "保存",
            DialogResult = DialogResult.OK,
            Width = 100,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f),
            Cursor = Cursors.Hand
        };
        saveButton.FlatAppearance.BorderSize = 0;

        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Width = 100,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 12, 0)
        };
        cancelButton.FlatAppearance.BorderSize = 0;

        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        panel.Controls.Add(buttons, 0, 13);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    public AppUserConfig BuildUpdatedConfig(AppUserConfig currentConfig)
    {
        var selectedPreset = (HoldKeyPreset)_hotkeyComboBox.SelectedItem!;
        var selectedMode = ((ModeOption)_modeComboBox.SelectedItem!).Value;
        return new AppUserConfig
        {
            SchemaVersion = currentConfig.SchemaVersion,
            HoldKeyName = selectedPreset.DisplayName,
            HoldKeyVirtualKey = selectedPreset.VirtualKey,
            EnableLiveCaption = true,
            EnableManualSemanticLlm = _manualSemanticCheckBox.Checked,
            EnableStickyDictationSemantic = false,
            EnableSpeakerDiarization = _speakerDiarizationCheckBox.Checked,
            HotwordConfig = HotwordConfig.FromMultiline(_hotwordsTextBox.Text),
            TranscriptionMode = selectedMode,
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
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = Color.FromArgb(100, 180, 255),
            Text = text,
            Margin = new Padding(0, 12, 0, 8)
        };
    }

    private static Label MakeHint(string text)
    {
        return new Label
        {
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            ForeColor = Color.FromArgb(160, 160, 160),
            Font = new Font("Segoe UI", 9f),
            Text = text,
            Margin = new Padding(0, 0, 0, 12)
        };
    }

    private static CheckBox MakeCheckBox(string text, bool value)
    {
        return new CheckBox
        {
            AutoSize = true,
            Checked = value,
            Text = text,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f),
            Margin = new Padding(0, 4, 0, 12)
        };
    }

    private sealed record ModeOption(string DisplayName, string Value);
}
