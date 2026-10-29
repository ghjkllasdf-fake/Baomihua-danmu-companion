using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Popcorn;

internal static class AppIcons
{
    public static Icon Load(string root,bool small=false)
    {
        string path=Path.Combine(root,"assets",small ? "tray.ico" : "app.ico");
        return new Icon(path,small ? SystemInformation.SmallIconSize : SystemInformation.IconSize);
    }
}
