using System.Drawing;
using System.Drawing.Drawing2D;

namespace PressTalk.App.Ui;

public sealed class FloatingRecorderForm : Form
{
    private readonly CircleHotkeyButton _button;
    private readonly Label _hintLabel;
    private readonly Panel _root;
    private bool _dragging;
    private Point _dragOrigin;

    public FloatingRecorderForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        Width = 132;
        Height = 152;
        BackColor = Color.FromArgb(24, 26, 33);
        ForeColor = Color.White;
        TopMost = true;
        DoubleBuffered = true;

        _root = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(24, 26, 33)
        };
        Controls.Add(_root);

        _button = new CircleHotkeyButton
        {
            Width = 108,
            Height = 108,
            Left = 12,
            Top = 10
        };
        _button.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _root.Controls.Add(_button);

        _hintLabel = new Label
        {
            AutoSize = false,
            Width = Width,
            Height = 24,
            Left = 0,
            Top = 122,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(170, 180, 195),
            Text = "Click to settings"
        };
        _root.Controls.Add(_hintLabel);

        BindDrag(_root);
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
            | ControlStyles.UserPaint,
            true);

        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var bounds = ClientRectangle;
        var circle = new RectangleF(6, 6, bounds.Width - 12, bounds.Height - 12);
        var shadowOffset = IsPressed ? 1f : 5f;
        var pressOffset = IsPressed ? 2f : 0f;

        using var shadowBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0));
        var shadowRect = circle;
        shadowRect.Offset(0, shadowOffset);
        e.Graphics.FillEllipse(shadowBrush, shadowRect);

        var fill = IsSticky
            ? Color.FromArgb(255, 173, 66)
            : (IsRecording ? Color.FromArgb(234, 67, 53) : Color.FromArgb(45, 140, 255));
        using var fillBrush = new LinearGradientBrush(
            circle,
            ControlPaint.Light(fill, 0.12f),
            ControlPaint.Dark(fill, 0.12f),
            LinearGradientMode.ForwardDiagonal);
        var circleRect = circle;
        circleRect.Offset(0, pressOffset);
        e.Graphics.FillEllipse(fillBrush, circleRect);

        using var borderPen = new Pen(Color.FromArgb(230, 255, 255, 255), 2f);
        e.Graphics.DrawEllipse(borderPen, circleRect);

        var textColor = Color.White;
        using var font = new Font("Segoe UI Semibold", 20f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(textColor);
        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        e.Graphics.DrawString(HotkeyText, font, textBrush, circleRect, format);
    }
}
