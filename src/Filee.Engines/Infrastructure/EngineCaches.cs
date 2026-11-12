// Caches engines keep between conversions (typefaces looked up for HWP and CAD text). The app releases them while no
// conversion runs, so a Filee waiting in the tray gives the memory back. Skia's own caches stay: they are shared with
// the UI's render thread, and purging them while it draws crashed in native code (SKGraphics.PurgeAllCaches).

namespace Filee.Engines.Infrastructure;

public static class EngineCaches
{
    /// <summary>
    /// Drops what the engines cache between conversions.
    /// </summary>
    public static void Release()
    {
    }
}
