using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Popcorn;

public sealed class DesktopWindow : Form
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive=true, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };
    private readonly Panel surface = new() { Dock=DockStyle.Fill };
    private CoreWebView2Controller controller;
    private CoreWebView2 core;
    private readonly ApiClient api;
    private readonly AutomaticMatcher automaticMatcher;
    private readonly EmbeddedApiService localApi;
    private readonly string localSourcesFile;
    private bool localSourcesChanging;
    private readonly DesktopTray tray;
    private readonly TrayFlyout flyout;
    private readonly StartupRegistration startup;
    private readonly Icon applicationIcon;
    private readonly System.Windows.Forms.Timer timer = new() { Interval=200 };
    private readonly string root, preferences, apiPreferences, appearancePreferences;
    private AppearanceSettings appearanceSettings=new();
    private string appearanceNotice="",backdrop="solid";
    private long appearanceRevision;
    public AppearanceSnapshot Appearance=>new(appearanceSettings.Skin,appearanceSettings.ReduceTransparency,appearanceRevision,backdrop,appearanceNotice);
    private string savedApiBase="",apiNotice="";
    private CancellationTokenSource pending = new();
    private int revision;
    private long documentEpoch,stateSequence,mediaRevision,settingsRevision;
    private long presentationTicket,manualNoticeTicket,matchOperation=-1;
    private string apiBase;
    private volatile bool closing;
    private int playerUpdateQueued;
    private readonly PlayerProbe probe;
    private readonly OverlayHost overlay;
    private readonly SyncClock sync=new();
    private PlayerSample sample;
    private string lastMedia="";
    private string commentSourceTitle="";
    private readonly string browserData;
    private bool initialized,openRequested,exitRequested;
    public bool Ready { get; private set; }
    public string Failure { get; private set; } = "";
    internal object LocalApiDiagnostics=>localApi==null ? null : new {localApi.View.Enabled,localApi.View.State,localApi.View.Message,localApi.View.Sources,ProcessId=localApi.ProcessId};
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool SuppressErrorDialogs { get; set; }
    public DanmakuSettings Settings { get; private set; } = new();
    public int CommentCount { get; private set; }
    public event Action<Comment[]> CommentsLoaded;
    public event Action<DanmakuSettings> SettingsUpdated;
    public event Action<double,bool> ManualClockUpdated;

    public DesktopWindow(string projectRoot,string initialApi,string dataProfile="app",string userDataRoot=null)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(dataProfile,@"^[a-z0-9][a-z0-9-]{0,47}$")) throw new ArgumentException("Invalid data profile.");
        root=projectRoot; apiBase=initialApi;
        applicationIcon=AppIcons.Load(root);Icon=applicationIcon;
        Text="爆米花 · 弹幕面板"; ClientSize=new Size(380,540);FormBorderStyle=FormBorderStyle.None;
        ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;BackColor=Color.FromArgb(245,243,235);
        var paths=new ApplicationPaths(root,dataProfile,userDataRoot);
        string data=paths.ProfileRoot; Directory.CreateDirectory(data);browserData=paths.BrowserData;
        preferences=paths.Preferences;
        appearancePreferences=paths.AppearancePreferences;
        var savedAppearance=AppearancePreferences.Load(appearancePreferences);
        appearanceSettings=savedAppearance.Settings;appearanceNotice=savedAppearance.Notice;
        BackColor=surface.BackColor=WindowMaterial.Fallback(appearanceSettings.Skin);
        localSourcesFile=Path.Combine(data,"embedded-api","sources.json");
        if(EmbeddedApiService.IsPackaged(paths)){localApi=new EmbeddedApiService(paths);apiBase="";localApi.Changed+=QueueLocalApiUpdate;}
        api=new ApiClient(useProxy:localApi==null);
        var savedPreferences=SettingsPreferences.Load(preferences);
        Settings=savedPreferences.Settings; Failure=savedPreferences.Notice;
        apiPreferences=paths.ApiPreferences;
        if(localApi==null)
        {
            var savedApi=ApiPreferences.Load(apiPreferences);
            savedApiBase=savedApi.ApiBase;apiNotice=savedApi.Notice;
            if(string.IsNullOrEmpty(apiBase))apiBase=savedApiBase;
        }
        Controls.Add(surface);
        surface.Resize+=(_,_)=>UpdateBrowserPresentation();
        VisibleChanged+=(_,_)=>UpdateBrowserPresentation();
        Resize+=(_,_)=>UpdateBrowserPresentation();
        LocationChanged+=(_,_)=>UpdateBrowserPresentation();
        overlay=new OverlayHost(); overlay.SetSettings(Settings);
        probe=new PlayerProbe(root); probe.SampleUpdated+=QueuePlayerUpdate; sample=probe.Latest;
        startup=new StartupRegistration(root);
        tray=new DesktopTray(root,OpenWorkbench,RequestExit,(anchor,wasVisible)=>{if(wasVisible || Visible)HideWorkbench();else OpenWorkbench();},()=>Visible);
        flyout=new TrayFlyout(this,()=>tray.MenuVisible);
        automaticMatcher=new AutomaticMatcher(api.FetchAsync,ApplyAutomaticMatch,OnAutomaticState,message=>tray.ShowFailure(message),localApi==null ? 0 : 3);
        CommentsLoaded+=comments=>overlay.SetComments(comments,presentationTicket);
        SettingsUpdated+=overlay.SetSettings;
        ManualClockUpdated+=(position,playing)=>sync.Manual(position,playing,Monotonic.Now);
        timer.Tick+=(_,_)=>{ UpdatePlayer(); PushState(); };
        FormClosed+=(_,_)=>Cleanup();
    }
    private void Cleanup()
    {
        if(closing)return;
        closing=true;timer.Stop();timer.Dispose();automaticMatcher?.Dispose();flyout?.Dispose();tray?.Dispose();
        if(localApi!=null){localApi.Changed-=QueueLocalApiUpdate;localApi.Dispose();}
        pending.Cancel();pending.Dispose();api.Dispose();
        if(probe!=null){probe.SampleUpdated-=QueuePlayerUpdate;probe.Dispose();}
        overlay?.Dispose();controller?.Close();
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing)Cleanup();
        base.Dispose(disposing);
        if(disposing)applicationIcon?.Dispose();
    }
    protected override void SetVisibleCore(bool value)
    {
        if(value && !initialized)
        {
            initialized=true;
            if(!IsHandleCreated)CreateHandle();
            BeginInvoke(new Action(async ()=>await InitializeAsync(browserData)));
        }
        base.SetVisibleCore(value && openRequested);
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if(!exitRequested && e.CloseReason==CloseReason.UserClosing){e.Cancel=true;HideWorkbench();}
        base.OnFormClosing(e);
    }
    protected override bool ProcessCmdKey(ref Message message,Keys keys)
    {
        if(keys==Keys.Escape){HideWorkbench();return true;}
        return base.ProcessCmdKey(ref message,keys);
    }
    public void OpenWorkbench()
    {
        if(closing || IsDisposed)return;
        openRequested=true;
        var screen=Screen.PrimaryScreen;
        flyout.Open(tray.Anchor??new Point(screen.Bounds.Right-24,screen.Bounds.Bottom-1));
        UpdateBrowserPresentation();
    }
    public void HideWorkbench(){if(!closing && !IsDisposed)flyout.Hide();}
    private void UpdateBrowserPresentation()
    {
        if(closing || controller==null)return;
        controller.Bounds=surface.ClientRectangle;
        // A controller created under a hidden parent remains hidden until the
        // host explicitly changes IsVisible. The loaded DOM alone is not proof of paint.
        // https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2controller.isvisible
        controller.IsVisible=Visible && WindowState!=FormWindowState.Minimized;
        if(controller.IsVisible)controller.NotifyParentWindowPositionChanged();
    }
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if((message.Msg==0x001A || message.Msg==0x031A) && !closing && controller!=null)
        {
            backdrop=WindowMaterial.Apply(this,surface,controller,appearanceSettings);
            PushState();
        }
    }
    public void RequestExit()
    {
        if(closing || IsDisposed)return;
        exitRequested=true;Close();
    }
    private static void ConfigureBrowserSettings(CoreWebView2Settings settings)
    {
        settings.AreDefaultContextMenusEnabled=false;
        settings.IsStatusBarEnabled=false;
        settings.AreDevToolsEnabled=false;
        settings.AreHostObjectsAllowed=false;
        settings.IsGeneralAutofillEnabled=false;
        settings.IsPasswordAutosaveEnabled=false;
    }
    private static string DescribeInitializationFailure(Exception error) => error is WebView2RuntimeNotFoundException
        ? "包内 WebView2 运行环境缺失或不可用。\r\n请完整解压或重新安装本版本，无需另外安装 PowerShell 或 .NET。"
        : "桌面界面初始化失败："+error.Message+"\r\n请检查发行包是否完整解压，或重新安装本版本。";
    private void ApplyAutomaticMatch(AutomaticMatchResult result)
    {
        if(closing)return;
        UpdatePlayer();
        if(closing || result.MediaRevision!=mediaRevision || result.Title!=sample.Title || result.EpisodeNumber!=sample.EpisodeNumber)return;
        CommentCount=result.Comments.Length;commentSourceTitle=result.SourceTitle;CommentsLoaded?.Invoke(result.Comments);
    }
    private void OnAutomaticState(AutomaticMatchStatus status)
    {
        if(closing)return;
        if(matchOperation!=automaticMatcher.OperationId){matchOperation=automaticMatcher.OperationId;presentationTicket++;}
        string message=status.State=="matching" && !status.Message.StartsWith("搜索弹幕源",StringComparison.Ordinal) ? "搜索弹幕源" : status.Message;
        overlay.SetStatus(presentationTicket,status.State,message);PushState();
    }
    private void QueuePlayerUpdate()
    {
        if(closing || IsDisposed || !IsHandleCreated || Interlocked.Exchange(ref playerUpdateQueued,1)!=0) return;
        try { BeginInvoke(new Action(DrainPlayerUpdate)); }
        catch(InvalidOperationException) { Interlocked.Exchange(ref playerUpdateQueued,0); }
    }
    private void DrainPlayerUpdate()
    {
        Interlocked.Exchange(ref playerUpdateQueued,0);
        if(closing || IsDisposed) return;
        UpdatePlayer(); PushState();
    }
    private void UpdatePlayer()
    {
        sample=probe.Latest; double now=Monotonic.Now;
        string media=sample.Title.Length>0 ? sample.Title+"#"+sample.EpisodeNumber : lastMedia;
        if (sample.Title.Length>0 && lastMedia!=media)
        {
            mediaRevision++;
            overlay.SetStatus(++presentationTicket,"waiting","");
            sync.BeginMediaEpoch(now);
            pending.Cancel(); revision++; CommentCount=0; commentSourceTitle=""; overlay.SetComments(Array.Empty<Comment>());
        }
        if (sample.Title.Length>0) lastMedia=media;
        bool identified=sample.Window!=null && !string.IsNullOrWhiteSpace(sample.Title);
        sync.Observe(identified ? sample.PositionMs : null,identified ? sample.Playing : null,sample.StampMs,now,media,sample.PositionResolutionMs); sync.Tick(now);
        var video=identified ? sample.Window : null;
        overlay.Update(video,sync.At(now),sync.Running,sync.SeekRevision,sync.Mode=="automatic",sync.LastProofMs,sync.Mode is "automatic" or "manual");
        automaticMatcher?.Observe(apiBase,identified && (localApi==null || apiBase.Length>0) ? sample.Title : "",sample.EpisodeNumber,mediaRevision);
    }
    private void OnNavigationStarting(object sender,CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsAppSource(e.Uri)) { e.Cancel=true; return; }
        // NavigationStarting excludes fragment changes, but includes same-origin document reloads.
        // https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/navigation-events
        if(manualNoticeTicket>0 && manualNoticeTicket==presentationTicket)
            overlay.SetStatus(++presentationTicket,"manual","");
        manualNoticeTicket=0;
        documentEpoch++; Ready=false; pending.Cancel(); revision++;
    }
    private async Task InitializeAsync(string data)
    {
        try
        {
            if(localApi!=null)
            {
                var selection=EmbeddedApiSources.Load(localSourcesFile);
                if(selection.Notice.Length>0)Failure=selection.Notice;
                await localApi.StartAsync(selection.Sources);
                if(closing)return;
                apiBase=localApi.ApiBase;
            }
            SharedBrowserRuntime.ConfigureLoader(root);
            var env=await CoreWebView2Environment.CreateAsync(null,data,SharedBrowserRuntime.Options);
            if (closing) return;
            controller=await env.CreateCoreWebView2ControllerAsync(surface.Handle);
            if (closing) { controller.Close(); return; }
            backdrop=WindowMaterial.Apply(this,surface,controller,appearanceSettings);
            UpdateBrowserPresentation();
            core=controller.CoreWebView2;
            ConfigureBrowserSettings(core.Settings);
            core.SetVirtualHostNameToFolderMapping("danmu.local",Path.Combine(root,"ui","dist"),CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting+=OnNavigationStarting;
            core.FrameNavigationStarting+=(_,e)=>e.Cancel=true;
            core.NewWindowRequested+=(_,e)=>e.Handled=true;
            core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            core.DownloadStarting+=(_,e)=>e.Cancel=true;
            core.WebMessageReceived+=OnMessage;
            core.Navigate("https://danmu.local/index.html");
            timer.Start();
        }
        catch (Exception ex) { Failure=DescribeInitializationFailure(ex); if(!SuppressErrorDialogs)MessageBox.Show(Failure,"爆米花弹幕助手",MessageBoxButtons.OK,MessageBoxIcon.Error); RequestExit(); }
    }
    public static bool IsAppSource(string source) => Uri.TryCreate(source,UriKind.Absolute,out var uri) && uri.Scheme=="https" && uri.Host=="danmu.local" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);
    private void Send(object message)
    {
        if (!closing && core!=null && IsAppSource(core.Source)) core.PostWebMessageAsJson(JsonSerializer.Serialize(message,Json));
    }
    private void PushState()
    {
        tray?.Update(sample.Title,sample.EpisodeNumber,commentSourceTitle,CommentCount);
        if (!Ready || closing) return;
        long sequence=++stateSequence;
        double now=Monotonic.Now; sync.Tick(now);
        Send(new { type="state", sequence, data=new { settings=Settings,settingsRevision,
            player=PlaybackSnapshot.Capture(sample,sync,now,sequence,mediaRevision),
            commentCount=CommentCount, notice=Failure,automaticMatch=automaticMatcher?.Status,localApi=localApi?.View,appearance=Appearance } });
    }
    private void QueueLocalApiUpdate()
    {
        if(closing || IsDisposed || !IsHandleCreated)return;
        try{BeginInvoke(new Action(()=>
        {
            if(closing)return;
            string next=localApi.ApiBase;
            if(apiBase!=next)
            {
                pending.Cancel();revision++;automaticMatcher.Suspend();apiBase=next;
                CommentCount=0;commentSourceTitle="";CommentsLoaded?.Invoke(Array.Empty<Comment>());
            }
            UpdatePlayer();PushState();
        }));}catch(InvalidOperationException){}
    }
    private async void OnMessage(object sender,CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsAppSource(e.Source) || closing) return;
        long epoch=documentEpoch;
        string id="", type="", commandCode="command-failed";
        long requestNoticeTicket=0;
        bool settingsCommitted=false,appearanceCommitted=false;
        try
        {
            string raw=e.WebMessageAsJson;
            if (raw.Length>65536) throw new ArgumentException("消息过大。");
            using var doc=JsonDocument.Parse(raw);
            var msg=doc.RootElement;
            id=msg.GetProperty("id").GetString()??"";
            if (id.Length is <1 or >64 || msg.GetProperty("v").GetInt32()!=1) throw new ArgumentException("不支持的消息协议。");
            type=msg.GetProperty("type").GetString()??"";
            var payload=msg.GetProperty("payload");
            object result;
            if (type=="hello") { Ready=true; result=new { apiBase, apiSaved=localApi==null && apiBase.Length>0 && apiBase==savedApiBase,apiNotice, settings=Settings, settingsRevision, stateSequence, protocolVersion=1, capabilities=localApi==null ? new[] { "media-context-v1","automatic-match-v1","desktop-lifecycle-v1","tray-flyout-v1","api-persistence-v1","appearance-v1" } : new[] { "media-context-v1","automatic-match-v1","desktop-lifecycle-v1","tray-flyout-v1","api-persistence-v1","embedded-api-v1","appearance-v1" },localApi=localApi?.View,appearance=Appearance }; }
            else if (type=="get-settings") result=new { settings=Settings,settingsRevision };
            else if(type is "get-appearance" or "set-appearance")
            {
                if(type=="set-appearance")
                {
                    var next=AppearancePreferences.Parse(payload);
                    try{AppearancePreferences.Save(appearancePreferences,next);}
                    catch(Exception storageError) when(storageError is IOException or UnauthorizedAccessException)
                    {throw new InvalidOperationException("外观保存失败，本次设置未应用。");}
                    appearanceCommitted=true;appearanceSettings=next;appearanceRevision++;appearanceNotice="";
                    backdrop=WindowMaterial.Apply(this,surface,controller,appearanceSettings);
                }
                else if(payload.ValueKind!=JsonValueKind.Object || payload.EnumerateObject().Any())throw new ArgumentException("读取外观参数无效。");
                result=Appearance;
            }
            else if(type is "get-local-api" or "set-local-sources")
            {
                if(localApi==null)throw new InvalidOperationException("此版本不包含本机弹幕服务。");
                bool write=type=="set-local-sources";
                if(payload.ValueKind!=JsonValueKind.Object || payload.EnumerateObject().Count()!=(write ? 1 : 0))throw new ArgumentException("弹幕源设置格式无效。");
                if(write)
                {
                    if(localSourcesChanging)throw new InvalidOperationException("正在应用弹幕源，请稍候。");
                    string[] sources=EmbeddedApiSources.Validate(payload.GetProperty("sources").Deserialize<string[]>());
                    try{EmbeddedApiSources.Save(localSourcesFile,sources);}
                    catch(Exception storageError) when(storageError is IOException or UnauthorizedAccessException){throw new InvalidOperationException("弹幕源保存失败，原设置未更改。");}
                    localSourcesChanging=true;
                    try
                    {
                        pending.Cancel();revision++;automaticMatcher.Suspend();apiBase="";CommentCount=0;commentSourceTitle="";CommentsLoaded?.Invoke(Array.Empty<Comment>());
                        await localApi.StartAsync(sources);if(closing)return;apiBase=localApi.ApiBase;UpdatePlayer();
                    }
                    finally{localSourcesChanging=false;}
                }
                result=localApi.View;
            }
            else if (type is "get-startup" or "set-startup")
            {
                bool write=type=="set-startup";
                if(payload.ValueKind!=JsonValueKind.Object || payload.EnumerateObject().Count()!=(write ? 1 : 0) ||
                    (write && (!payload.TryGetProperty("enabled",out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))))
                    throw new ArgumentException("开机启动设置格式无效。");
                result=new {enabled=write ? startup.SetEnabled(payload.GetProperty("enabled").GetBoolean()) : startup.Enabled};
            }
            else if (type is "configure-api" or "save-api")
            {
                if(localApi!=null)throw new InvalidOperationException("includeAPI 版本自动连接本机服务，无需设置 API 地址。");
                if(payload.ValueKind!=JsonValueKind.Object || payload.EnumerateObject().Count()!=1 ||
                    !payload.TryGetProperty("apiBase",out var address) || address.ValueKind!=JsonValueKind.String)
                    throw new ArgumentException("弹幕服务设置格式无效。");
                bool save=type=="save-api";
                string text=address.GetString()??"";
                string requestedBase=save && string.IsNullOrWhiteSpace(text) ? "" : ApiClient.NormalizeBase(text);
                if(save)
                {
                    try{ApiPreferences.Save(apiPreferences,requestedBase);}
                    catch(Exception storageError) when(storageError is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                    {throw new InvalidOperationException("API 地址保存失败，原设置未更改，请重试。");}
                    savedApiBase=requestedBase;apiNotice="";
                }
                if(requestedBase!=apiBase)
                {
                    pending.Cancel();revision++;apiBase=requestedBase;CommentCount=0;commentSourceTitle="";CommentsLoaded?.Invoke(Array.Empty<Comment>());
                }
                UpdatePlayer();result=save ? (object)new {apiBase,saved=apiBase.Length>0} : new {configured=true};
            }
            else if (type=="update-settings")
            {
                var settings=payload.GetProperty("settings").Deserialize<DanmakuSettings>(Json)??throw new ArgumentException("缺少设置。");
                settings.Validate();
                try
                {
                    string temp=preferences+".tmp"; File.WriteAllText(temp,SettingsPreferences.Serialize(settings)); File.Move(temp,preferences,true);
                }
                catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException("显示偏好保存失败，本次设置未应用。");
                }
                settingsCommitted=true; Settings=settings; settingsRevision++; Failure=""; SettingsUpdated?.Invoke(settings);
                result=Settings;
            }
            else if (type=="manual-clock")
            {
                double position=payload.GetProperty("positionMs").GetDouble(); bool playing=payload.GetProperty("playing").GetBoolean();
                if (!double.IsFinite(position) || position<0 || position>86400000) throw new ArgumentException("无效时间。");
                ManualClockUpdated?.Invoke(position,playing); result=new { positionMs=position, playing };
            }
            else if (type=="cancel") { automaticMatcher?.Suspend();pending.Cancel(); revision++; result=new { cancelled=true }; }
            else if (type=="hide")
            {
                if(payload.ValueKind!=JsonValueKind.Object || payload.EnumerateObject().Any())throw new ArgumentException("收起面板参数无效。");
                BeginInvoke(new Action(HideWorkbench));result=new {hidden=true};
            }
            else if (type=="close") { BeginInvoke(new Action(RequestExit)); result=new { closing=true }; }
            else if (type is "search" or "episodes" or "comments")
            {
                string requestedBase=ApiClient.NormalizeBase(payload.GetProperty("apiBase").GetString()??"");
                if(localApi!=null && (localApi.View.State!="ready" || requestedBase!=localApi.ApiBase || requestedBase!=apiBase))throw new InvalidOperationException("本机弹幕服务尚未就绪或连接已更新，请稍后重试。");
                string value=payload.GetProperty("value").GetString()??"";
                string requestedSource="";
                if(type=="comments" && payload.TryGetProperty("sourceTitle",out var label))
                {
                    if(label.ValueKind!=JsonValueKind.String || (label.GetString()??"").Length>500)throw new ArgumentException("来源名称无效。");
                    requestedSource=label.GetString()??"";
                }
                if (string.IsNullOrWhiteSpace(value) || value.Length>500) throw new ArgumentException("请求内容为空或过长。");
                long expectedMediaRevision=0;
                if (type=="comments" && (!payload.TryGetProperty("expectedMediaRevision",out var expected) || expected.ValueKind!=JsonValueKind.Number || !expected.TryGetInt64(out expectedMediaRevision) || expectedMediaRevision<0 || expectedMediaRevision>9007199254740991L))
                {
                    commandCode="media-context-required";
                    throw new ArgumentException("弹幕请求缺少有效的媒体版本，请重新打开新版工作台。");
                }
                UpdatePlayer();
                if (type=="comments" && expectedMediaRevision!=mediaRevision)
                {
                    commandCode="media-changed"; PushState();
                    throw new InvalidOperationException("播放器已切换，请核对分集并重新加载弹幕。");
                }
                automaticMatcher?.Suspend();
                requestNoticeTicket=++presentationTicket;
                manualNoticeTicket=requestNoticeTicket;
                overlay.SetStatus(requestNoticeTicket,type=="search" ? "matching" : "loading",type=="search" ? "搜索弹幕源" : "已发现弹幕，进行装载");
                apiBase=requestedBase;
                pending.Cancel(); pending.Dispose(); pending=new(); int current=++revision;
                var body=await api.FetchAsync(apiBase,type,value,pending.Token);
                if (closing || current!=revision) throw new OperationCanceledException();
                UpdatePlayer();
                if (closing || current!=revision) throw new OperationCanceledException();
                if (type=="comments")
                {
                    var comments=await Task.Run(()=>ApiClient.ParseComments(body));
                    if (closing || current!=revision) throw new OperationCanceledException();
                    UpdatePlayer();
                    if (closing || current!=revision) throw new OperationCanceledException();
                    CommentCount=comments.Length;commentSourceTitle=string.IsNullOrWhiteSpace(requestedSource) ? "手动选择的来源" : requestedSource; CommentsLoaded?.Invoke(comments);
                    overlay.SetStatus(requestNoticeTicket,comments.Length>0 ? "loaded" : "failed",comments.Length>0 ? "已发现弹幕，进行装载" : "未找到弹幕，可手动搜索或换源");
                    result=new { count=CommentCount, duration=body["videoDuration"]?.DeepClone() };
                }
                else {result=body;overlay.SetStatus(requestNoticeTicket,"manual","");}
            }
            else throw new ArgumentException("不支持的命令。");
            if(requestNoticeTicket>0 && manualNoticeTicket==requestNoticeTicket)manualNoticeTicket=0;
            if (epoch!=documentEpoch) return;
            Send(new { type="response", id, ok=true, data=result, settingsRevision }); PushState();
        }
        catch (Exception ex)
        {
            if(requestNoticeTicket>0 && manualNoticeTicket==requestNoticeTicket)manualNoticeTicket=0;
            if (epoch!=documentEpoch) return;
            string error=ex is OperationCanceledException ? "请求已取消。" : ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException ? ex.Message : "操作失败，请重试。";
            if (!string.IsNullOrEmpty(apiBase)) error=error.Replace(apiBase,"[API 地址]",StringComparison.OrdinalIgnoreCase);
            if(requestNoticeTicket>0 && requestNoticeTicket==presentationTicket)overlay.SetStatus(requestNoticeTicket,"failed",error);
            string code=type=="update-settings" ? settingsCommitted ? "settings-outcome-unknown" : "settings-not-applied" : type=="set-appearance" ? appearanceCommitted ? "appearance-outcome-unknown" : "appearance-not-applied" : commandCode;
            Send(new { type="response", id, ok=false, error, code });
        }
    }
}
