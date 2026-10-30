using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Popcorn;

internal static class WindowMaterial
{
    [StructLayout(LayoutKind.Sequential)] private struct Margins {public int Left,Right,Top,Bottom;}
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus {public byte AcLine,BatteryFlag,Percent,EnergySaver;public uint Life,FullLife;}
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window,ref Margins margins);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    private static bool AllowsTransparency()
    {
        try
        {
            object enabled=Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","EnableTransparency",1);
            return !SystemInformation.TerminalServerSession && enabled is int value && value!=0 &&
                (!GetSystemPowerStatus(out var power) || power.EnergySaver==0);
        }
        catch(Exception ex) when(ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or ExternalException){return false;}
    }
    internal static Color Fallback(string skin)=>skin switch
    {
        "glass"=>Color.FromArgb(237,243,247),"winui"=>Color.FromArgb(243,243,243),
        "studio"=>Color.FromArgb(32,34,37),_=>Color.FromArgb(239,244,249)
    };
    internal static bool WantsAcrylic(AppearanceSettings settings,bool highContrast,int windowsBuild)=>
        settings.Skin is "glass" or "winui" && !settings.ReduceTransparency && !highContrast && windowsBuild>=22621;
    internal static string Apply(Form window,Panel surface,CoreWebView2Controller controller,AppearanceSettings settings)
    {
        bool acrylic=false;
        Color fallback=Fallback(settings.Skin);
        if(window.IsHandleCreated)
        {
            try
            {
                // A tray flyout is a transient surface, not a long-lived Mica window.
                int kind=WantsAcrylic(settings,SystemInformation.HighContrast,Environment.OSVersion.Version.Build) && AllowsTransparency() && controller!=null ? 3 : 1;
                int result=DwmSetWindowAttribute(window.Handle,38,ref kind,sizeof(int));
                var margins=kind==3 && result==0 ? new Margins {Left=-1,Right=-1,Top=-1,Bottom=-1} : new Margins();
                acrylic=kind==3 && result==0 && DwmExtendFrameIntoClientArea(window.Handle,ref margins)==0;
                int dark=settings.Skin=="studio" ? 1 : 0;
                DwmSetWindowAttribute(window.Handle,20,ref dark,sizeof(int));
                int corner=settings.Skin=="studio" ? 1 : 2;
                DwmSetWindowAttribute(window.Handle,33,ref corner,sizeof(int));
            }
            catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException or ExternalException){acrylic=false;}
        }
        if(controller!=null)
        {
            try{controller.DefaultBackgroundColor=acrylic ? Color.Transparent : fallback;}
            catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or ExternalException)
            {acrylic=false;controller.DefaultBackgroundColor=fallback;}
        }
        if(!acrylic && window.IsHandleCreated)
        {
            try{int none=1;DwmSetWindowAttribute(window.Handle,38,ref none,sizeof(int));var margins=new Margins();DwmExtendFrameIntoClientArea(window.Handle,ref margins);}
            catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException or ExternalException){}
        }
        // Black client pixels carry zero alpha on an extended DWM frame. WebView2
        // must also be transparent; CSS blur alone cannot sample the desktop.
        window.BackColor=surface.BackColor=acrylic ? Color.Black : fallback;
        return acrylic ? "acrylic" : "solid";
    }
}
