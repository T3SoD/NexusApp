using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Nexus_v4.Services;

public class OcrService : IDisposable
{
    private OcrEngine? _engine;
    private bool _available;

    public bool IsAvailable => _available;
    public bool LastScanHadRegion { get; private set; }

    private int  _regX, _regY, _regW, _regH;
    private bool _hasRegion;

    public void SetRegion(int x, int y, int w, int h)
    {
        _regX = x; _regY = y; _regW = w; _regH = h;
        _hasRegion = w > 0 && h > 0;
    }

    public OcrService()
    {
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                   ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            _available = _engine != null;
        }
        catch { _available = false; }
    }

    public async Task<(int? Value, bool PinFound)> ScanFullScreenAsync()
    {
        if (!_available || _engine == null) return (null, false);

        byte[]? raw;
        int bw, bh;

        if (_hasRegion)
        {
            // Use stored coordinates — capture only the known scan region.
            raw = CaptureRegion(_regX, _regY, _regW, _regH);
            if (raw == null) return (null, false);
            LastScanHadRegion = true;
            bw = _regW; bh = _regH;
        }
        else
        {
            // Fallback: find the magenta scan-box border on the full screen.
            var fw = GetSystemMetrics(SM_CXSCREEN);
            var fh = GetSystemMetrics(SM_CYSCREEN);
            if (fw <= 0 || fh <= 0) return (null, false);

            var full = CaptureRegion(0, 0, fw, fh);
            if (full == null) return (null, false);

            var box = FindMagentaRegion(full, fw, fh);
            LastScanHadRegion = box != null;
            if (box == null) return (null, false);

            var (bx, by, _bw, _bh) = box.Value;
            raw = ExtractSubRegion(full, fw, bx, by, _bw, _bh);
            bw = _bw; bh = _bh;
        }

        try
        {
            var processed = Preprocess(raw!, bw, bh, out int pw, out int ph);
            var softBmp   = ToSoftwareBitmap(processed, pw, ph);
            var result    = await _engine.RecognizeAsync(softBmp);
            var val       = ExtractRsValue(result.Text);
            return (val, val.HasValue);
        }
        catch { }

        return (null, false);
    }

    // ── Preprocessing ──────────────────────────────────────────────────────────
    // Invert (dark navy → white, white text → black) and scale up 6× so each
    // glyph is large enough for the OCR engine to cleanly distinguish 6/8/9.
    // Anti-aliasing is preserved — Windows OCR reads smooth glyphs better than
    // hard-binarized pixel art.

    private static byte[] Preprocess(byte[] bgra, int w, int h, out int outW, out int outH)
    {
        const int scale   = 6;
        const int padding = 24;

        outW = w * scale + padding * 2;
        outH = h * scale + padding * 2;

        var output = new byte[outW * outH * 4];
        Array.Fill(output, (byte)255);

        for (int sy = 0; sy < h; sy++)
            for (int sx = 0; sx < w; sx++)
            {
                int src = (sy * w + sx) * 4;

                // Invert then boost contrast ×1.4 so ambiguous mid-gray pixels
                // (the open gap at the top of "6") are pushed clearly toward
                // white and away from the dark text band.
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

    // ── Value extraction ───────────────────────────────────────────────────────

    // Matches "X XXX" (single digit, space, exactly 3 digits) with no surrounding digits —
    // covers 2,000–9,999 where OCR reads the thousands comma as a space.
    private static readonly Regex _splitThousands = new(@"(?<!\d)(\d) (\d{3})(?!\d)", RegexOptions.Compiled);

    private static int? ExtractRsValue(string text)
    {
        // Strip commas/periods sitting between two digit characters ("17,200" → "17200")
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((c == ',' || c == '.') &&
                i > 0 && i < text.Length - 1 &&
                char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]))
                continue;
            sb.Append(c);
        }

        // Collapse "X XXX" → "XXXX" for cases where OCR reads the comma as a space
        var normalized = _splitThousands.Replace(sb.ToString(), "$1$2");

        // Find first continuous digit run of 4–6 chars in valid RS range
        int start = -1;
        for (int i = 0; i <= normalized.Length; i++)
        {
            bool isDigit = i < normalized.Length && char.IsDigit(normalized[i]);
            if (isDigit && start == -1)
            {
                start = i;
            }
            else if (!isDigit && start != -1)
            {
                int len = i - start;
                if (len >= 4 && len <= 6 &&
                    int.TryParse(normalized.AsSpan(start, len), out var val) &&
                    val >= 2000 && val <= 200000)
                    return val;
                start = -1;
            }
        }
        return null;
    }

    // ── Capture / convert ──────────────────────────────────────────────────────

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

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static byte[] ExtractSubRegion(byte[] src, int srcW, int rx, int ry, int rw, int rh)
    {
        var dst = new byte[rw * rh * 4];
        for (int y = 0; y < rh; y++)
            Array.Copy(src, ((ry + y) * srcW + rx) * 4, dst, y * rw * 4, rw * 4);
        return dst;
    }

    private static (int x, int y, int w, int h)? FindMagentaRegion(byte[] raw, int sw, int sh)
    {
        int minX = sw, maxX = 0, minY = sh, maxY = 0;
        bool found = false;
        for (int y = 0; y < sh; y += 2)
            for (int x = 0; x < sw; x += 2)
            {
                int i = (y * sw + x) * 4;
                if (!IsMagenta(raw[i + 2], raw[i + 1], raw[i])) continue;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                found = true;
            }
        if (!found || maxX - minX < 20 || maxY - minY < 10) return null;
        const int border = 4;
        int ix = minX + border, iy = minY + border;
        int iw = maxX - minX - border * 2, ih = maxY - minY - border * 2;
        if (iw <= 0 || ih <= 0) return null;
        return (ix, iy, iw, ih);
    }

    private static bool IsMagenta(byte r, byte g, byte b) => r > 200 && g < 30 && b > 200;

    // ── P/Invoke ───────────────────────────────────────────────────────────────

    private const int  SRCCOPY        = 0xCC0020;
    private const uint DIB_RGB_COLORS = 0;
    private const int  SM_CXSCREEN    = 0;
    private const int  SM_CYSCREEN    = 1;

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool   DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool   DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool   BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, int rop);
    [DllImport("gdi32.dll")] static extern int    GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, IntPtr bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int   ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] static extern int   GetSystemMetrics(int n);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize, biWidth; public int biHeight;
        public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }
}
