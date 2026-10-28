using System;
using System.Threading;

namespace Popcorn;

internal enum PlayerControlKind { Other,Text,Button }
internal sealed record PlayerControlRange(double Value,double Minimum,double Maximum);
internal sealed record PlayerControlValue(string Id,string Name,PlayerControlKind Kind,PlayerControlRange Range,double StampMs);

internal sealed class PlayerControlCache<T>(Func<VideoWindow,T[]> discover,Func<T,PlayerControlValue> read,Func<double> now,CancellationToken stop=default)
{
    private T[] elements;
    private IntPtr handle;
    private string title;
    private double lastObserved=double.NaN,expiresAt;
    public void Invalidate() { elements=null; handle=IntPtr.Zero; title=null; lastObserved=double.NaN; }
    public PlayerControlValue[] Read(VideoWindow window)
    {
        if(window==null) { Invalidate(); return Array.Empty<PlayerControlValue>(); }
        double stamp=now();
        try
        {
            stop.ThrowIfCancellationRequested();
            if(elements==null || window.Handle!=handle || window.Title!=title || stamp<lastObserved || stamp>=expiresAt)
            {
                elements=discover(window);
                handle=window.Handle; title=window.Title;
                // Cache references, never time/playing values. Periodic discovery also
                // limits readable detached peers; actual response also depends on poll work.
                expiresAt=stamp+(elements.Length==0 ? 200 : 400);
            }
            lastObserved=stamp;
            var values=new PlayerControlValue[elements.Length]; int count=0; double began=now();
            if(began<lastObserved) { Invalidate(); return Array.Empty<PlayerControlValue>(); }
            lastObserved=began;
            while(count<elements.Length)
            {
                double tick=now();
                if(tick<lastObserved) { Invalidate(); return Array.Empty<PlayerControlValue>(); }
                lastObserved=tick;
                if(tick-began>150) break;
                stop.ThrowIfCancellationRequested(); values[count]=read(elements[count]); count++;
            }
            double finished=now();
            if(finished<lastObserved) { Invalidate(); return Array.Empty<PlayerControlValue>(); }
            lastObserved=finished;
            stop.ThrowIfCancellationRequested();
            if(count!=values.Length) Array.Resize(ref values,count);
            return values;
        }
        catch { Invalidate(); throw; }
    }
}
