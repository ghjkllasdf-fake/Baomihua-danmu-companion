using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Popcorn;

internal static class LayeredGlyphs
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        internal uint HeaderSize;
        internal int Width, Height;
        internal ushort Planes, Bits;
        internal uint Compression, ImageSize;
        internal int PixelsPerMeterX, PixelsPerMeterY;
        internal uint ColorsUsed, ImportantColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Blend(byte Operation, byte Flags, byte Opacity, byte AlphaFormat);

    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, in BitmapHeader header, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint hwnd, nint screen, nint position, in Size size,
        nint source, in Point origin, uint key, in Blend blend, uint flags);

    internal static bool Present(nint hwnd, Bitmap pixels, byte opacity)
    {
        var dc = CreateCompatibleDC(0);
        if (dc == 0) return false;
        nint bitmap = 0, previous = 0;
        try
        {
            var size = pixels.Size;
            var header = new BitmapHeader { HeaderSize = 40, Width = size.Width, Height = -size.Height, Planes = 1, Bits = 32 };
            bitmap = CreateDIBSection(dc, header, 0, out nint bits, 0, 0);
            if (bitmap == 0) return false;
            previous = SelectObject(dc, bitmap);
            if (previous == 0 || previous == -1) return false;
            var data = pixels.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int bytes = size.Width * 4;
                var row = new byte[bytes];
                for (int y = 0; y < size.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, bytes);
                    Marshal.Copy(row, 0, bits + y * bytes, bytes);
                }
            }
            finally { pixels.UnlockBits(data); }
            return UpdateLayeredWindow(hwnd, 0, 0, size, dc, Point.Empty, 0, new Blend(0, 0, opacity, 1), 2);
        }
        finally
        {
            if (previous != 0 && previous != -1) SelectObject(dc, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }
}
