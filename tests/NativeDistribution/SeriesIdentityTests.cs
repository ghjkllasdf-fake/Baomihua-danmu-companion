using System;
using System.IO;
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
        CheckConsecutiveDuration();
        CheckCache(current);
        CheckRecentAlbumCache(current);
        Console.WriteLine("PASS: Chinese/SxxExx/Season identities, long episode numbers, literal movie sequels, and cross-checked library evidence without guessing.");
    }
    private static void CheckConsecutiveDuration()
    {
        var reader=new LibrarySeasonReader(System.Threading.CancellationToken.None);
        var episode=SeriesIdentity.Parse("Example Show S01E27 Previous caption");
        var window=new VideoWindow(IntPtr.Zero,"episode27",0,0,1280,720);
        Check(reader.Read(window,episode,2766560)==1,"The previous episode establishes a verified season.");
        episode=episode with{Season=0,Episode=28,EpisodeTitle="Next caption"};
        window=window with{Title="episode28"};
        Check(reader.Read(window,episode,2766560)==1,"Auto-next may initially expose the previous episode duration.");
        Check(reader.Read(window,episode,null)==1,"Missing transition duration must not discard the consecutive season.");
        Check(reader.Read(window,episode,2766560)==1,"Repeated stale duration must not consume the transition.");
        Check(reader.Read(window,episode,2748880)==1,"The new episode duration must not clear its carried season.");
        Check(reader.Read(window,episode,2748880)==1,"The season must remain stable after the actual duration arrives.");
        Check(reader.Read(window,episode,2600000)==0,"A later same-episode duration replacement still invalidates the season.");

        reader=new LibrarySeasonReader(System.Threading.CancellationToken.None);
        Check(reader.Read(window with{Title="episode27"},episode with{Season=1,Episode=27},2766560)==1,"Immediate transition baseline.");
        Check(reader.Read(window,episode,2748880)==1,"An already fresh next-episode duration must retain continuity.");
        Check(reader.Read(window,episode,2600000)==0,"Fresh duration must not grant a later replacement a transition exemption.");
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

    private static void CheckRecentAlbumCache(SeriesIdentity current)
    {
        var item=JsonNode.Parse("""
            {"player_title":"示例剧 第2集 测试分集标题","session_title":"示例剧","last_played":{"season":2,"episode":2},
             "files":[{"drive_path_info":{"file_path":"/TV/示例剧/Season 2/示例剧 - S02E02.mkv"},"real_total_duration":2784}]}
            """).AsObject();
        JsonObject RecentBody(JsonNode[] entries,string type="recent_play",string id="recent_play")=>
            new(){["code"]=0,["data"]=new JsonObject{["album_type"]=type,["album_id"]=id,
                ["album_name"]="最近观看",["count"]=entries.Length,["list"]=new JsonArray(entries.Select(e=>e.DeepClone()).ToArray())}};
        JsonObject HomeBody(JsonNode entry)=>new(){["code"]=0,["data"]=new JsonObject{["general_albums"]=new JsonArray(
            new JsonObject{["album_type"]="recent_play",["list"]=new JsonArray(entry.DeepClone())})}};
        JsonObject Envelope(JsonObject body,string url="/v3/albums")=>new(){["url"]=url,["statusCode"]=200,
            ["expiryUtc"]="2030-01-01T00:00:00Z",["data"]=Convert.ToBase64String(Encoding.UTF8.GetBytes(body.ToJsonString()))};
        JsonObject Recent(JsonNode entry)=>Envelope(RecentBody(new[]{entry}));
        int Resolve(JsonObject envelope)=>CachedSeasonReader.Resolve(current,2784000,JsonSerializer.SerializeToElement(envelope),new DateTimeOffset(2026,10,30,0,0,0,TimeSpan.Zero));
        Check(Resolve(Recent(item))==2,"The dedicated recent-play album must identify the active episode when the homepage is stale.");

        var changed=item.DeepClone().AsObject();changed["player_title"]="示例剧 第2集 另一季的标题";
        Check(Resolve(Recent(changed))==0,"A recent entry for another episode caption must not identify the active season.");
        changed=item.DeepClone().AsObject();changed["session_title"]="同名另一部作品";
        Check(Resolve(Recent(changed))==0,"A different work in recent-play data must not supply a season.");
        var wrongTitle=item.DeepClone().AsObject();wrongTitle["player_title"]="另一部作品 第2集 测试分集标题";
        var wrongSeason=item.DeepClone().AsObject();wrongSeason["player_title"]="示例剧 第1季 第2集 测试分集标题";
        int recentTitle=Resolve(Recent(wrongTitle)),recentSeason=Resolve(Recent(wrongSeason));
        int homeTitle=Resolve(Envelope(HomeBody(wrongTitle),"/v2/homepage")),homeSeason=Resolve(Envelope(HomeBody(wrongSeason),"/v2/homepage"));
        Check(recentTitle==0 && recentSeason==0 && homeTitle==0 && homeSeason==0,
            $"Player title/work and explicit season must agree with the session and file (recent work={recentTitle}, recent season={recentSeason}, home work={homeTitle}, home season={homeSeason}).");
        changed=item.DeepClone().AsObject();changed["player_title"]="示例剧 第2季 第2集 测试分集标题";
        Check(Resolve(Recent(changed))==2 && Resolve(Envelope(HomeBody(changed),"/v2/homepage"))==2,
            "An explicit player-title season matching the session and file must remain eligible.");
        changed=item.DeepClone().AsObject();changed["last_played"]["episode"]=1;
        Check(Resolve(Recent(changed))==0,"The recent item's episode number must match the current episode.");
        changed=item.DeepClone().AsObject();changed["last_played"]["season"]=1;
        Check(Resolve(Recent(changed))==0,"The recent item's season must agree with the source filename.");
        changed=item.DeepClone().AsObject();changed["files"][0]["drive_path_info"]["file_path"]="/TV/示例剧 - S02E03.mkv";
        Check(Resolve(Recent(changed))==0,"A recent entry naming a different episode file must not identify this episode.");
        changed=item.DeepClone().AsObject();changed["files"][0]["real_total_duration"]=20;
        Check(Resolve(Recent(changed))==0,"A recent entry with a mismatched duration must remain unusable.");
        Check(Resolve(Envelope(RecentBody(new[]{item},"media")))==0,"A non-recent album must not supply season evidence.");
        Check(Resolve(Envelope(RecentBody(new[]{item},id:"movie")))==0,"The recent album ID must agree with its type.");
        var rejected=Recent(item);rejected["url"]="/v3/albums?name=recent_play";
        Check(Resolve(rejected)==0,"An unverified query-bearing album endpoint must not supply season evidence.");
        rejected=Recent(item);rejected["url"]="/v2/drive/alltokens";rejected["data"]="NOT_BASE64_PRIVATE_EXAMPLE";
        Check(Resolve(rejected)==0,"Credential endpoints must be excluded before decoding their body.");
        rejected=Recent(item);rejected["expiryUtc"]="2020-01-01T00:00:00Z";
        Check(Resolve(rejected)==0,"Expired recent-play cache data must remain unusable.");
        rejected=Recent(item);rejected["statusCode"]=500;
        Check(Resolve(rejected)==0,"An unsuccessful recent-play response must remain unusable.");
        rejected=Recent(item);rejected["data"]="INVALID_BASE64";
        Check(Resolve(rejected)==0,"Corrupt recent-play cache must fail closed.");

        var otherSeason=item.DeepClone().AsObject();otherSeason["last_played"]["season"]=1;
        otherSeason["files"][0]["drive_path_info"]["file_path"]="/TV/示例剧 - S01E02.mkv";
        var conflicting=Envelope(RecentBody(new JsonNode[]{item,otherSeason}));
        var result=CachedSeasonReader.Resolve(current,2784000,JsonSerializer.SerializeToElement(conflicting),
            new DateTimeOffset(2026,10,30,0,0,0,TimeSpan.Zero),out bool conflict);
        Check(result==0 && conflict,"Matching entries from two seasons in one recent album must be reported as ambiguous.");

        string directory=Path.Combine(Path.GetTempPath(),"danmu-recent-cache-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string recent=Path.Combine(directory,"api_cache__v3_albums_name=recent_play&id=recent_play&type=recent_play&page=1&size=30.json");
        string secondPage=Path.Combine(directory,"api_cache__v3_albums_name=recent_play&id=recent_play&type=recent_play&page=2&size=50.json");
        string stale=Path.Combine(directory,new string('a',32)+".json");
        string decoy=Path.Combine(directory,"api_cache__v1_drive_alltokens.json");
        string sameEpisode=Path.Combine(directory,new string('b',32)+".json");
        string[] rejectedNames=new[]{
            "api_cache__v3_albums_name=recent_play&id=recent_play&type=recent_play&page=0&size=30.json",
            "api_cache__v3_albums_name=recent_play&id=recent_play&type=recent_play&page=01&size=30.json",
            "api_cache__v3_albums_name=recent_play&id=recent_play&type=recent_play&page=1&size=0.json",
            "api_cache__v3_albums_name=recent_play&id=recent_play&type=recent_play&page=1&size=030.json",
            "api_cache__v3_albums_name=movie&id=recent_play&type=recent_play&page=1&size=30.json",
            "api_cache__v3_albums_name=recent_play&id=movie&type=recent_play&page=1&size=30.json",
            "api_cache__v3_albums_name=recent_play&id=recent_play&type=media&page=1&size=30.json",
            "api_cache__v3_albums_name=recent_play&id=recent_play&type=recent_play&page=1&size=30&extra=1.json"
        }.Select(name=>Path.Combine(directory,name)).ToArray();
        try
        {
            var old=item.DeepClone().AsObject();old["player_title"]="示例剧 第1集 旧的分集标题";
            old["last_played"]["season"]=1;old["last_played"]["episode"]=1;
            old["files"][0]["drive_path_info"]["file_path"]="/TV/示例剧 - S01E01.mkv";
            File.WriteAllText(stale,Envelope(HomeBody(old),"/v2/homepage").ToJsonString());
            File.WriteAllText(recent,Recent(item).ToJsonString());
            File.WriteAllText(decoy,Recent(otherSeason).ToJsonString());
            foreach(string path in rejectedNames)File.WriteAllText(path,Recent(otherSeason).ToJsonString());
            result=CachedSeasonReader.Read(current,2784000,directory,out conflict);
            Check(result==2 && !conflict,"The recent-play cache must work with a stale homepage, while credential and invalid album/page/size names are excluded.");

            File.Delete(recent);
            File.WriteAllText(secondPage,Recent(item).ToJsonString());
            result=CachedSeasonReader.Read(current,2784000,directory,out conflict);
            Check(result==2 && !conflict,"A later recent-play page with positive page and size must be eligible by itself.");
            File.WriteAllText(recent,Recent(item).ToJsonString());
            File.WriteAllText(secondPage,Recent(otherSeason).ToJsonString());
            result=CachedSeasonReader.Read(current,2784000,directory,out conflict);
            Check(result==0 && conflict,"Matching entries from different recent-play cache pages must report conflicting seasons.");
            File.Delete(secondPage);

            File.WriteAllText(sameEpisode,Envelope(HomeBody(otherSeason),"/v2/homepage").ToJsonString());
            result=CachedSeasonReader.Read(current,2784000,directory,out conflict);
            Check(result==0 && conflict,"Conflicting recent-play and homepage evidence for the same episode must not choose a season.");
        }
        finally
        {
            foreach(string path in new[]{recent,secondPage,stale,decoy,sameEpisode}.Concat(rejectedNames))if(File.Exists(path))File.Delete(path);
            if(Directory.Exists(directory))Directory.Delete(directory);
        }
        Console.WriteLine("PASS: dedicated recent-play cache identity, endpoint/album validation, stale homepage, and conflict rejection.");
    }
}
