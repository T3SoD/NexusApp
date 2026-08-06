using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace NexusApp.Services;

// This service deliberately DUPLICATES the GDI capture (CaptureRegion),
// the SoftwareBitmap conversion (ToSoftwareBitmap), the BITMAPINFOHEADER
// struct, the constants, and the [DllImport] declarations from OcrService.
// OcrService is the LOCKED, proven Rock/RS scan path and must NOT be modified,
// so the pieces this wallet scanner needs are copied verbatim here, the same
// arrangement ContractOcrService documents. Preprocess is scale = 6, invert,
// 24px padding, and NO contrast boost: live calibration (2026-08-06, two real
// mobiGlas samples) proved the RS path's x1.4 contrast saturates the balance
// digits' glow after inversion - OCR then reads the player handle line and
// misses the number entirely. With the boost removed, both samples read the
// full balance. The wallet region is a small digit strip, so the heavy
// upscale is affordable and the small mobiGlas glyphs need it.

public sealed class WalletOcrService : IDisposable
{
    private OcrEngine? _engine;
    private bool _available;

    public bool IsAvailable => _available;

    private int  _x, _y, _w, _h;
    private bool _hasRegion;

    public bool HasRegion => _hasRegion;

    public void SetRegion(int x, int y, int w, int h)
    {
        _x = x; _y = y; _w = w; _h = h;
        _hasRegion = w > 0 && h > 0;
    }

    public WalletOcrService()
    {
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                   ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            _available = _engine != null;
        }
        catch { _available = false; }
    }

    // Dual recognition (calibration rounds 2-3, 2026-08-06): the SAME captured pixels run
    // through OCR twice, inverted and plain, and WalletOcrTrigger.BestRead grades the pair:
    // agreement counts, a parse disagreement rejects the grab (the misread signal), and a
    // single reading pass stands alone because the burst's cross-grab agreement guards it.
    // The region is a small strip, so the second pass is cheap.
    public async Task<string?> ScanRegionTextAsync()
    {
        if (!IsAvailable || _engine is null || !_hasRegion) return null;

        var raw = CaptureRegion(_x, _y, _w, _h);
        if (raw is null) return null;

        try
        {
            var inverted = Preprocess(raw, _w, _h, invert: true, out int iw, out int ih);
            var plain    = Preprocess(raw, _w, _h, invert: false, out int pw2, out int ph2);
            var a = (await _engine.RecognizeAsync(ToSoftwareBitmap(inverted, iw, ih))).Text;
            var b = (await _engine.RecognizeAsync(ToSoftwareBitmap(plain, pw2, ph2))).Text;
            return WalletOcrTrigger.BestRead(a, b);
        }
        catch { }

        return null;
    }

    // ── Preprocessing ──────────────────────────────────────────────────────────
    // 24px padding + scale = 6 as the RS path, but WITHOUT its x1.4 contrast
    // boost: calibrated against real mobiGlas captures (see the class header).
    // Both polarities exist because dual recognition runs each grab through the
    // engine twice: inverted (dark digits on light) and plain (as captured).

    private static byte[] Preprocess(byte[] bgra, int w, int h, bool invert, out int outW, out int outH)
    {
        const int scale   = 6;
        const int padding = 24;

        outW = w * scale + padding * 2;
        outH = h * scale + padding * 2;

        var output = new byte[outW * outH * 4];
        Array.Fill(output, invert ? (byte)255 : (byte)0);

        for (int sy = 0; sy < h; sy++)
            for (int sx = 0; sx < w; sx++)
            {
                int src = (sy * w + sx) * 4;

                byte ib = invert ? (byte)(255 - bgra[src]) : bgra[src];
                byte ig = invert ? (byte)(255 - bgra[src + 1]) : bgra[src + 1];
                byte ir = invert ? (byte)(255 - bgra[src + 2]) : bgra[src + 2];

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
