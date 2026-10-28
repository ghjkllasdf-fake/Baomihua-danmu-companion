using System;

namespace Popcorn;

internal static class ProbeCadence
{
    public static int DelayMilliseconds(double started,double finished)
    {
        if(!double.IsFinite(started) || !double.IsFinite(finished) || finished<started) return 200;
        return (int)Math.Ceiling(Math.Clamp(200-(finished-started),0,200));
    }
}
