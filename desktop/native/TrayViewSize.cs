using System;
using System.Drawing;
using System.Linq;
using System.Text.Json;

namespace Popcorn;

internal static class TrayViewSize
{
    internal static Size Desired(string view,bool includeApi=false)=>view switch
    {
        "home"=>new(380,includeApi ? 430 : 510),
        "settings" or "search"=>new(380,540),
        _=>throw new ArgumentException("面板视图无效。")
    };
    internal static string Parse(JsonElement payload)
    {
        if(payload.ValueKind!=JsonValueKind.Object || payload.EnumerateObject().Count()!=1 ||
            !payload.TryGetProperty("view",out var value) || value.ValueKind!=JsonValueKind.String)
            throw new ArgumentException("面板视图参数无效。");
        string view=value.GetString();Desired(view);return view;
    }
}
