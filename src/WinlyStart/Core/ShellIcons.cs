using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinlyStart.Core;

/// <summary>
/// One icon pipeline for desktop shortcuts and packaged apps:
/// IShellItemImageFactory → HBITMAP → GetDIBits (top-down BGRA) → frozen BitmapSource.
/// No System.Drawing, no GDI+.
/// </summary>
public static class ShellIcons
{
    /// <param name="unplate">Strip an opaque "plate" (Windows 10 packaged-app icons) and crop to the glyph —
    /// for tile-sized icons only; see <see cref="Unplate"/>.</param>
    public static BitmapSource? Load(string parsingName, int pixels, bool unplate = false)
    {
        try
        {
            if (Native.CreateShellItem(parsingName) is not Native.IShellItemImageFactory factory) return null;
            var bmp = Fetch(factory, pixels, unplate, out bool plated);
            // A Windows 10 plate carries its glyph at only ~25 % of the square, whatever size is asked
            // (measured 96 → 24 px … 512 → 131 px). Cropped from the size we would normally request the
            // glyph would be upscaled and soft, so fetch a 4× bitmap and crop that instead. Only plated
            // icons pay for the bigger bitmap; Windows 11's unplated glyphs take the first answer.
            if (plated && pixels < 512) bmp = Fetch(factory, Math.Min(512, pixels * 4), unplate, out _) ?? bmp;
            return bmp;
        }
        catch { return null; }
    }

    private static BitmapSource? Fetch(Native.IShellItemImageFactory factory, int pixels, bool unplate, out bool plated)
    {
        plated = false;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            int hr = factory.GetImage(new Native.SIZE(pixels, pixels), Native.SIIGBF_ICONONLY | Native.SIIGBF_BIGGERSIZEOK, out hbm);
            if (hr != 0 || hbm == IntPtr.Zero)
            {
                // some items refuse ICONONLY (e.g. .url with favicon) → take whatever the shell offers
                hr = factory.GetImage(new Native.SIZE(pixels, pixels), Native.SIIGBF_RESIZETOFIT, out hbm);
                if (hr != 0 || hbm == IntPtr.Zero) return null;
            }
            return FromHBitmap(hbm, unplate, out plated);
        }
        finally { if (hbm != IntPtr.Zero) Native.DeleteObject(hbm); }
    }

    /// <summary>Decode an image file directly. Used for cached website favicons: routing those through
    /// the shell returns the generic "picture file" icon rather than the image itself.</summary>
    public static BitmapSource? LoadFile(string path, int pixels)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(path);
            bi.DecodePixelWidth = Math.Max(16, pixels);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    /// <summary>Decode a packed application resource (PNG) at the requested size.</summary>
    public static BitmapSource? LoadResource(string relativePath, int pixels)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri("pack://application:,,,/" + relativePath, UriKind.Absolute);
            bi.DecodePixelWidth = pixels;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private static BitmapSource? FromHBitmap(IntPtr hbm, bool unplate, out bool plated)
    {
        plated = false;
        if (Native.GetObjectW(hbm, Marshal.SizeOf<Native.BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        if (w <= 0 || h <= 0) return null;

        var header = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,          // negative = top-down rows, exactly what WPF wants
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Native.BI_RGB,
        };
        var pixels = new byte[w * h * 4];
        var dc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(dc, hbm, 0, (uint)h, pixels, ref header, Native.DIB_RGB_COLORS) == 0) return null;
        }
        finally { Native.ReleaseDC(IntPtr.Zero, dc); }

        // Legacy 24-bit icons (and a few 32-bit ones drawn without alpha) come back fully transparent.
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        if (unplate && Unplate(pixels, w, h) is { } glyph) { pixels = glyph.Pixels; w = h = glyph.Side; plated = true; }

        // GetDIBits returns STRAIGHT (non-premultiplied) alpha, so the format must be Bgra32.
        // Using Pbgra32 here made edge pixels blend wrong → the fringed/jagged icon borders.
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Windows 10 hands out packaged-app icons "plated": the glyph drawn on an opaque square in the app's
    /// own tile colour (Windows 11 gives the bare glyph on transparency). On our accent tile that square
    /// merges with the plate and only the small glyph stays visible — "the icons look too small". Here
    /// the plate (the colour of all four corners) is made transparent and the bitmap is cropped to the
    /// glyph with a little air, so it scales up like an unplated icon. Returns null when the image does
    /// not look plated: a transparent background, corners that disagree, next to nothing left, or an
    /// opaque picture that fills the square anyway.
    /// </summary>
    private static (byte[] Pixels, int Side)? Unplate(byte[] p, int w, int h)
    {
        int n = w * h;
        if (n < 64) return null;
        int opaque = 0;
        for (int i = 3; i < p.Length; i += 4) if (p[i] == 255) opaque++;
        if (opaque < n * 0.97) return null;

        int c0 = (1 * w + 1) * 4;
        byte pb = p[c0], pg = p[c0 + 1], pr = p[c0 + 2];
        bool Near(int i) => Math.Abs(p[i] - pb) + Math.Abs(p[i + 1] - pg) + Math.Abs(p[i + 2] - pr) <= 36;
        if (!Near((1 * w + w - 2) * 4) || !Near(((h - 2) * w + 1) * 4) || !Near(((h - 2) * w + w - 2) * 4)) return null;

        int kept = 0, minx = w, miny = h, maxx = -1, maxy = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (Near((y * w + x) * 4)) continue;
                kept++;
                if (x < minx) minx = x;
                if (x > maxx) maxx = x;
                if (y < miny) miny = y;
                if (y > maxy) maxy = y;
            }
        if (kept < n * 0.02 || kept > n * 0.85) return null;

        int bw = maxx - minx + 1, bh = maxy - miny + 1, side = Math.Max(bw, bh);
        side = Math.Min(side + Math.Max(2, side / 8), Math.Min(w, h));
        int x0 = Math.Clamp((minx + maxx) / 2 - side / 2, 0, w - side);
        int y0 = Math.Clamp((miny + maxy) / 2 - side / 2, 0, h - side);
        var q = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int s = ((y0 + y) * w + (x0 + x)) * 4, d = (y * side + x) * 4;
                q[d] = p[s]; q[d + 1] = p[s + 1]; q[d + 2] = p[s + 2];
                q[d + 3] = Near(s) ? (byte)0 : p[s + 3];
            }
        return (q, side);
    }
}

/// <summary>
/// Single background STA worker that resolves icons lazily, in the order the UI asks for them.
/// Keeps CPU spikes and the working set low even with hundreds of apps.
/// </summary>
public static class IconLoader
{
    private static readonly BlockingCollection<(AppEntry app, int size)> Queue = new();
    private static readonly ConcurrentDictionary<(string id, int size), bool> Pending = new();
    private static double _dpiScale = 1.0;

    static IconLoader()
    {
        var t = new Thread(Worker) { IsBackground = true, Name = "WinlyStart.Icons", Priority = ThreadPriority.BelowNormal };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>Called by the window whenever its DPI changes so icons are rendered crisp.</summary>
    public static void SetDpiScale(double scale) => _dpiScale = scale <= 0 ? 1.0 : scale;

    public static void Request(AppEntry app, int size)
    {
        if (Pending.TryAdd((app.Id, size), true)) Queue.Add((app, size));
    }

    private static void Worker()
    {
        foreach (var (app, size) in Queue.GetConsumingEnumerable())
        {
            int px = (int)Math.Round(size * _dpiScale);
            var bmp = app.ParsingName.StartsWith("res:", StringComparison.Ordinal)
                ? ShellIcons.LoadResource(app.ParsingName[4..], px)
                : app.ParsingName.StartsWith("img:", StringComparison.Ordinal)
                    ? ShellIcons.LoadFile(app.ParsingName[4..], px)
                    // tile-sized (64+) packaged-app icons lose their Windows 10 plate; the 32 px list icon
                    // keeps it, exactly like the Windows 10 app list
                    : ShellIcons.Load(app.ParsingName, px, unplate: app.IsPackaged && size >= 64);
            Pending.TryRemove((app.Id, size), out _);
            if (bmp == null) continue;
            Application.Current?.Dispatcher.BeginInvoke(() => app.SetIcon(size, bmp));
        }
    }
}
