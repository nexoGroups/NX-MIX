using System.Diagnostics;
using System.Runtime.InteropServices;
using NxMix.Audio;
using NxMix.Ipc;

namespace NxMix.Daemon;

/// <summary>Schlankes natives Win32-System-Tray-Icon (Shell_NotifyIconW) für den NX-MIX Hintergrunddienst.</summary>
internal sealed unsafe class TrayIconHost : IDisposable
{
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_USER = 0x0400;
    private const uint WM_TRAYICON = WM_USER + 42;
    private const uint WM_QUIT_LOOP = WM_USER + 43;

    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x0000;
    private const uint MF_CHECKED = 0x0008;
    private const uint MF_GRAYED = 0x0001;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const int CMD_OPEN_UI = 1001;
    private const int CMD_PANIC_MUTE = 1002;
    private const int CMD_TOGGLE_AUDIO = 1003;
    private const int CMD_QUIT_DAEMON = 1004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIconW(nint hInstance, nint lpIconName);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    private static TrayIconHost? s_instance;
    private readonly DaemonEngineController _engine;
    private nint _hwnd;
    private nint _icon;
    private bool _iconAdded;

    public TrayIconHost(DaemonEngineController engine)
    {
        _engine = engine;
        s_instance = this;
        _engine.SnapshotUpdated += _ => UpdateTooltip();
    }

    public void RunMessageLoop()
    {
        const string className = "NxMixDaemonTrayWnd";
        nint hInst = GetModuleHandleW(null);
        // Versuche eingebettetes Anwendungs-Icon (#32512 = IDI_APPLICATION in Exe, sonst Fallback System-Icon)
        _icon = LoadIconW(hInst, 32512);
        if (_icon == 0) _icon = LoadIconW(0, 32512);

        fixed (char* clsPtr = className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = hInst,
                lpszClassName = clsPtr,
            };
            RegisterClassExW(ref wc);
        }

        _hwnd = CreateWindowExW(0, className, "NX-MIX Audio Engine", 0, 0, 0, 0, 0, 0, 0, hInst, 0);
        AddOrUpdateTrayIcon(NIM_ADD);

        while (GetMessageW(out MSG msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_QUIT_LOOP) break;
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        RemoveTrayIcon();
        if (_hwnd != 0)
        {
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }

    public void RequestExit()
    {
        if (_hwnd != 0)
            PostMessageW(_hwnd, WM_QUIT_LOOP, 0, 0);
    }

    private void UpdateTooltip()
    {
        if (_hwnd != 0 && _iconAdded)
            AddOrUpdateTrayIcon(NIM_MODIFY);
    }

    private void AddOrUpdateTrayIcon(uint nimAction)
    {
        var snap = _engine.Snapshot;
        string stateTxt = snap.PanicMuted
            ? "PANIC MUTE AKTIV"
            : snap.HostStats.State == HostState.Running
                ? $"Aktiv · {snap.Config.SampleRate / 1000.0:0.#} kHz · {snap.HostStats.BlockFrames} Samples"
                : $"Bereit ({snap.HostStats.State})";
        string tip = $"NX-MIX Audio Engine — {stateTxt}";

        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _icon,
        };
        int max = Math.Min(tip.Length, 126);
        for (int i = 0; i < max; i++) nid.szTip[i] = tip[i];
        nid.szTip[max] = '\0';

        if (Shell_NotifyIconW(nimAction, ref nid))
            _iconAdded = true;
    }

    private void RemoveTrayIcon()
    {
        if (!_iconAdded || _hwnd == 0) return;
        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
        };
        Shell_NotifyIconW(NIM_DELETE, ref nid);
        _iconAdded = false;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static nint WndProc(nint hWnd, uint msg, nuint wParam, nint lParam)
    {
        var self = s_instance;
        if (self != null)
        {
            if (msg == WM_TRAYICON)
            {
                uint ev = (uint)(lParam & 0xFFFF);
                if (ev == WM_LBUTTONDBLCLK)
                {
                    self.LaunchUiApp();
                    return 0;
                }
                if (ev is WM_RBUTTONUP or WM_CONTEXTMENU)
                {
                    self.ShowContextMenu(hWnd);
                    return 0;
                }
            }
            else if (msg == WM_QUIT_LOOP)
            {
                PostQuitMessage(0);
                return 0;
            }
            else if (msg == WM_DESTROY)
            {
                PostQuitMessage(0);
                return 0;
            }
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu(nint hWnd)
    {
        var snap = _engine.Snapshot;
        bool running = snap.HostStats.State == HostState.Running;

        nint menu = CreatePopupMenu();
        string header = running
            ? $"NX-MIX Engine: Läuft ({snap.Config.SampleRate} Hz, {snap.HostStats.OutputLatencyMs:0.0} ms)"
            : $"NX-MIX Engine: {snap.HostStats.State}";

        AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, header);
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, CMD_OPEN_UI, "NX-MIX Oberfläche öffnen");
        AppendMenuW(menu, MF_STRING | (snap.PanicMuted ? MF_CHECKED : 0), CMD_PANIC_MUTE, "Panic-Mute (Alle Ausgänge stumm)");
        AppendMenuW(menu, MF_STRING, CMD_TOGGLE_AUDIO, running ? "Audio-Streaming stoppen" : "Audio-Streaming starten");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, CMD_QUIT_DAEMON, "NX-MIX Engine vollständig beenden");

        GetCursorPos(out POINT pt);
        SetForegroundWindow(hWnd);
        int cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, 0, hWnd, 0);
        DestroyMenu(menu);

        switch (cmd)
        {
            case CMD_OPEN_UI:
                LaunchUiApp();
                break;
            case CMD_PANIC_MUTE:
                _ = _engine.SetPanicMuteAsync(!snap.PanicMuted);
                break;
            case CMD_TOGGLE_AUDIO:
                _ = running ? _engine.StopAudioAsync() : _engine.StartAudioAsync();
                break;
            case CMD_QUIT_DAEMON:
                RequestExit();
                break;
        }
    }

    private void LaunchUiApp()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string candidate = Path.Combine(baseDir, "NX-MIX.exe");
            if (!File.Exists(candidate))
            {
                // Entwicklungs-Fallback (Geschwister-Ausgabeordner Release / Debug)
                foreach (string cfg in new[] { "Release", "Debug" })
                {
                    string dev = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "NxMix.App", "bin", cfg, "net10.0-windows", "NX-MIX.exe"));
                    if (File.Exists(dev)) { candidate = dev; break; }
                }
            }
            if (File.Exists(candidate))
            {
                var psi = new ProcessStartInfo(candidate)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(candidate),
                };
                if (!_engine.Paths.IsDefaultUserRoot)
                    psi.Arguments = $"--data-dir \"{_engine.Paths.RootDir}\"";
                Process.Start(psi);
            }
        }
        catch
        {
        }
    }

    public void Dispose() => RemoveTrayIcon();
}
