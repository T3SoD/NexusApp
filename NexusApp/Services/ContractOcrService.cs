using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace NexusApp.Services;

// This service deliberately DUPLICATES the GDI capture (CaptureRegion),
// the SoftwareBitmap conversion (ToSoftwareBitmap), the BITMAPINFOHEADER
// struct, the constants, and the [DllImport] declarations from OcrService.
// OcrService is the LOCKED, proven Rock/RS scan path and must NOT be modified,
// so the pieces this contract-panel scanner needs are copied verbatim here.
// The only intentional difference is a lighter Preprocess pass (scale = 2
// instead of 6): a contract panel is large, and a 6x upscale would produce a
// multi-megapixel bitmap that is slow to OCR. Invert + x1.4 contrast + 24px
// padding are kept identical to the RS path.

public sealed class ContractOcrService : IDisposable
{
    private OcrEngine? _engine;
    private bool _available;

    public bool IsAvailable => _available;

    private int  _x, _y, _w, _h;
    private bool _hasRegion;

    public void SetRegion(int x, int y, int w, int h)
    {
        _x = x; _y = y; _w = w; _h = h;
        _hasRegion = w > 0 && h > 0;
    }

    public ContractOcrService()
    {
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                   ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            _available = _engine != null;
        }
        catch { _available = false; }
    }

    public async Task<string?> ScanRegionTextAsync()
    {
        if (!IsAvailable || _engine is null || !_hasRegion) return null;

        var raw = CaptureRegion(_x, _y, _w, _h);
        if (raw is null) return null;

        try
        {
            var processed = Preprocess(raw, _w, _h, out int pw, out int ph);
            var softBmp   = ToSoftwareBitmap(processed, pw, ph);
            var result    = await _engine.RecognizeAsync(softBmp);
            return result.Text;
        }
        catch { }

        return null;
    }

    // ── Preprocessing ──────────────────────────────────────────────────────────
    // Same invert + x1.4 contrast + 24px padding as OcrService.Preprocess, but
    // scale = 2 (not 6). Contract-panel glyphs are already large; a 6x upscale
    // would be multi-megapixel and slow to recognize.

    private static byte[] Preprocess(byte[] bgra, int w, int h, out int outW, out int outH)
    {
        const int scale   = 2;
        const int padding = 24;

        outW = w * scale + padding * 2;
        outH = h * scale + padding * 2;

        var output = new byte[outW * outH * 4];
        Array.Fill(output, (byte)255);

        for (int sy = 0; sy < h; sy++)
            for (int sx = 0; sx < w; sx++)
            {
                int src = (sy * w + sx) * 4;

                byte ib = (byte)Math.Min(255, (255 - bgra[src])     * 14 / 10);
                byte ig = (byte)Math.Min(255, (255 - bgra[src + 1]) * 14 / 10);
                byte ir = (byte)Math.Min(255, (255 - bgra[src + 2]) * 14 / 10);

                for (int dy = 0; dy < scale; dy++)
                    for (int dx = 0; dx < scale; dx++)
                    {
                        int dstX = sx * scale + dx + padding;
                        int dstY = sy * scale + dy + padding;
                        int dst  = (dstY * outW + dstX) * 4;
                        output[dst]     = ib;
                        output[dst + 1] = ig;
                        output[dst + 2] = ir;
                        output[dst + 3] = 255;
                    }
            }

        return output;
    }

    // ── Capture / convert (copied verbatim from OcrService) ──────────────────────

    private static unsafe byte[]? CaptureRegion(int x, int y, int w, int h)
    {
        var hdc    = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;
        var hdcMem = CreateCompatibleDC(hdc);
        var hBmp   = CreateCompatibleBitmap(hdc, w, h);
        var hOld   = SelectObject(hdcMem, hBmp);
        BitBlt(hdcMem, 0, 0, w, h, hdc, x, y, SRCCOPY);
        var bmpInfo = new BITMAPINFOHEADER
        {
            biSize     = (uint)sizeof(BITMAPINFOHEADER),
            biWidth    = (uint)w, biHeight = -h,
            biPlanes   = 1, biBitCount = 32, biCompression = 0
        };
        var buf = new byte[w * h * 4];
        fixed (byte* p = buf)
            GetDIBits(hdcMem, hBmp, 0, (uint)h, (IntPtr)p, ref bmpInfo, DIB_RGB_COLORS);
        SelectObject(hdcMem, hOld);
        DeleteObject(hBmp);
        DeleteDC(hdcMem);
        ReleaseDC(IntPtr.Zero, hdc);
        return buf;
    }

    private static SoftwareBitmap ToSoftwareBitmap(byte[] bgra, int w, int h)
    {
        var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Ignore);
        bmp.CopyFromBuffer(bgra.AsBuffer());
        return bmp;
    }

    public void Dispose() { }

    // ── P/Invoke (copied verbatim from OcrService) ───────────────────────────────

    private const int  SRCCOPY        = 0xCC0020;
    private const uint DIB_RGB_COLORS = 0;

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool   DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool   DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool   BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, int rop);
    [DllImport("gdi32.dll")] static extern int    GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, IntPtr bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int   ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize, biWidth; public int biHeight;
        public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }
}
