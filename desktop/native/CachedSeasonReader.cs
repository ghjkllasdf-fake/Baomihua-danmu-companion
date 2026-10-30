using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Popcorn;

// Reads only existing homepage metadata. Never contacts a server, opens a drive,
// decrypts a database, writes the player's cache, or returns file paths.
internal static class CachedSeasonReader
{
    private const int MaxBytes=1024*1024;
    private static readonly JsonDocumentOptions Options=new(){MaxDepth=24,AllowDuplicateProperties=false};
    internal static int Read(SeriesIdentity playing,double? duration,out bool conflict)
    {
        conflict=false;
        string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BaoMiHua","Cache","RequestCache");
        var matches=new HashSet<int>();int bytesRead=0;
        try
        {
            foreach(string path in Directory.EnumerateFiles(directory,"*.json").Take(32))
            {
                // Current homepage envelopes use hashed names. Named credentials
                // and drive-cache files are excluded before opening any stream.
                if(!Regex.IsMatch(Path.GetFileName(path),@"^[a-fA-F0-9]{32,64}\.json$"))continue;
                try
                {
                    if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)continue;
                    using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                    if(file.Length is <1 or >MaxBytes)continue;
                    byte[] bytes=new byte[(int)Math.Min(file.Length+1,MaxBytes+1)];int count=0,read;
                    while(count<bytes.Length && (read=file.Read(bytes,count,bytes.Length-count))>0)count+=read;
                    bytesRead+=count;if(bytesRead>4*MaxBytes)break;
                    if(count>MaxBytes)continue;
                    int start=count>=3 && bytes[0]==239 && bytes[1]==187 && bytes[2]==191 ? 3 : 0;
                    using var envelope=JsonDocument.Parse(bytes.AsMemory(start,count-start),Options);
                    int season=Resolve(playing,duration,envelope.RootElement,DateTimeOffset.UtcNow,out bool ambiguous);
                    conflict|=ambiguous;
                    if(season>0)matches.Add(season);
                }
                catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException){}
            }
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException){}
        conflict|=matches.Count>1;
        return !conflict && matches.Count==1 ? matches.Single() : 0;
    }
    internal static int Resolve(SeriesIdentity playing,double? duration,JsonElement envelope,DateTimeOffset now)
        =>Resolve(playing,duration,envelope,now,out _);
    internal static int Resolve(SeriesIdentity playing,double? duration,JsonElement envelope,DateTimeOffset now,out bool conflict)
    {
        conflict=false;
        try{return ResolveCore(playing,duration,envelope,now,out conflict);}
        catch(Exception error) when(error is JsonException or InvalidOperationException or ArgumentException or FormatException){return 0;}
    }
    private static int ResolveCore(SeriesIdentity playing,double? duration,JsonElement envelope,DateTimeOffset now,out bool conflict)
    {
        conflict=false;
        if(playing.Episode==0 || duration is not >0 || SeriesIdentity.EpisodeKey(playing.EpisodeTitle).Length<3 ||
            Text(envelope,"url")!="/v2/homepage" || !envelope.TryGetProperty("statusCode",out var status) || !status.TryGetInt32(out int code) || code!=200 ||
            !DateTimeOffset.TryParse(Text(envelope,"expiryUtc"),CultureInfo.InvariantCulture,DateTimeStyles.None,out var expiry) || expiry<now)return 0;
        string encoded=Text(envelope,"data");if(encoded.Length==0 || encoded.Length>MaxBytes*4/3+4)return 0;
        byte[] decoded=Convert.FromBase64String(encoded);if(decoded.Length>MaxBytes)return 0;
        using var body=JsonDocument.Parse(decoded,Options);
        if(!body.RootElement.TryGetProperty("code",out var responseCode) || !responseCode.TryGetInt32(out int result) || result!=0 ||
            !body.RootElement.TryGetProperty("data",out var data) || !data.TryGetProperty("general_albums",out var albums) || albums.ValueKind!=JsonValueKind.Array)return 0;
        var matches=new HashSet<int>();string key=SeriesIdentity.TitleKey(playing.Title),caption=SeriesIdentity.EpisodeKey(playing.EpisodeTitle);
        foreach(var album in albums.EnumerateArray().Take(100))
        {
            if(Text(album,"album_type")!="recent_play" || !album.TryGetProperty("list",out var list) || list.ValueKind!=JsonValueKind.Array)continue;
            foreach(var item in list.EnumerateArray().Take(1000))
            {
                if(SeriesIdentity.TitleKey(Text(item,"session_title"))!=key)continue;
                var active=SeriesIdentity.Parse(Text(item,"player_title"));
                if(active.Episode!=playing.Episode || SeriesIdentity.EpisodeKey(active.EpisodeTitle)!=caption ||
                    !item.TryGetProperty("last_played",out var last) || !last.TryGetProperty("season",out var seasonValue) ||
                    !seasonValue.TryGetInt32(out int season) || season is <1 or >999 ||
                    !last.TryGetProperty("episode",out var episodeValue) || !episodeValue.TryGetInt32(out int episode) || episode!=playing.Episode ||
                    !item.TryGetProperty("files",out var files) || files.ValueKind!=JsonValueKind.Array)continue;
                foreach(var file in files.EnumerateArray().Take(32))
                {
                    if(!file.TryGetProperty("drive_path_info",out var drive))continue;
                    string path=Text(drive,"file_path");if(path.Length is <1 or >4096)continue;
                    var identity=SeriesIdentity.Parse(path.Replace('\\','/').Split('/').Last());
                    if(identity.Season!=season || identity.Episode!=episode || SeriesIdentity.TitleKey(identity.Title)!=key)continue;
                    double seconds=0;
                    if(file.TryGetProperty("real_total_duration",out var real) && real.ValueKind==JsonValueKind.Number)real.TryGetDouble(out seconds);
                    if(seconds<=0 && file.TryGetProperty("total_duration",out var total) && total.ValueKind==JsonValueKind.Number)total.TryGetDouble(out seconds);
                    if(double.IsFinite(seconds) && seconds>0 && Math.Abs(seconds*1000-duration.Value)<=2000)matches.Add(season);
                }
            }
        }
        conflict=matches.Count>1;
        return matches.Count==1 ? matches.Single() : 0;
    }
    private static string Text(JsonElement value,string key)=>value.ValueKind==JsonValueKind.Object &&
        value.TryGetProperty(key,out var property) && property.ValueKind==JsonValueKind.String ? property.GetString()??"" : "";
}
