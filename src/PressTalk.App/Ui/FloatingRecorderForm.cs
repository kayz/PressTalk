using System.Drawing;
using System.Drawing.Drawing2D;

namespace PressTalk.App.Ui;

public sealed class FloatingRecorderForm : Form
{
    private readonly CircleHotkeyButton _button;
    private readonly Label _hintLabel;
    private readonly ContextMenuStrip _contextMenu;
    private bool _dragging;
    private Point _dragOrigin;

    public FloatingRecorderForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        Width = 106;
        Height = 126;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;

        _button = new CircleHotkeyButton
        {
            Width = 86,
            Height = 86,
            Left = (Width - 86) / 2,
            Top = 6
        };
        _button.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(_button);

        _hintLabel = new Label
        {
            AutoSize = false,
            Width = Width,
            Height = 26,
            Left = 0,
            Top = 96,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Text = "点击按钮设置"
        };
        Controls.Add(_hintLabel);

        _contextMenu = new ContextMenuStrip();
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

        BindRightClick(this);
        BindRightClick(_button);
        BindRightClick(_hintLabel);
        BindDrag(this);
        BindDrag(_button);
        BindDrag(_hintLabel);
    }

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
    }

    public void SetHintText(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new MethodInvoker(() => SetHintText(text)));
            return;
        }

        _hintLabel.Text = text;
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
