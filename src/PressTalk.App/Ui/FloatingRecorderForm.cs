using System.Drawing;
using System.Drawing.Drawing2D;
using PressTalk.Contracts.Asr;

namespace PressTalk.App.Ui;

public sealed class FloatingRecorderForm : Form
{
    private readonly CircleHotkeyButton _button;
    private readonly Label _hintLabel;
    private readonly RichTextBox _previewBox;
    private readonly ContextMenuStrip _contextMenu;
    private readonly System.Windows.Forms.Timer _waveTimer;
    private bool _dragging;
    private Point _dragOrigin;
    private bool _isRecording;
    private int _waveFrame;
    private string _baseHintText = "点击按钮设置";

    public FloatingRecorderForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        Width = 360;
        Height = 260;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;

        _button = new CircleHotkeyButton
        {
            Width = 86,
            Height = 86,
            Left = 12,
            Top = 12
        };
        _button.Click += (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(_button);

        _hintLabel = new Label
        {
            AutoSize = false,
            Width = Width - 120,
            Height = 44,
            Left = 104,
            Top = 20,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point),
            Text = _baseHintText
        };
        Controls.Add(_hintLabel);

        _previewBox = new RichTextBox
        {
            Left = 12,
            Top = 108,
            Width = Width - 24,
            Height = Height - 120,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(22, 22, 22),
            ForeColor = Color.FromArgb(232, 232, 232),
            Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point),
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Text = "实时转写会显示在这里..."
        };
        Controls.Add(_previewBox);

        _contextMenu = new ContextMenuStrip();
        var settingsItem = new ToolStripMenuItem("设置");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _contextMenu.Items.Add(settingsItem);

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            var result = MessageBox.Show(
                this,
                "确认退出 PressTalk 吗？",
                "退出确认",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
            {
                Close();
            }
        };
        _contextMenu.Items.Add(exitItem);

        _waveTimer = new System.Windows.Forms.Timer
        {
            Interval = 150
        };
        _waveTimer.Tick += (_, _) => RefreshWaveHint();

        BindRightClick(this);
        BindRightClick(_button);
        BindRightClick(_hintLabel);
        BindRightClick(_previewBox);
        BindDrag(this);
        BindDrag(_button);
        BindDrag(_hintLabel);
        BindDrag(_previewBox);
    }

    public event EventHandler? ToggleRequested;
    public event EventHandler? SettingsRequested;

    public void SetTopMost(bool topMost)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new MethodInvoker(() => SetTopMost(topMost)));
            return;
        }

        TopMost = topMost;
    }

    public void SetHotkeyText(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new MethodInvoker(() => SetHotkeyText(text)));
            return;
        }

        _button.HotkeyText = text;
        _button.Invalidate();
    }

    public void SetVisualState(bool isPressed, bool isRecording, bool isSticky)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new MethodInvoker(() => SetVisualState(isPressed, isRecording, isSticky)));
            return;
        }

        _button.IsPressed = isPressed;
        _button.IsRecording = isRecording;
        _button.IsSticky = isSticky;
        _button.Invalidate();

        _isRecording = isRecording;
        if (_isRecording)
        {
            if (!_waveTimer.Enabled)
            {
                _waveFrame = 0;
                _waveTimer.Start();
            }
        }
        else
        {
            if (_waveTimer.Enabled)
            {
                _waveTimer.Stop();
            }

            _hintLabel.Text = _baseHintText;
        }
    }

    public void SetHintText(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new MethodInvoker(() => SetHintText(text)));
            return;
        }

        _baseHintText = text;
        if (!_isRecording)
        {
            _hintLabel.Text = text;
        }
    }

    public void SetLivePreview(
        string previewText,
        string confirmedText,
        IReadOnlyList<SpeakerSegment>? speakerSegments = null)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new MethodInvoker(() => SetLivePreview(previewText, confirmedText, speakerSegments)));
            return;
        }

        var preferred = string.IsNullOrWhiteSpace(confirmedText) ? previewText : confirmedText;
        if (speakerSegments is not null && speakerSegments.Count > 0)
        {
            RenderSpeakerSegments(speakerSegments);
            return;
        }

        _previewBox.Clear();
        _previewBox.SelectionColor = Color.FromArgb(232, 232, 232);
        _previewBox.AppendText(string.IsNullOrWhiteSpace(preferred) ? "实时转写会显示在这里..." : preferred);
    }

    private void RenderSpeakerSegments(IReadOnlyList<SpeakerSegment> segments)
    {
        var palette = new[]
        {
            Color.FromArgb(126, 224, 255),
            Color.FromArgb(178, 241, 173),
            Color.FromArgb(255, 201, 123),
            Color.FromArgb(255, 152, 168),
            Color.FromArgb(216, 184, 255)
        };

        _previewBox.Clear();
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var color = palette[i % palette.Length];
            _previewBox.SelectionColor = color;
            _previewBox.AppendText($"[{segment.SpeakerId}] ");
            _previewBox.SelectionColor = Color.FromArgb(232, 232, 232);
            _previewBox.AppendText(segment.Text);
            if (i < segments.Count - 1)
            {
                _previewBox.AppendText(Environment.NewLine);
            }
        }
    }

    private void RefreshWaveHint()
    {
        if (!_isRecording)
        {
            return;
        }

        var frames = new[] { "▁▂▃▂", "▂▃▄▃", "▃▄▅▄", "▄▅▆▅" };
        _hintLabel.Text = $"{_baseHintText}  {frames[_waveFrame % frames.Length]}";
        _waveFrame++;
    }

    private void BindDrag(Control control)
    {
        control.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            _dragging = true;
            _dragOrigin = e.Location;
        };

        control.MouseMove += (_, e) =>
        {
            if (!_dragging || e.Button != MouseButtons.Left)
            {
                return;
            }

            var screenPoint = control.PointToScreen(e.Location);
            Location = new Point(screenPoint.X - _dragOrigin.X, screenPoint.Y - _dragOrigin.Y);
        };

        control.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragging = false;
            }
        };
    }

    private void BindRightClick(Control control)
    {
        control.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            _contextMenu.Show(control, e.Location);
        };
    }
}

internal sealed class CircleHotkeyButton : Control
{
    public string HotkeyText { get; set; } = "F8";
    public bool IsPressed { get; set; }
    public bool IsRecording { get; set; }
    public bool IsSticky { get; set; }

    public CircleHotkeyButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor
            | ControlStyles.UserPaint,
            true);

        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var chrome = new LinearGradientBrush(
            ClientRectangle,
            Color.FromArgb(40, 40, 40),
            Color.FromArgb(18, 18, 18),
            LinearGradientMode.Vertical);
        e.Graphics.FillRectangle(chrome, ClientRectangle);

        var bounds = ClientRectangle;
        var baseCircle = new RectangleF(4, 4, bounds.Width - 8, bounds.Height - 8);
        var depressed = IsPressed || IsSticky;
        var pressOffset = depressed ? 2.5f : 0f;
        var shadowOffset = depressed ? 1.2f : 4.8f;

        using var shadowBrush = new SolidBrush(Color.FromArgb(72, 0, 0, 0));
        var shadowCircle = baseCircle;
        shadowCircle.Offset(0, shadowOffset);
        e.Graphics.FillEllipse(shadowBrush, shadowCircle);

        var circle = baseCircle;
        circle.Offset(0, pressOffset);

        using var fillBrush = new LinearGradientBrush(
            circle,
            Color.FromArgb(252, 252, 252),
            Color.FromArgb(214, 214, 214),
            LinearGradientMode.ForwardDiagonal);
        e.Graphics.FillEllipse(fillBrush, circle);

        using var borderPen = new Pen(Color.FromArgb(236, 236, 236), 1.8f);
        e.Graphics.DrawEllipse(borderPen, circle);

        if (depressed)
        {
            using var innerShade = new Pen(Color.FromArgb(88, 0, 0, 0), 3.2f);
            var inner = RectangleF.Inflate(circle, -5f, -5f);
            e.Graphics.DrawEllipse(innerShade, inner);
        }
        else
        {
            using var topHighlight = new Pen(Color.FromArgb(210, 255, 255, 255), 2.2f);
            var highlight = RectangleF.Inflate(circle, -3f, -3f);
            e.Graphics.DrawArc(topHighlight, highlight, 208, 118);
        }

        if (IsSticky)
        {
            using var stickyRing = new Pen(Color.FromArgb(170, 255, 255, 255), 1.8f)
            {
                DashStyle = DashStyle.Dash
            };
            var ring = RectangleF.Inflate(circle, 3f, 3f);
            e.Graphics.DrawEllipse(stickyRing, ring);
        }

        if (IsRecording && !IsSticky)
        {
            using var recRing = new Pen(Color.FromArgb(130, 255, 255, 255), 1.2f);
            var ring = RectangleF.Inflate(circle, 2f, 2f);
            e.Graphics.DrawEllipse(recRing, ring);
        }

        using var font = new Font("Segoe UI Semibold", 17f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.FromArgb(54, 54, 54));
        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        e.Graphics.DrawString(HotkeyText, font, textBrush, circle, format);
    }
}
