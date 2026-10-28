// Standalone .NET Framework WPF input experiment. Not a product renderer or a performance gate.
// Compile with the installed Framework64 v4 compiler and WPF references; installs nothing.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

public sealed class WpfInputDiagnostic : FrameworkElement
{
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle,string cls,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd,int command);
    private static string log;
    private static void Record(string text) { File.AppendAllText(log, DateTime.UtcNow.ToString("o")+" "+text+Environment.NewLine); }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(800, 940, 500, 140));
        dc.DrawRectangle(Brushes.White, null, new Rect(100, 100, 300, 40));
    }
    [STAThread] public static void Main(string[] args)
    {
        if(args.Length<1 || File.Exists(args[0])) throw new ArgumentException("Supply a new diagnostic log path.");
        log=Path.GetFullPath(args[0]);
        if(args.Length==2 && args[1]=="parent") { RunNativeParent(); return; }
        var window=new Window {
            Title="DWM click-through diagnostic", WindowStyle=WindowStyle.None, ResizeMode=ResizeMode.NoResize,
            AllowsTransparency=false, Background=Brushes.Transparent, ShowActivated=false, ShowInTaskbar=false,
            Topmost=true, Width=1920, Height=1080, Left=0, Top=0, Content=new WpfInputDiagnostic(), IsHitTestVisible=false
        };
        Record("foreground-before="+GetForegroundWindow().ToInt64());
        window.SourceInitialized+=delegate {
            IntPtr hwnd=new WindowInteropHelper(window).Handle;
            var source=HwndSource.FromHwnd(hwnd); source.CompositionTarget.BackgroundColor=Colors.Transparent;
            var margins=new Margins { Left=-1,Right=-1,Top=-1,Bottom=-1 };
            int result=DwmExtendFrameIntoClientArea(hwnd,ref margins);
            SetWindowLongPtr(hwnd,-20,new IntPtr(GetWindowLongPtr(hwnd,-20).ToInt64()|0x80000|0x20|0x08000000|0x80));
            bool layered=SetLayeredWindowAttributes(hwnd,0,255,2);
            Record("hwnd="+hwnd.ToInt64()+" dwm="+result+" layered="+layered+" styles="+GetWindowLongPtr(hwnd,-20).ToInt64());
            source.AddHook(delegate(IntPtr h,int message,IntPtr w,IntPtr l,ref bool handled) {
                if(message==0x21 || message==0x201 || message==0x202) Record("input-message="+message+" foreground="+GetForegroundWindow().ToInt64());
                return IntPtr.Zero;
            });
        };
        var timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(120) };
        timer.Tick+=delegate { window.Close(); };
        window.Closed+=delegate { timer.Stop(); Record("closed foreground="+GetForegroundWindow().ToInt64()); };
        window.Loaded+=delegate { Record("loaded foreground="+GetForegroundWindow().ToInt64()); timer.Start(); };
        new Application().Run(window);
    }
    private static void RunNativeParent()
    {
        Record("parent foreground-before="+GetForegroundWindow().ToInt64());
        IntPtr parent=CreateWindowEx(0x80000|0x20|0x08000000|0x8,"STATIC","DWM native parent diagnostic",0x82000000,
            0,0,1920,1080,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(parent==IntPtr.Zero) throw new InvalidOperationException("Native diagnostic parent failed.");
        try {
            bool layered=SetLayeredWindowAttributes(parent,0,255,2);
            var margins=new Margins { Left=-1,Right=-1,Top=-1,Bottom=-1 };
            int result=DwmExtendFrameIntoClientArea(parent,ref margins);
            var parameters=new HwndSourceParameters("WPF diagnostic child") {
                ParentWindow=parent, WindowStyle=0x40000000|0x10000000, Width=1920, Height=1080
            };
            using(var source=new HwndSource(parameters)) {
                source.CompositionTarget.BackgroundColor=Colors.Transparent;
                source.RootVisual=new WpfInputDiagnostic { IsHitTestVisible=false };
                Record("parent hwnd="+parent.ToInt64()+" dwm="+result+" layered="+layered+" styles="+GetWindowLongPtr(parent,-20).ToInt64());
                ShowWindow(parent,4);
                Record("parent shown foreground="+GetForegroundWindow().ToInt64());
                var frame=new DispatcherFrame();
                var timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(120) };
                timer.Tick+=delegate { timer.Stop();frame.Continue=false; };
                timer.Start();
                try { Dispatcher.PushFrame(frame); }
                finally { timer.Stop(); }
            }
        } finally { DestroyWindow(parent);Record("parent closed foreground="+GetForegroundWindow().ToInt64()); }
    }
}
