using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Popcorn;

internal static class TrayIconLocator
{
    // WinForms has no public shell identifier; these fields are checked against
    // the pinned runtime, with the last tray interaction as a safe fallback.
    private static readonly FieldInfo windowField=typeof(NotifyIcon).GetField("_window",BindingFlags.Instance|BindingFlags.NonPublic);
    private static readonly FieldInfo idField=typeof(NotifyIcon).GetField("_id",BindingFlags.Instance|BindingFlags.NonPublic);
    [StructLayout(LayoutKind.Sequential)]
    private struct Identifier {public uint Size;public IntPtr Window;public uint Id;public Guid Guid;}
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect {public int Left,Top,Right,Bottom;}
    [DllImport("shell32.dll",ExactSpelling=true)]
    private static extern int Shell_NotifyIconGetRect(ref Identifier icon,out NativeRect bounds);

    internal static Rectangle? GetBounds(NotifyIcon icon)
    {
        if(icon==null || !icon.Visible || windowField?.GetValue(icon) is not NativeWindow window ||
            window.Handle==IntPtr.Zero || idField?.GetValue(icon) is not uint id)return null;
        var identifier=new Identifier {Size=(uint)Marshal.SizeOf<Identifier>(),Window=window.Handle,Id=id};
        if(Shell_NotifyIconGetRect(ref identifier,out var bounds)!=0 || bounds.Right<=bounds.Left || bounds.Bottom<=bounds.Top)return null;
        return Rectangle.FromLTRB(bounds.Left,bounds.Top,bounds.Right,bounds.Bottom);
    }
}
