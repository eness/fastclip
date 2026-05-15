using System.Drawing;
using System.Windows.Forms;

namespace FastClip.Controls;

internal sealed class AspectRatioIconButton : Control
{
    public bool IsLinked { get; set; }

    public AspectRatioIconButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.Selectable,
            true);

        Cursor = Cursors.Hand;
        BackColor = SystemColors.Control;
        Size = new Size(28, 28);
        TabStop = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var iconColor = IsLinked
            ? Color.FromArgb(34, 160, 74)
            : Color.FromArgb(150, 184, 184, 184);
        var borderColor = IsLinked
            ? Color.FromArgb(190, 219, 234, 201)
            : Color.FromArgb(220, 225, 225, 225);

        using var borderPen = new Pen(borderColor);
        using var iconPen = new Pen(iconColor, 1.8f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };

        var bounds = ClientRectangle;
        bounds.Inflate(-1, -1);
        var backgroundColor = Parent?.BackColor ?? SystemColors.Control;
        using var backgroundBrush = new SolidBrush(backgroundColor);
        using var buttonBrush = new SolidBrush(Color.FromArgb(250, backgroundColor));
        e.Graphics.FillRectangle(backgroundBrush, ClientRectangle);
        e.Graphics.FillRectangle(buttonBrush, bounds);
        e.Graphics.DrawRectangle(borderPen, bounds);

        DrawChainIcon(e.Graphics, iconPen, bounds);

        if (Focused)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, bounds);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private static void DrawChainIcon(Graphics graphics, Pen pen, Rectangle bounds)
    {
        var centerY = bounds.Top + (bounds.Height / 2f);
        var leftLink = new RectangleF(bounds.Left + 4f, centerY - 4.5f, 8f, 9f);
        var rightLink = new RectangleF(bounds.Left + 15f, centerY - 4.5f, 8f, 9f);

        graphics.DrawArc(pen, leftLink, 45, 270);
        graphics.DrawArc(pen, rightLink, 225, 270);
        graphics.DrawLine(pen, bounds.Left + 10.5f, centerY, bounds.Left + 16.5f, centerY);
    }
}
