using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Popcorn;

internal static class CommentSprite
{
    private const double BlurRadius=3,ShadowDepth=1.5;
    private static double Extent(double strength)=>strength>0 ? BlurRadius+ShadowDepth : 0;
    public static double Padding(double outline,double strength,double scale)=>outline*scale+2+Extent(strength)*scale;
    public static double LineHeight(double font,double strength,double scale)=>font*1.4+(4+2*Extent(strength))*scale;

    private sealed class TextVisual : DrawingVisual
    {
        public TextVisual(double strength,double scale)
        {
            if(strength<=0)return;
            var effect=new DropShadowEffect{Color=Colors.Black,Opacity=.85*strength,Direction=315,
                BlurRadius=BlurRadius*scale,ShadowDepth=ShadowDepth*scale,RenderingBias=RenderingBias.Quality};
            effect.Freeze();VisualEffect=effect;
        }
    }
    public static BitmapSource Render(Geometry geometry,Brush fill,double outline,double strength,double scale,double dpi,double pad,RasterSize raster)
    {
        // Bake the soft shadow once per cached sprite, not once per overlay frame.
        var text=new TextVisual(strength,scale);
        using(var dc=text.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(pad-geometry.Bounds.Left,pad-geometry.Bounds.Top));
            dc.DrawGeometry(fill,outline>0 ? new Pen(Brushes.Black,outline*scale) : null,geometry);dc.Pop();
        }
        var bitmap=new RenderTargetBitmap(raster.Width,raster.Height,96*dpi,96*dpi,PixelFormats.Pbgra32);
        bitmap.Render(text);return bitmap;
    }
}
