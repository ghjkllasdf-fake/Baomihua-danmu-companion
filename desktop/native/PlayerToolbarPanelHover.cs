using System.Drawing;

namespace Popcorn;

internal static class PanelHover
{
    internal static bool ShouldOpen(Rectangle gear, Rectangle panel, Point cursor, bool hitsGear) =>
        hitsGear && !panel.IsEmpty && gear.Contains(cursor);
}
