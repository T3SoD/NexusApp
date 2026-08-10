using System.Runtime.InteropServices;

namespace NexusApp.Services;

/// <summary>
/// A Windows notification-area icon for the main window (issue #46, the "hide to system tray"
/// option). Raw Win32 Shell_NotifyIcon rather than WinForms' NotifyIcon on purpose: this app
/// publishes SELF-CONTAINED win-x64, and turning on UseWindowsForms drags the whole WinForms stack
/// into every portable download for one icon. The csproj already trims 6 MB of satellite resources
/// deliberately, so paying ~15 MB here would undo that and more. The app is already a Win32
/// interop user (per-monitor DPI, overlay placement), so this is the house idiom, not a new one.
///
/// <para>Lifetime: created once when the tray is first needed and disposed on real app exit. The
/// icon hangs off a message-only window (HWND_MESSAGE) that exists solely to receive the callback,
/// so nothing here depends on the main window still being visible - which is the entire point.</para>
///
/// <para>Threading: every method must be called on the UI thread. The window procedure is invoked
/// on whichever thread pumps messages, which for a message-only window created here is that same
/// UI thread, so the events below raise there too and handlers may touch WPF directly.</para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_DESTROY = 0x0002;
    private const int WM_COMMAND = 0x0111;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_APP_TRAY = 0x0400 + 1;   // WM_APP + 1, this icon's callback message

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x01;

    private const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;

    private const int IdOpen = 1, IdExit = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private delegate IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint flags, int idNewItem, string? item);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hWnd, IntPtr lptpm);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private readonly WndProc _proc;   // held so the delegate is not collected while Windows holds it
    private readonly IntPtr _hwnd;
    private IntPtr _icon;
    private NOTIFYICONDATA _data;
    private bool _added, _disposed;

    /// <summary>The user asked for the window back (double-click, or Open on the menu).</summary>
    public event Action? OpenRequested;
    /// <summary>The user chose Exit on the tray menu. The app must actually quit.</summary>
    public event Action? ExitRequested;

    public TrayIcon(string tooltip)
    {
        _proc = Wnd;
        var hInstance = GetModuleHandle(null);
        var cls = new WNDCLASS
        {
            lpfnWndProc = _proc,
            hInstance = hInstance,
            lpszClassName = "NexusTrayHost_" + Guid.NewGuid().ToString("N"),
            lpszMenuName = "",
        };
        RegisterClass(ref cls);
        // HWND_MESSAGE (-3): a message-only window. No pixels, no taskbar presence, and it survives
        // the main window being hidden, which is exactly what the tray option needs.
        _hwnd = CreateWindowEx(0, cls.lpszClassName, "Nexus tray", 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, hInstance, IntPtr.Zero);

        _icon = LoadAppIcon();
        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _icon,
            szTip = Truncate(tooltip, 127),
            szInfo = "",
            szInfoTitle = "",
        };
    }

    /// <summary>Puts the icon in the notification area. Safe to call twice.</summary>
    public void Show()
    {
        if (_disposed || _added) return;
        _added = Shell_NotifyIcon(NIM_ADD, ref _data);
        Logger.Info(_added ? "[WIN] tray icon shown" : "[WIN] tray icon could not be added");
    }

    /// <summary>A balloon explaining where the window went. Shown once ever (AppSettings.TrayHintShown)
    /// because an app that vanishes with no explanation reads as a crash.</summary>
    public void ShowHint(string title, string message)
    {
        if (_disposed || !_added) return;
        var d = _data;
        d.uFlags = NIF_INFO;
        d.szInfoTitle = Truncate(title, 63);
        d.szInfo = Truncate(message, 255);
        d.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added) { Shell_NotifyIcon(NIM_DELETE, ref _data); _added = false; }
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        Logger.Info("[WIN] tray icon removed");
    }

    private IntPtr Wnd(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            var mouse = (int)(lParam.ToInt64() & 0xFFFF);
            // A single left click restores too: the tray convention is double-click, but a single
            // click is what most people try first and there is nothing else for it to mean here.
            if (mouse is WM_LBUTTONUP or WM_LBUTTONDBLCLK) OpenRequested?.Invoke();
            else if (mouse == WM_RBUTTONUP) ShowMenu();
            return IntPtr.Zero;
        }
        if (msg == WM_COMMAND)
        {
            var id = (int)(wParam.ToInt64() & 0xFFFF);
            if (id == IdOpen) OpenRequested?.Invoke();
            else if (id == IdExit) ExitRequested?.Invoke();
            return IntPtr.Zero;
        }
        if (msg == WM_DESTROY) return IntPtr.Zero;
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MF_STRING, IdOpen, "Open Nexus");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, IdExit, "Exit Nexus");
            GetCursorPos(out var pt);
            // Documented requirement: without this the menu does not dismiss when the user clicks
            // away from it, because the owning window is not in the foreground.
            SetForegroundWindow(_hwnd);
            var chosen = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            if (chosen == IdOpen) OpenRequested?.Invoke();
            else if (chosen == IdExit) ExitRequested?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }

    // The app's own icon, taken from the running exe so it always matches the shipped Assets\nexus.ico
    // without embedding a second copy. IntPtr.Zero (extraction failed) is survivable: Windows draws a
    // blank slot rather than refusing the icon, and the menu still works.
    private static IntPtr LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return IntPtr.Zero;
            var small = new IntPtr[1];
            ExtractIconEx(exe, 0, null, small, 1);
            return small[0];
        }
        catch (Exception ex)
        {
            Logger.Info($"[WIN] tray icon art unavailable: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max];
}
