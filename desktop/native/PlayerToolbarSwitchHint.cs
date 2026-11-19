using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Popcorn;

internal sealed class SwitchHint : Form
{
    internal const string Caption = "\u5f39\u5e55\u5f00\u5173";
    internal const int DelayMs = 400;

    internal SwitchHint()
    {
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var parameters = base.CreateParams; parameters.ExStyle |= 0x080800A0; return parameters; }
    }

    internal static Rectangle Position(Rectangle button, Rectangle viewport, uint dpi)
    {
        if (dpi == 0) return Rectangle.Empty;
        int width = Pixels(66, dpi), height = Pixels(30, dpi);
        int x = Math.Max(viewport.Left, Math.Min(viewport.Right - width, button.Left + (button.Width - width) / 2));
        var result = new Rectangle(x, button.Top - height - Pixels(6, dpi), width, height);
        return viewport.Contains(result) ? result : Rectangle.Empty;
    }

    internal bool Present(Rectangle bounds, uint dpi)
    {
        if (bounds.IsEmpty) { Hide(); return false; }
        if (Visible && Size == bounds.Size) { Location = bounds.Location; return false; }
        Bounds = bounds;
        using var bitmap = Render(bounds.Size, dpi);
        if (!LayeredGlyphs.Present(Handle, bitmap, 255)) { Hide(); return false; }
        Show();
        return true;
    }

    internal static Bitmap Render(Size size, uint dpi)
    {
        var result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(result);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float s = dpi / 96f;
        using var outline = new GraphicsPath();
        outline.AddRoundedRectangle(new RectangleF(s / 2, s / 2, size.Width - s, size.Height - s), new SizeF(4 * s, 4 * s));
        bool highContrast = SystemInformation.HighContrast;
        using var background = new SolidBrush(highContrast ? SystemColors.Info : Color.FromArgb(44, 44, 44));
        using var stroke = new Pen(highContrast ? SystemColors.InfoText : Color.FromArgb(70, 70, 70), s);
        using var ink = new SolidBrush(highContrast ? SystemColors.InfoText : PlayerToolbarUi.Ink);
        using var font = new Font("Microsoft YaHei UI", 12 * s, GraphicsUnit.Pixel);
        using var align = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.FillPath(background, outline);
        g.DrawPath(stroke, outline);
        g.DrawString(Caption, font, ink, new RectangleF(0, 0, size.Width, size.Height), align);
        return result;
    }

    private static int Pixels(int logical, uint dpi) => (int)Math.Round(logical * (dpi / 96.0));
}
