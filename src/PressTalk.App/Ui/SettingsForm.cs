using System.Drawing;
using System.Linq;
using PressTalk.App.Configuration;
using PressTalk.App.Hotkey;

namespace PressTalk.App.Ui;

public sealed class SettingsForm : Form
{
    private readonly ComboBox _hotkeyComboBox;
    private readonly CheckBox _manualSemanticCheckBox;
    private readonly CheckBox _speakerDiarizationCheckBox;
    private readonly CheckBox _alwaysOnTopCheckBox;
    private readonly TextBox _hotwordsTextBox;

    public SettingsForm(AppUserConfig currentConfig)
    {
        Text = "PressTalk 设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        Width = 480;
        Height = 540;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 16
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
        panel.Controls.Add(MakeHint("建议使用单个功能键。默认 F8，单击热键可开始/停止流式录音。"), 0, 2);

        panel.Controls.Add(MakeTitle("行为"), 0, 3);
        _manualSemanticCheckBox = MakeCheckBox(
            "结束录音后启用语义润色（最终文本）",
            currentConfig.EnableManualSemanticLlm);
        panel.Controls.Add(_manualSemanticCheckBox, 0, 4);

        _speakerDiarizationCheckBox = MakeCheckBox(
            "启用 CampPlus 说话人分离（会增加结束延迟）",
            currentConfig.EnableSpeakerDiarization);
        panel.Controls.Add(_speakerDiarizationCheckBox, 0, 5);

        panel.Controls.Add(MakeTitle("专业词库（热词）"), 0, 6);
        panel.Controls.Add(MakeHint("每行一个词，流式识别时按热词引导。"), 0, 7);
        _hotwordsTextBox = new TextBox
        {
            Multiline = true,
            Width = 420,
            Height = 170,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
            Text = currentConfig.HotwordConfig.ToMultiline()
        };
        panel.Controls.Add(_hotwordsTextBox, 0, 8);

        _alwaysOnTopCheckBox = MakeCheckBox(
            "悬浮按钮始终置顶",
            currentConfig.AlwaysOnTop);
        panel.Controls.Add(_alwaysOnTopCheckBox, 0, 9);

        panel.Controls.Add(MakeHint("悬浮球左键可开始/停止，右键打开设置或退出。"), 0, 10);

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
        panel.Controls.Add(buttons, 0, 11);

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
            EnableLiveCaption = true,
            EnableManualSemanticLlm = _manualSemanticCheckBox.Checked,
            EnableStickyDictationSemantic = false,
            EnableSpeakerDiarization = _speakerDiarizationCheckBox.Checked,
            HotwordConfig = HotwordConfig.FromMultiline(_hotwordsTextBox.Text),
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
