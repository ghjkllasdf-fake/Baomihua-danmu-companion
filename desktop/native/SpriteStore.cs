using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Popcorn;

internal sealed class Sprite(BitmapSource image,double width,double height,RasterSize raster,LinkedListNode<string> node)
{
    public BitmapSource Image { get; }=image;
    private ImageBrush alphaBrush;
    private double brushOpacity=double.NaN;
    public ImageBrush AlphaBrush(double opacity)
    {
        if(alphaBrush==null || brushOpacity!=opacity)
        {
            alphaBrush=new ImageBrush(Image) { Opacity=opacity,Stretch=Stretch.Fill };
            alphaBrush.Freeze();brushOpacity=opacity;
        }
        return alphaBrush;
    }
    public double Width { get; }=width;
    public double Height { get; }=height;
    public RasterSize Raster { get; }=raster;
    public LinkedListNode<string> Node { get; }=node;
    public int References;
}

internal sealed class SpriteStore
{
    private readonly Dictionary<string,Sprite> entries=new();
    private readonly LinkedList<string> lru=new();
    private readonly long budget;
    public int Count=>entries.Count;
    public long ResidentBytes { get; private set; }
    public long Created { get; private set; }
    public long Rejected { get; private set; }
    public SpriteStore(long budget=SpriteLimits.MaxResidentBytes)
    {
        if (budget<=0 || budget>SpriteLimits.MaxResidentBytes) throw new ArgumentOutOfRangeException(nameof(budget));
        this.budget=budget;
    }
    public Sprite Find(string key)
    {
        if (!entries.TryGetValue(key,out var sprite)) return null;
        lru.Remove(sprite.Node); lru.AddLast(sprite.Node); return sprite;
    }
    public Sprite Create(string key,double width,double height,RasterSize raster,Func<BitmapSource> factory)
    {
        var existing=Find(key); if (existing!=null) return existing;
        if (raster.Width<1 || raster.Height<1 || raster.Width>SpriteLimits.MaxDimension || raster.Height>SpriteLimits.MaxDimension ||
            raster.Bytes!=checked((long)raster.Width*raster.Height*4) || raster.Bytes>SpriteLimits.MaxSpriteBytes || raster.Bytes>budget)
        { Rejected++; return null; }
        while (ResidentBytes>budget-raster.Bytes || entries.Count>=SpriteLimits.MaxEntries)
        {
            var candidate=lru.First;
            while (candidate!=null && entries[candidate.Value].References>0) candidate=candidate.Next;
            if (candidate==null) { Rejected++; return null; }
            Remove(candidate);
        }
        // Pinned entries remain in this store, so active bullets cannot escape the pixel budget.
        var image=factory();
        if (image==null || image.PixelWidth!=raster.Width || image.PixelHeight!=raster.Height) throw new InvalidOperationException("Sprite pixel dimensions differ from their reservation.");
        if (!image.IsFrozen) image.Freeze();
        var node=lru.AddLast(key);
        var sprite=new Sprite(image,width,height,raster,node);
        entries.Add(key,sprite); ResidentBytes+=raster.Bytes; Created++; return sprite;
    }
    public void Pin(Sprite sprite)
    {
        if (sprite.Node.List!=lru) throw new InvalidOperationException("Cannot pin an evicted sprite.");
        sprite.References++;
    }
    public void Release(Sprite sprite)
    {
        if (sprite.References<=0) throw new InvalidOperationException("Sprite reference released twice.");
        sprite.References--;
    }
    public void ClearUnused()
    {
        var node=lru.First;
        while (node!=null)
        {
            var next=node.Next;
            if (entries[node.Value].References==0) Remove(node);
            node=next;
        }
    }
    private void Remove(LinkedListNode<string> node)
    {
        ResidentBytes-=entries[node.Value].Raster.Bytes; entries.Remove(node.Value); lru.Remove(node);
    }
}
