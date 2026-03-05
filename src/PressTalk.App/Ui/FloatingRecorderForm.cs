using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using PressTalk.Contracts.Asr;

namespace PressTalk.App.Ui;

public sealed class FloatingRecorderForm : Form
{
    private const int HWND_TOPMOST = -1;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, int hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly CircleHotkeyButton _button;
    private readonly Label _hintLabel;
    private readonly RichTextBox _previewBox;
    private readonly ContextMenuStrip _contextMenu;
    private readonly System.Windows.Forms.Timer _waveTimer;
    private bool _isRecording;
    private int _waveFrame;
    private bool _dragging;
    private Point _dragOrigin;
    private string _baseHintText = "点击按钮设置";

    public FloatingRecorderForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        Width = 100;
        Height = 100;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;

        _button = new CircleHotkeyButton
        {
            Width = 100,
            Height = 100,
            Left = 0,
            Top = 0
        };
        Controls.Add(_button);

        TrySetAppIconFromPng();

        _hintLabel = new Label { Visible = false };
        Controls.Add(_hintLabel);

        _previewBox = new RichTextBox { Visible = false };
        Controls.Add(_previewBox);

        _contextMenu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(45, 45, 48),
            ForeColor = Color.White,
            Renderer = new DarkMenuRenderer()
        };

        var settingsItem = new ToolStripMenuItem("⚙ 设置")
        {
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 11f)
        };
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _contextMenu.Items.Add(settingsItem);

        var exitItem = new ToolStripMenuItem("✕ 退出")
        {
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 11f)
        };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _contextMenu.Items.Add(exitItem);

        _waveTimer = new System.Windows.Forms.Timer
        {
            Interval = 150
        };
        _waveTimer.Tick += (_, _) => RefreshWaveHint();

        BindRightClick(this);
        BindRightClick(_button);
        BindDrag(this);
        BindDrag(_button);

        Activated += (_, _) => EnsureTopMostLayer();
        Shown += (_, _) => EnsureTopMostLayer();
    }

    private void TrySetAppIconFromPng()
    {
        try
        {
            var pngPath = Path.Combine(AppContext.BaseDirectory, "white.png");
            if (!File.Exists(pngPath))
            {
                return;
            }

            using var bitmap = new Bitmap(pngPath);
            var hIcon = bitmap.GetHicon();
            try
            {
                using var tempIcon = Icon.FromHandle(hIcon);
                Icon = (Icon)tempIcon.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch
        {
            // Ignore icon load failures.
        }
    }

    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public void SetTopMost(bool topMost)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new MethodInvoker(() => SetTopMost(topMost)));
            return;
        }

        TopMost = topMost;
        if (topMost)
        {
            EnsureTopMostLayer();
        }
    }

    private void EnsureTopMostLayer()
    {
        if (Handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (TopMost)
        {
            EnsureTopMostLayer();
        }
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
            Invoke(new MethodInvoker(() => SetVisualState(isPressed, isRecording, isSticky)));
            return;
        }

        _button.IsPressed = isPressed;
        _button.IsRecording = isRecording;
        _button.IsSticky = isSticky;
        _button.Invalidate();
        _button.Update();

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

        var preferred = string.IsNullOrWhiteSpace(previewText) ? confirmedText : previewText;
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
            Left = screenPoint.X - _dragOrigin.X;
            Top = screenPoint.Y - _dragOrigin.Y;
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

    private float _pulsePhase;
    private readonly System.Windows.Forms.Timer _animTimer;
    private readonly Random _random = new Random();
    private float[] _waveHeights = new float[5];

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

        for (int i = 0; i < _waveHeights.Length; i++)
        {
            _waveHeights[i] = 0.3f + (float)_random.NextDouble() * 0.4f;
        }

        _animTimer = new System.Windows.Forms.Timer { Interval = 80 };
        _animTimer.Tick += (_, _) =>
        {
            if (IsRecording)
            {
                _pulsePhase += 0.15f;
                for (int i = 0; i < _waveHeights.Length; i++)
                {
                    _waveHeights[i] = 0.2f + (float)_random.NextDouble() * 0.6f;
                }
                Invalidate();
            }
        };
        _animTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        var bounds = ClientRectangle;
        var centerX = bounds.Width / 2f;
        var centerY = bounds.Height / 2f;
        var radius = Math.Min(bounds.Width, bounds.Height) / 2f - 12;

        var mainCircle = new RectangleF(centerX - radius, centerY - radius, radius * 2, radius * 2);

        using var bgBrush = new LinearGradientBrush(
            mainCircle,
            Color.FromArgb(255, 255, 255),
            Color.FromArgb(220, 220, 220),
            LinearGradientMode.Vertical);
        e.Graphics.FillEllipse(bgBrush, mainCircle);

        using var outerBorderPen = new Pen(Color.FromArgb(180, 180, 180), 1.5f);
        e.Graphics.DrawEllipse(outerBorderPen, mainCircle);

        var highlightCircle = new RectangleF(
            centerX - radius * 0.85f,
            centerY - radius * 0.95f,
            radius * 1.7f,
            radius * 1.3f);

        using var highlightBrush = new LinearGradientBrush(
            highlightCircle,
            Color.FromArgb(200, 255, 255, 255),
            Color.FromArgb(0, 255, 255, 255),
            LinearGradientMode.Vertical);

        using var mainPath = new GraphicsPath();
        mainPath.AddEllipse(mainCircle);
        e.Graphics.SetClip(mainPath);
        e.Graphics.FillEllipse(highlightBrush, highlightCircle);
        e.Graphics.ResetClip();

        var bottomShadowCircle = new RectangleF(
            centerX - radius * 0.7f,
            centerY + radius * 0.3f,
            radius * 1.4f,
            radius * 0.8f);

        using var bottomShadowBrush = new LinearGradientBrush(
            bottomShadowCircle,
            Color.FromArgb(0, 0, 0, 0),
            Color.FromArgb(50, 0, 0, 0),
            LinearGradientMode.Vertical);

        e.Graphics.SetClip(mainPath);
        e.Graphics.FillEllipse(bottomShadowBrush, bottomShadowCircle);
        e.Graphics.ResetClip();

        if (IsRecording)
        {
            DrawMicrophoneWithWaves(e.Graphics, centerX, centerY, radius * 0.66f);
        }
        else
        {
            using var font = new Font("Microsoft YaHei UI", radius * 0.68f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.FromArgb(80, 80, 80));
            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            e.Graphics.DrawString(HotkeyText, font, textBrush, centerX, centerY, format);
        }
    }

    private void DrawMicrophoneWithWaves(Graphics g, float cx, float cy, float size)
    {
        using var micPen = new Pen(Color.FromArgb(80, 80, 80), size * 0.14f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        var bodyRect = new RectangleF(cx - size * 0.25f, cy - size * 0.5f, size * 0.5f, size * 0.7f);
        g.DrawArc(micPen, bodyRect, 0, 180);
        g.DrawLine(micPen, bodyRect.Left, bodyRect.Top + bodyRect.Height * 0.5f, bodyRect.Left, bodyRect.Bottom);
        g.DrawLine(micPen, bodyRect.Right, bodyRect.Top + bodyRect.Height * 0.5f, bodyRect.Right, bodyRect.Bottom);

        var standTop = cy + size * 0.3f;
        g.DrawLine(micPen, cx, bodyRect.Bottom, cx, standTop);
        g.DrawLine(micPen, cx - size * 0.3f, standTop, cx + size * 0.3f, standTop);

        using var wavePen = new Pen(Color.FromArgb(120, 100, 150, 255), size * 0.08f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        float waveSpacing = size * 0.15f;
        float waveBaseY = cy - size * 0.1f;

        for (int i = 0; i < _waveHeights.Length; i++)
        {
            float x = cx - size * 0.5f + i * waveSpacing;
            float waveHeight = size * 0.4f * _waveHeights[i];
            g.DrawLine(wavePen, x, waveBaseY - waveHeight / 2, x, waveBaseY + waveHeight / 2);
        }
    }
}

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item.Selected)
        {
            using var brush = new SolidBrush(Color.FromArgb(62, 62, 66));
            e.Graphics.FillRectangle(brush, e.Item.ContentRectangle);
        }
    }
}

internal sealed class DarkColorTable : ProfessionalColorTable
{
    public override Color MenuItemSelected => Color.FromArgb(62, 62, 66);
    public override Color MenuItemBorder => Color.FromArgb(62, 62, 66);
    public override Color MenuBorder => Color.FromArgb(60, 60, 60);
    public override Color ImageMarginGradientBegin => Color.FromArgb(45, 45, 48);
    public override Color ImageMarginGradientMiddle => Color.FromArgb(45, 45, 48);
    public override Color ImageMarginGradientEnd => Color.FromArgb(45, 45, 48);
}
