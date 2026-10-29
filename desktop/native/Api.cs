using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

public sealed class ApiRateLimitException : InvalidOperationException
{
    public ApiRateLimitException() : base("弹幕服务已限流，请稍后重试；不会自动循环请求。") { }
}
public sealed class ApiAuthenticationException : InvalidOperationException
{
    public ApiAuthenticationException() : base("弹幕服务认证失败，请检查 API 地址或凭据。") { }
}

public sealed class ApiClient : IDisposable
{
    private readonly HttpClient client;
    public ApiClient(bool useProxy=true)=>client=new(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=useProxy}){Timeout=TimeSpan.FromSeconds(180)};
    public static string NormalizeBase(string value)
    {
        value=(value??"").Trim().TrimEnd('/');
        if (!Uri.TryCreate(value,UriKind.Absolute,out var uri) || (uri.Scheme!="http" && uri.Scheme!="https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || value.Length>2048)
            throw new ArgumentException("请填写有效的 HTTP/HTTPS 弹幕服务地址。");
        if (value.EndsWith("/api/v2",StringComparison.OrdinalIgnoreCase)) value=value[..^7];
        return value;
    }
    public async Task<JsonObject> FetchAsync(string apiBase,string action,string value,CancellationToken cancellation)
    {
        apiBase=NormalizeBase(apiBase);
        if (value==null || value.Length>500) throw new ArgumentException("请求内容过长。");
        string id=Uri.EscapeDataString(value);
        string path=action switch {
            "search"=>"/search/anime?keyword="+id,
            "episodes"=>"/bangumi/"+id,
            "comments"=>"/comment/"+id+"?format=json&duration=true",
            _=>throw new ArgumentException("未知请求。")
        };
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if(client.Timeout!=Timeout.InfiniteTimeSpan) deadline.CancelAfter(client.Timeout);
        var requestStop=deadline.Token;
        try
        {
            using var response=await client.GetAsync(apiBase+"/api/v2"+path,HttpCompletionOption.ResponseHeadersRead,requestStop).ConfigureAwait(false);
            if((int)response.StatusCode==429)throw new ApiRateLimitException();
            if((int)response.StatusCode is 401 or 403)throw new ApiAuthenticationException();
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("弹幕服务返回 HTTP "+(int)response.StatusCode+"。");
            if (response.Content.Headers.ContentLength>64*1024*1024) throw new InvalidOperationException("弹幕响应超过大小限制。");
            using var stream=await response.Content.ReadAsStreamAsync(requestStop).ConfigureAwait(false);
            using var buffer=new System.IO.MemoryStream();
            var chunk=new byte[81920]; int read;
            while ((read=await stream.ReadAsync(chunk,requestStop).ConfigureAwait(false))>0)
            {
                if (buffer.Length+read>64*1024*1024) throw new InvalidOperationException("弹幕响应超过大小限制。");
                buffer.Write(chunk,0,read);
            }
            requestStop.ThrowIfCancellationRequested();
            string text=Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
            var body=JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException("服务响应不是 JSON 对象。");
            if (body["success"] is JsonValue success && success.TryGetValue<bool>(out bool ok) && !ok)
                throw new InvalidOperationException(body["errorMessage"]?.ToString()??"弹幕服务返回失败状态。");
            var result=NormalizeResponse(action,body);
            requestStop.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException ex) when (!cancellation.IsCancellationRequested && (deadline.IsCancellationRequested || ex.InnerException is TimeoutException))
        {
            throw new InvalidOperationException("弹幕服务请求超时，请重试。");
        }
        catch (OperationCanceledException) { throw; }
        catch (ApiRateLimitException) { throw; }
        catch (ApiAuthenticationException) { throw; }
        catch (Exception ex)
        {
            string message=ex is HttpRequestException ? "无法连接弹幕服务，请检查地址和网络。" : ex.Message;
            message=message.Replace(apiBase,"[API 地址]",StringComparison.OrdinalIgnoreCase);
            message=Regex.Replace(message,@"https?://[^\s\""<>]+","[API 地址]",RegexOptions.IgnoreCase);
            if (message.Length>1000) message=message[..1000];
            throw new InvalidOperationException(message);
        }
    }
    public static JsonObject NormalizeResponse(string action,JsonObject body)
    {
        if (action=="comments")
        {
            if (body["comments"] is not JsonArray) throw new InvalidOperationException("服务未返回有效的弹幕列表。");
            return body;
        }
        JsonArray source=action=="search" ? body["animes"] as JsonArray : (body["bangumi"] as JsonObject)?["episodes"] as JsonArray;
        if (source==null) throw new InvalidOperationException("服务返回的作品或分集列表格式不正确。");
        var safe=new JsonArray();
        foreach (var node in source.Take(10000))
        {
            if (node is not JsonObject item) continue;
            string id=ScalarId(item[action=="search" ? "animeId" : "episodeId"]);
            if (id.Length==0 || id.Length>500) continue;
            if (action=="search")
            {
                if (item["animeTitle"] is not JsonValue titleValue || !titleValue.TryGetValue<string>(out var title) || string.IsNullOrWhiteSpace(title)) continue;
                safe.Add(new JsonObject { ["animeId"]=id,["animeTitle"]=title.Length>500 ? title[..500] : title });
            }
            else
            {
                string title=item["episodeTitle"] is JsonValue titleValue && titleValue.TryGetValue<string>(out var text) ? text : "";
                string number=ScalarId(item["episodeNumber"]);
                safe.Add(new JsonObject { ["episodeId"]=id,["episodeTitle"]=title.Length>500 ? title[..500] : title,["episodeNumber"]=number.Length>0 ? number : (safe.Count+1).ToString(CultureInfo.InvariantCulture) });
            }
        }
        if (source.Count>0 && safe.Count==0) throw new InvalidOperationException("服务列表没有可识别的有效条目。");
        return action=="search" ? new JsonObject { ["animes"]=safe } : new JsonObject { ["bangumi"]=new JsonObject { ["episodes"]=safe } };
    }
    private static string ScalarId(JsonNode value)
    {
        if (value is not JsonValue scalar) return "";
        if (scalar.TryGetValue<string>(out var text)) return text??"";
        return scalar.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : "";
    }
    public static Comment[] ParseComments(JsonObject response)
    {
        var parsed=new List<Comment>();
        if (response["comments"] is not JsonArray source) return parsed.ToArray();
        foreach (var item in source.Take(300000))
        {
            if (item is not JsonObject comment || comment["m"] is not JsonValue message || !message.TryGetValue<string>(out var text) ||
                comment["p"] is not JsonValue metadata || !metadata.TryGetValue<string>(out var parameters)) continue;
            string[] parts=parameters.Split(',');
            if (string.IsNullOrWhiteSpace(text) || parts.Length<2 ||
                !double.TryParse(parts[0],NumberStyles.Float,CultureInfo.InvariantCulture,out double time) || !double.IsFinite(time) || time<0) continue;
            if (!int.TryParse(parts[1],out int mode)) mode=1;
            if (mode is 7 or 8) continue;
            int colorField=parts.Length>=8 ? 3 : 2;
            int color=16777215;
            if (parts.Length>colorField && int.TryParse(parts[colorField],out int value) && value>=0 && value<=16777215) color=value;
            if (((color>>16)&255)*299+((color>>8)&255)*587+(color&255)*114<65000) color=16777215;
            text=DanmakuText.SingleLine(text);
            if (text.Length==0) continue;
            parsed.Add(new Comment(time*1000,mode,color,text));
        }
        return parsed.OrderBy(c=>c.TimeMs).ToArray();
    }
    public void Dispose()=>client.Dispose();
}
