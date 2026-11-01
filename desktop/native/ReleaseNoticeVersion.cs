using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Popcorn;

internal sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    private readonly uint[] core;
    private readonly string[] pre;
    public string Text {get;}
    public bool Stable=>pre.Length==0;
    private ReleaseVersion(string text,uint[] core,string[] pre){Text=text;this.core=core;this.pre=pre;}
    public static bool TryParse(string input,out ReleaseVersion version)
    {
        version=null;
        if(input==null || input.Length is <5 or >96 || input.Any(char.IsWhiteSpace))return false;
        string text=input.StartsWith('v') ? input[1..] : input;
        if(!Regex.IsMatch(text,@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z"))return false;
        text=text.Split('+')[0];string[] parts=text.Split('-',2),numbers=parts[0].Split('.');
        var core=new uint[3];for(int i=0;i<3;i++)if(!uint.TryParse(numbers[i],out core[i]))return false;
        string[] pre=parts.Length==2 ? parts[1].Split('.') : Array.Empty<string>();
        if(pre.Any(p=>p.All(char.IsAsciiDigit) && p.Length>1 && p[0]=='0'))return false;
        version=new ReleaseVersion(text,core,pre);return true;
    }
    public int CompareTo(ReleaseVersion other)
    {
        for(int i=0;i<3;i++){int c=core[i].CompareTo(other.core[i]);if(c!=0)return c;}
        if(Stable || other.Stable)return Stable.CompareTo(other.Stable);
        for(int i=0;i<Math.Min(pre.Length,other.pre.Length);i++)
        {
            string a=pre[i],b=other.pre[i];bool an=a.All(char.IsAsciiDigit),bn=b.All(char.IsAsciiDigit);
            int c=an && bn ? a.Length!=b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a,b) : an!=bn ? an ? -1 : 1 : string.CompareOrdinal(a,b);
            if(c!=0)return c;
        }
        return pre.Length.CompareTo(other.pre.Length);
    }
    public static bool Newer(string a,string b)=>TryParse(a,out var x) && TryParse(b,out var y) && x.CompareTo(y)>0;
}
