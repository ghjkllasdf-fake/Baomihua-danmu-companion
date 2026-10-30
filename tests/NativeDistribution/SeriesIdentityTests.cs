using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Popcorn;

internal static class SeriesIdentityTests
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    internal static void Run()
    {
        foreach(var entry in new[]{
            ("示例剧 第 二 季 第3集 相逢","示例剧",2,3,"相逢"),
            ("示例剧 - S02E03 - 相逢.mkv","示例剧",2,3,"相逢"),
            ("Example.Show.S02E03.mkv","Example.Show",2,3,""),
            ("示例剧 [S02E03] 相逢.mkv","示例剧",2,3,"相逢"),
            ("Example Show Season 2 第3集 相逢","Example Show",2,3,"相逢"),
            ("Example_Show_Season_2 第3集 相逢","Example_Show",2,3,"相逢"),
            ("示例剧S2 第3集 相逢","示例剧",2,3,"相逢"),
            ("示例剧 2x03 相逢.mkv","示例剧",2,3,"相逢"),
            ("示例剧 第十二季 第3集 相逢","示例剧",12,3,"相逢"),
            ("示例剧 第3集 相逢","示例剧",0,3,"相逢"),
            ("长篇动画 第1133集 相逢","长篇动画",0,1133,"相逢"),
            ("示例电影2.mp4","示例电影2",0,0,""),
            ("示例剧(2019) 第2季","示例剧(2019)",2,0,"")})
        {
            var actual=SeriesIdentity.Parse(entry.Item1);
            Check(actual.Title==entry.Item2 && actual.Season==entry.Item3 && actual.Episode==entry.Item4 && actual.EpisodeTitle==entry.Item5,
                "Season/episode parsing lost or invented media identity: "+entry.Item1);
        }
        Check(SeriesIdentity.Parse("示例剧 第一季").QualifiedTitle=="示例剧 第1季","Equivalent season labels need one stable media title.");
        Check(SeriesIdentity.Parse("示例剧 S00E02").Title!="示例剧","Special-season markers must not be erased into an ordinary first-season identity.");
        Check(SeriesIdentity.Parse("示例剧 第二季").SearchQueries().SequenceEqual(new[]{"示例剧 第2季","示例剧"}),"Season queries must retain the base-title fallback.");
        Check(SeriesIdentity.Parse("示例剧(2019) 第2季").SearchQueries().SequenceEqual(new[]{"示例剧 第2季","示例剧"}),"A series' original year must not hide a later-season search result.");
        var current=SeriesIdentity.Parse("示例剧 第2集 测试分集标题");
        LibrarySeasonText[] context={
            new("","示例剧 第 2 季"),new("","示例剧 - S02E02 - 测试文件.mkv"),
            new("","2. 测试分集标题"),new("VideoTimeSpanText","46 分钟 24 秒")};
        Check(LibrarySeasonReader.Resolve(current,2784000,context)==2,"Matching library season/file/episode-caption/duration must identify the active season.");
        Check(LibrarySeasonReader.Resolve(current,10000,context)==0,"A different episode duration must not become season evidence.");
        Check(LibrarySeasonReader.Resolve(current,2784000,context.Select(c=>c.Text=="2. 测试分集标题" ? c with{Text="2. 另一个分集标题"} : c))==0,"Another season's episode subtitle must not identify the active video.");
        Check(LibrarySeasonReader.Resolve(current,2784000,context.Select(c=>c.Text.EndsWith(".mkv",StringComparison.Ordinal) ? c with{Text="示例剧 - S01E02.mkv"} : c))==0,"Contradictory header/file seasons must remain unknown.");
        Check(LibrarySeasonReader.Resolve(current,2784000,context.Select(c=>c.Text.EndsWith(".mkv",StringComparison.Ordinal) ? c with{Text="示例剧 - S02E03.mkv"} : c))==0,"A selected file for another episode must not identify the active video.");
        Check(LibrarySeasonReader.Resolve(SeriesIdentity.Parse("示例剧 第2集"),2784000,context)==0,"A generic episode number cannot prove which library season is playing.");
        var next=context.Concat(new[]{new LibrarySeasonText("VideoTimeText","46:00"),new LibrarySeasonText("","3. 新的分集标题")});
        Check(LibrarySeasonReader.Resolve(SeriesIdentity.Parse("示例剧 第3集 新的分集标题"),2760000,next)==2,
            "Auto-next may use the matching episode row and its duration while the details page still selects the previous file.");
        Check(LibrarySeasonReader.Resolve(SeriesIdentity.Parse("示例剧 第3集 新的分集标题"),20000,next)==0,
            "Auto-next still needs the current episode's duration, not a stale selected-file duration.");
        var directEpisode=new[]{
            new LibrarySeasonText("","示例剧 第 1 季"),new("","示例剧 - S01E19 - 之前的文件.mp4"),
            new("VideoTimeText","45:00"),new("","21. 当前分集标题"),new("VideoTimeSpanText","46 分钟")};
        var directIdentity=SeriesIdentity.Parse("示例剧 第21集 当前分集标题");
        Check(LibrarySeasonReader.Resolve(directIdentity,2750040,directEpisode)==1,
            "Direct episode selection must accept the library's truncated-minute duration while the selected filename still belongs to another episode.");
        Check(LibrarySeasonReader.Resolve(directIdentity,2800000,directEpisode)==0,
            "Truncated-minute evidence must not accept a video outside that minute.");
        Check(LibrarySeasonReader.Resolve(directIdentity,2690000,directEpisode)==0,
            "Truncated-minute evidence must not accept the previous minute.");
        Check(LibrarySeasonReader.Resolve(directIdentity,2750040,directEpisode.Select(t=>t.Id=="VideoTimeText" ? t with{Text="45:10"} : t))==0,
            "A duration containing explicit seconds must retain its seconds-level comparison.");
        Check(LibrarySeasonReader.Resolve(directIdentity,2750040,directEpisode.Select(t=>t.Text=="21. 当前分集标题" ? t with{Text="21. 其他分集标题"} : t))==0,
            "A minute-level duration must still require the exact active episode caption.");
        Check(SeriesIdentity.Parse("示例剧 第1季").QualifiedTitle!=SeriesIdentity.Parse("示例剧 第2季").QualifiedTitle,"Same episode number in distinct seasons must form separate media keys.");
        var video=new VideoWindow(new IntPtr(1),"video",0,0,1280,720){ProcessId=7};
        var continuity=new SeasonContinuity();
        Check(continuity.Next(video,current)==0,"A fresh media session must not invent a season.");
        continuity.Confirm(2);
        Check(continuity.Next(video,current with{Episode=3})==2,"A verified season may follow consecutive episodes in the same window.");
        Check(continuity.Next(video,current with{Episode=3,EpisodeTitle="不同季同集数"})==0,"Same-number media replacement must not carry a season hint.");
        continuity.Confirm(2);
        Check(continuity.Next(video with{Handle=new IntPtr(2)},current with{Episode=4})==0,"A new playback window must revalidate its season.");
        CheckCache(current);
        Console.WriteLine("PASS: Chinese/SxxExx/Season identities, long episode numbers, literal movie sequels, and cross-checked library evidence without guessing.");
    }
    private static void CheckCache(SeriesIdentity current)
    {
        var item=JsonNode.Parse("""
            {"player_title":"示例剧 第2集 测试分集标题","session_title":"示例剧","last_played":{"season":2,"episode":2},
             "files":[{"drive_path_info":{"file_path":"/TV/示例剧/Season 2/示例剧 - S02E02.mkv"},"real_total_duration":2784}]}
            """).AsObject();
        JsonObject Envelope(JsonNode entry)
        {
            var body=new JsonObject{["code"]=0,["data"]=new JsonObject{["general_albums"]=new JsonArray(
                new JsonObject{["album_type"]="recent_play",["list"]=new JsonArray(entry.DeepClone())})}};
            return new JsonObject{["url"]="/v2/homepage",["statusCode"]=200,["expiryUtc"]="2030-01-01T00:00:00Z",
                ["data"]=Convert.ToBase64String(Encoding.UTF8.GetBytes(body.ToJsonString()))};
        }
        int Resolve(JsonObject envelope)=>CachedSeasonReader.Resolve(current,2784000,JsonSerializer.SerializeToElement(envelope),new DateTimeOffset(2026,10,30,0,0,0,TimeSpan.Zero));
        Check(Resolve(Envelope(item))==2,"Recent-play cache must identify the exact active episode without an open details page.");
        var changed=item.DeepClone().AsObject();changed["player_title"]="示例剧 第2集 另一季的标题";
        Check(Resolve(Envelope(changed))==0,"Stale cache captions must not choose another season.");
        changed=item.DeepClone().AsObject();changed["last_played"]["season"]=1;
        Check(Resolve(Envelope(changed))==0,"Cached season and source filename must agree.");
        changed=item.DeepClone().AsObject();changed["files"][0]["real_total_duration"]=20;
        Check(Resolve(Envelope(changed))==0,"Cached media duration must agree with the actual player.");
        var rejected=Envelope(item);rejected["url"]="/v2/drive/alltokens";rejected["data"]="NOT_BASE64_PRIVATE_EXAMPLE";
        Check(Resolve(rejected)==0,"Credential endpoints must be excluded before decoding their body.");
        rejected=Envelope(item);rejected["expiryUtc"]="2020-01-01T00:00:00Z";
        Check(Resolve(rejected)==0,"Expired cache data must remain unusable.");
        rejected=Envelope(item);rejected["data"]="INVALID_BASE64";
        Check(Resolve(rejected)==0,"Corrupt cache must fail closed without throwing or printing contents.");
        rejected=Envelope(item);rejected["statusCode"]="invalid";
        Check(Resolve(rejected)==0,"Invalid cache types must not break playback sampling.");
        Console.WriteLine("PASS: read-only homepage evidence, stale/contradictory/expired/corrupt cache rejection, and no credentials-body decoding.");
    }
}
