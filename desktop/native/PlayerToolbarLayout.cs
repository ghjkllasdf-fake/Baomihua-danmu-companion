using System;
using System.Drawing;

namespace Popcorn;

internal static class PlayerRow
{
    internal static bool TryPlace(Rectangle client, uint dpi, out Rectangle buttons, out Rectangle popup)
    {
        buttons = popup = Rectangle.Empty;
        if (dpi == 0) return false;
        double unit = dpi / 96.0;
        int Pixel(double logical) => (int)Math.Round(logical * unit);

        // Measured at 200% DPI from the supplied player layout. Use logical units,
        // not percentages: the left group is anchored left and playback is centered.
        int dockHeight = Pixel(34);
        int rowCenter = client.Bottom - Pixel(23.5);
        var dock = new Rectangle(client.Left + Pixel(138), rowCenter - dockHeight / 2, Pixel(80), dockHeight);
        int skipClearance = client.Left + client.Width / 2 - Pixel(57);
        if (!client.Contains(dock) || dock.Right > skipClearance) return false;

        int width = Pixel(313), height = Pixel(264);
        buttons = dock;
        if (width > client.Width - 2 * Pixel(8) || height > dock.Top - client.Top - Pixel(6) - Pixel(8)) return true;
        int left = Math.Clamp(dock.Right - width, client.Left + Pixel(8), client.Right - Pixel(8) - width);
        popup = new Rectangle(left, dock.Top - Pixel(6) - height, width, height);
        return true;
    }
}
