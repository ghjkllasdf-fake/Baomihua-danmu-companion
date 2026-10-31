using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Popcorn;

internal static class TrayIconLocatorTests
{
    internal static void Run()
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        DistributionTests.Check(typeof(NotifyIcon).GetField("_id",flags)?.FieldType==typeof(uint),"The pinned WinForms shell icon ID must remain a uint.");
        var window=typeof(NotifyIcon).GetField("_window",flags)?.FieldType;
        DistributionTests.Check(window!=null && typeof(NativeWindow).IsAssignableFrom(window),"The pinned WinForms notification window must remain available.");
        var identifier=typeof(TrayIconLocator).GetNestedType("Identifier",BindingFlags.NonPublic);
        DistributionTests.Check(Marshal.SizeOf(identifier)==(IntPtr.Size==8 ? 40 : 28),"The shell notification identifier must preserve its native ABI size.");
        DistributionTests.Check(Marshal.OffsetOf(identifier,"Window").ToInt32()==IntPtr.Size,"The shell callback handle must be pointer-aligned.");
        DistributionTests.Check(Marshal.OffsetOf(identifier,"Id").ToInt32()==IntPtr.Size*2,"The shell icon ID must follow the callback handle.");
        using var icon=new NotifyIcon();
        DistributionTests.Check(TrayIconLocator.GetBounds(icon)==null,"An unregistered icon must not supply a made-up screen position.");
        icon.Dispose();
        DistributionTests.Check(TrayIconLocator.GetBounds(icon)==null,"A disposed icon must not supply a stale screen position.");
        Console.WriteLine("PASS: pinned WinForms tray identity, shell ABI and unavailable-icon fallback.");
    }
}
