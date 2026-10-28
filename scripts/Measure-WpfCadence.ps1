#requires -Version 7.6
param(
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateRange(5,120)][int]$Seconds=10,
    [ValidateRange(1,100)][int]$Images=1,
    [switch]$Transparent,
    [switch]$Retained,
    [switch]$DwmGlass,
    [switch]$LayeredClickThrough,
    [switch]$NativeParent
)
$ErrorActionPreference='Stop'
if([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA'){throw 'Use the bundled PS7 with -STA.'}
if(Test-Path -LiteralPath $OutputPath){throw 'Choose a new diagnostic output file.'}
foreach($name in @('System.Windows.Forms','PresentationFramework','PresentationCore','WindowsBase')){Add-Type -AssemblyName $name}
[void][System.Windows.Forms.Application]::SetHighDpiMode([System.Windows.Forms.HighDpiMode]::PerMonitorV2)
$references=@(Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll'|ForEach-Object FullName)
$references+=@(Get-ChildItem $PSHOME -Filter '*.dll'|Where-Object {$_.Name -match '^(Presentation(Core|Framework)|WindowsBase|System\.Windows|System\.Drawing|Microsoft\.Win32\.SystemEvents)'}|ForEach-Object FullName)
$source=@'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;

public sealed class WpfCadenceDiagnostic : FrameworkElement
{
    [StructLayout(LayoutKind.Sequential)] private struct Margins {public int Left,Right,Top,Bottom;}
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd,ref Margins margins);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW",SetLastError=true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd,int index,IntPtr value);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd,uint key,byte alpha,uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle,string cls,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd,int command);
    private readonly BitmapSource image;
    private readonly int count;
    private readonly bool transparent;
    private readonly bool retained;
    private readonly VisualCollection children;
    private readonly List<TranslateTransform> offsets=new();
    private readonly double started=Now;
    private readonly List<double> callbacks=new(),targets=new();
    private double last=-1,lastTarget=-1;
    private int renders;
    private static double Now=>Stopwatch.GetTimestamp()*1000.0/Stopwatch.Frequency;
    private WpfCadenceDiagnostic(int count,bool transparent,bool retained)
    {
        this.count=count;this.transparent=transparent;this.retained=retained;children=new VisualCollection(this);
        var pixels=new byte[200*40*4];Array.Fill(pixels,(byte)255);
        image=BitmapSource.Create(200,40,96,96,PixelFormats.Pbgra32,null,pixels,200*4);image.Freeze();
        if(retained)for(int i=0;i<count;i++)
        {
            var visual=new DrawingVisual();using(var dc=visual.RenderOpen())dc.DrawImage(image,new Rect(0,0,200,40));
            var offset=new TranslateTransform(0,i%20*48);offsets.Add(offset);visual.Transform=offset;children.Add(visual);
        }
    }
    protected override int VisualChildrenCount=>children.Count;
    protected override Visual GetVisualChild(int index)=>children[index];
    protected override void OnRender(DrawingContext dc)
    {
        renders++;if(!transparent)dc.DrawRectangle(Brushes.Black,null,new Rect(0,0,ActualWidth,ActualHeight));
        double position=(Now-started)/100;
        if(!retained)for(int i=0;i<count;i++)dc.DrawImage(image,new Rect((i*211+position)%Math.Max(1,ActualWidth),i%20*48,200,40));
    }
    private void Rendering(object sender,EventArgs args)
    {
        double now=Now,target=((RenderingEventArgs)args).RenderingTime.TotalMilliseconds;
        if(last>=0)callbacks.Add(now-last);last=now;
        if(lastTarget>=0)targets.Add(target-lastTarget);lastTarget=target;
        if(retained)for(int i=0;i<count;i++)offsets[i].X=(i*211+(now-started)/100)%Math.Max(1,ActualWidth);
        InvalidateVisual();
    }
    private static object Stats(IEnumerable<double> source)
    {
        var values=source.Skip(120).OrderBy(value=>value).ToArray();
        return new{count=values.Length,p95=values.Length>0 ? values[(int)Math.Floor((values.Length-1)*.95)] : 0,
            p99=values.Length>0 ? values[(int)Math.Floor((values.Length-1)*.99)] : 0,
            zero=values.Count(value=>value==0)};
    }
    public static void Run(string path,int seconds,int images,bool transparent,bool retained,bool dwmGlass,bool layeredClickThrough,bool nativeParent)
    {
        if(nativeParent) { RunNativeParent(path,seconds,images,retained);return; }
        var surface=new WpfCadenceDiagnostic(images,transparent,retained);
        var window=new Window{Title="WPF cadence diagnostic - synthetic",WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,Topmost=true,
            ShowActivated=!layeredClickThrough,ShowInTaskbar=!layeredClickThrough,
            AllowsTransparency=transparent && !dwmGlass,Background=transparent ? Brushes.Transparent : Brushes.Black,Width=1920,Height=1080,Content=surface};
        int dwmResult=0;bool layeredResult=false;long foregroundBefore=GetForegroundWindow().ToInt64();
        if(dwmGlass)window.SourceInitialized+=(_,_)=>{
            IntPtr hwnd=new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor=Colors.Transparent;
            var margins=new Margins{Left=-1,Right=-1,Top=-1,Bottom=-1};dwmResult=DwmExtendFrameIntoClientArea(hwnd,ref margins);
            if(layeredClickThrough){
                SetWindowLongPtr(hwnd,-20,new IntPtr(GetWindowLongPtr(hwnd,-20).ToInt64()|0x80000|0x20|0x08000000|0x80));
                layeredResult=SetLayeredWindowAttributes(hwnd,0,255,2);
            }
        };
        var pump=new DispatcherTimer(DispatcherPriority.Render){Interval=TimeSpan.FromMilliseconds(16)};
        pump.Tick+=(_,_)=>surface.InvalidateVisual();
        var close=new DispatcherTimer{Interval=TimeSpan.FromSeconds(seconds)};
        close.Tick+=(_,_)=>window.Close();
        var frame=new DispatcherFrame();
        CompositionTarget.Rendering+=surface.Rendering;
        window.Closed+=(_,_)=>{pump.Stop();close.Stop();CompositionTarget.Rendering-=surface.Rendering;frame.Continue=false;};
        window.Show();IntPtr nativeHandle=new WindowInteropHelper(window).Handle;
        bool nativeVisibleAtShow=IsWindowVisible(nativeHandle),nativeMinimizedAtShow=IsIconic(nativeHandle);
        long foregroundAfterShow=GetForegroundWindow().ToInt64(),extendedStyles=GetWindowLongPtr(nativeHandle,-20).ToInt64();
        pump.Start();close.Start();
        try{Dispatcher.PushFrame(frame);}
        finally{pump.Stop();close.Stop();CompositionTarget.Rendering-=surface.Rendering;if(window.IsVisible)window.Close();}
        var result=new{seconds,images,transparent,retained,dwmGlass,dwmResult,layeredClickThrough,layeredResult,extendedStyles,foregroundBefore,foregroundAfterShow,nativeHandle=nativeHandle.ToInt64(),nativeVisibleAtShow,nativeMinimizedAtShow,width=surface.ActualWidth,height=surface.ActualHeight,renders=surface.renders,
            tier=RenderCapability.Tier>>16,callback=Stats(surface.callbacks),estimatedTarget=Stats(surface.targets),
            rawCallbacks=surface.callbacks,rawEstimatedTargets=surface.targets,
            scope="Simple own WPF window; callback and estimated animation target times, not presentation or application acceptance."};
        using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        JsonSerializer.Serialize(output,result);
    }
    private static void RunNativeParent(string path,int seconds,int images,bool retained)
    {
        var surface=new WpfCadenceDiagnostic(images,true,retained);
        long foregroundBefore=GetForegroundWindow().ToInt64();
        IntPtr parent=CreateWindowEx(0x80000|0x20|0x08000000|0x8,"STATIC","WPF native parent cadence diagnostic",0x82000000,
            0,0,1920,1080,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(parent==IntPtr.Zero) throw new InvalidOperationException("Native diagnostic parent failed.");
        int dwmResult=0;bool layeredResult=false;long foregroundAfterShow=0;
        try {
            layeredResult=SetLayeredWindowAttributes(parent,0,255,2);
            var margins=new Margins{Left=-1,Right=-1,Top=-1,Bottom=-1};dwmResult=DwmExtendFrameIntoClientArea(parent,ref margins);
            var parameters=new HwndSourceParameters("WPF diagnostic child"){
                ParentWindow=parent,WindowStyle=0x40000000|0x10000000,Width=1920,Height=1080
            };
            using var source=new HwndSource(parameters);
            source.CompositionTarget.BackgroundColor=Colors.Transparent;source.RootVisual=surface;
            var frame=new DispatcherFrame();
            var pump=new DispatcherTimer(DispatcherPriority.Render){Interval=TimeSpan.FromMilliseconds(16)};
            pump.Tick+=(_,_)=>surface.InvalidateVisual();
            var close=new DispatcherTimer{Interval=TimeSpan.FromSeconds(seconds)};
            close.Tick+=(_,_)=>frame.Continue=false;
            CompositionTarget.Rendering+=surface.Rendering;
            try {
                ShowWindow(parent,4);foregroundAfterShow=GetForegroundWindow().ToInt64();
                pump.Start();close.Start();Dispatcher.PushFrame(frame);
            } finally {pump.Stop();close.Stop();CompositionTarget.Rendering-=surface.Rendering;}
            var result=new{seconds,images,nativeParent=true,retained,dwmResult,layeredResult,foregroundBefore,foregroundAfterShow,
                nativeHandle=parent.ToInt64(),nativeVisible=IsWindowVisible(parent),extendedStyles=GetWindowLongPtr(parent,-20).ToInt64(),
                width=surface.ActualWidth,height=surface.ActualHeight,renders=surface.renders,tier=RenderCapability.Tier>>16,
                callback=Stats(surface.callbacks),estimatedTarget=Stats(surface.targets),rawCallbacks=surface.callbacks,rawEstimatedTargets=surface.targets,
                scope="Native layered parent, opaque WPF child with DWM transparency; callbacks only, not product acceptance or presentation."};
            using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
            JsonSerializer.Serialize(output,result);
        } finally {DestroyWindow(parent);}
    }
}
'@
Add-Type -TypeDefinition $source -ReferencedAssemblies ($references|Select-Object -Unique)
[WpfCadenceDiagnostic]::Run([IO.Path]::GetFullPath($OutputPath),$Seconds,$Images,[bool]$Transparent,[bool]$Retained,[bool]$DwmGlass,[bool]$LayeredClickThrough,[bool]$NativeParent)
$result=Get-Content -LiteralPath $OutputPath -Raw|ConvertFrom-Json
$result|Select-Object seconds,images,transparent,retained,dwmGlass,dwmResult,layeredClickThrough,layeredResult,extendedStyles,foregroundBefore,foregroundAfterShow,nativeHandle,nativeVisibleAtShow,nativeMinimizedAtShow,width,height,renders,tier,callback,estimatedTarget|ConvertTo-Json -Depth 3
