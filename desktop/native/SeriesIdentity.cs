using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Popcorn;

internal sealed record SeriesIdentity(string Title,int Season,int Episode,string EpisodeTitle)
{
    private const string Digits=@"[0-9零〇一二两三四五六七八九十百]+";
    private static readonly Regex EncodedEpisode=new(@"S(?<season>\d{1,3})[ ._-]*E(?<episode>\d{1,4})(?!\d)|(?<!\d)(?<season>\d{1,2})x(?<episode>\d{1,4})(?!\d)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private static readonly Regex ChineseEpisode=new(@"第\s*(?<episode>"+Digits+@")\s*集",RegexOptions.CultureInvariant);
    private static readonly Regex SeasonMarker=new(@"第\s*(?<season>"+Digits+@")\s*季|(?<![A-Za-z0-9])Season[ ._-]*(?<season>\d{1,3})(?![A-Za-z0-9])|(?<![A-Za-z0-9])S(?<season>\d{1,3})(?![A-Za-z0-9])",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    internal string QualifiedTitle=>Season>0 ? Title+" 第"+Season.ToString(CultureInfo.InvariantCulture)+"季" : Title;
    internal static int Number(string text)
    {
        if(int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out int value))return value>0 ? value : 0;
        int total=0,digit=0;
        foreach(char c in text??"")
        {
            int n="零一二三四五六七八九".IndexOf(c);
            if(c=='〇')n=0;if(c=='两')n=2;
            if(n>=0){digit=n;continue;}
            if(c is '十' or '百'){total+=(digit==0 ? 1 : digit)*(c=='十' ? 10 : 100);digit=0;}
            else return 0;
        }
        value=total+digit;return value is >0 and <=999 ? value : 0;
    }
    internal static SeriesIdentity Parse(string text)
    {
        text=(text??"").Normalize(NormalizationForm.FormKC).Trim();
        text=Regex.Replace(text,@"\s*[-|]\s*(?:网易)?爆米花$","",RegexOptions.IgnoreCase);
        text=Regex.Replace(text,@"\.(?:mkv|mp4|avi|mov|webm|m4v|ts)$","",RegexOptions.IgnoreCase);
        int season=0,episode=0;string caption="";
        var marker=EncodedEpisode.Match(text);
        if(!marker.Success)marker=ChineseEpisode.Match(text);
        if(marker.Success)
        {
            episode=Number(marker.Groups["episode"].Value);
            if(marker.Groups["season"].Success)season=Number(marker.Groups["season"].Value);
            if(marker.Groups["season"].Success && season==0)return new(text,0,0,"");
            caption=text[(marker.Index+marker.Length)..].Trim(' ','.','_','-','–','—',':','：',']',')','】');
            text=text[..marker.Index].TrimEnd(' ','.','_','-','–','—',':','：','[','(','【');
        }
        var seasons=SeasonMarker.Matches(text).Select(m=>Number(m.Groups["season"].Value)).Where(n=>n>0).Distinct().ToArray();
        if(seasons.Length==1 && (season==0 || season==seasons[0]))season=seasons[0];
        else if(seasons.Length>0)return new(text,0,episode,caption);
        text=Regex.Replace(SeasonMarker.Replace(text,m=>Number(m.Groups["season"].Value) is >0 and <=999 ? "" : m.Value),
            @"[\[（(]\s*[\]）)]|【\s*】","").Trim(' ','.','_','-','–','—',':','：');
        if(season>999)season=0;
        return new(text,season,episode,caption);
    }
    internal static string TitleKey(string text)
    {
        text=Regex.Replace(text??"", @"[（(](?:19|20)\d{2}[)）].*$","");
        return Regex.Replace(text.Normalize(NormalizationForm.FormKC),@"[\s\p{P}\p{S}]","").ToUpperInvariant();
    }
    internal static string EpisodeKey(string text)
    {
        text=Regex.Replace(text??"", @"^\s*(?:第\s*"+Digits+@"\s*集|(?:Episode|EP|E)?\s*\d+[\s.、:_-]*)","",RegexOptions.IgnoreCase);
        text=Regex.Replace(text,@"\s*\d{1,3}:\d{2}(?::\d{2})?\s*$","");
        return TitleKey(text);
    }
    internal string[] SearchQueries()
    {
        if(Season==0)return new[]{Title.Trim()};
        string searchTitle=Regex.Replace(Title,@"\s*[（(](?:19|20)\d{2}[)）]\s*"," ").Trim();
        if(searchTitle.Length==0)searchTitle=Title;
        // The base-title fallback also finds providers which name a sequel "Title2".
        return new[]{searchTitle+" 第"+Season.ToString(CultureInfo.InvariantCulture)+"季",searchTitle}.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
