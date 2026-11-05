using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NxMix.App.Model;
using NxMix.App.Ui;
using Vortice.Direct2D1;
using Vortice.DCommon;
using Vortice.DXGI;
using Vortice.Mathematics;
using static NxMix.App.Native.Win32;

namespace NxMix.App.Native;

/// <summary>
/// Das Hauptfenster in reinem Win32: rahmenlos mit eigener Titelleiste (WM_NCCALCSIZE/WM_NCHITTEST, Aero-Snap, Snap-Layouts),
/// DPI PerMonitorV2, Direct2D-HwndRenderTarget, Neuzeichnen nur bei Bedarf (Timer ~60 Hz).
/// </summary>
internal sealed unsafe class MainWindow : IShell
{
    const string ClassName = "NxMixMainWindow";
    const nuint TimerId = 1;
    const uint D2DERR_RECREATE_TARGET = 0x8899000C;

    static MainWindow? s_instance;

    readonly AppModel _model;
    readonly ConcurrentQueue<Action> _posted = new();
    UiRoot? _root;
    nint _hwnd;
    ID2D1HwndRenderTarget? _rt;
    Renderer? _g;
    uint _dpi = 96;
    float _scale = 1f;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastTick;
    bool _trackLeave, _trackNcLeave;
    bool _minimized;
    double _normalW = Theme.DefaultWindowWidth, _normalH = Theme.DefaultWindowHeight;
    int _exitAfterMs;
    readonly WindowDto? _initial;
    readonly UiPrefs _prefs;
    const uint WM_SETTINGCHANGE = 0x001A;

    public MainWindow(AppModel model, WindowDto? initial, int exitAfterMs, UiPrefs prefs)
    {
        _model = model;
        _initial = initial;
        _exitAfterMs = exitAfterMs;
        _prefs = prefs;
    }

    // ---- IShell --------------------------------------------------------------------------------
    public void Minimize() => ShowWindow(_hwnd, SW_MINIMIZE);
    public void ToggleMaximize() => ShowWindow(_hwnd, IsZoomed(_hwnd) ? SW_RESTORE : SW_SHOWMAXIMIZED);
    public void Close() => PostMessage(_hwnd, WM_CLOSE, 0, 0);
    public bool IsMaximized => _hwnd != 0 && IsZoomed(_hwnd);
    public void SetCapture(bool on) { if (on) Win32.SetCapture(_hwnd); else ReleaseCapture(); }
    public string? ShowSaveDialog(string title, string filter, string defaultName) => FileDialogs.Save(_hwnd, title, filter, defaultName);
    public string? ShowOpenDialog(string title, string filter) => FileDialogs.Open(_hwnd, title, filter);
    public WindowDto? WindowState => new() { Width = Math.Round(_normalW), Height = Math.Round(_normalH), Maximized = IsMaximized };

    /// <summary>Windows-Einstellung "Standard-App-Modus" (Registry: AppsUseLightTheme = 0 heisst dunkel).</summary>
    public bool SystemPrefersDark
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v ? v == 0 : true;
            }
            catch { return true; }
        }
    }

    public void ThemeChanged()
    {
        if (_hwnd == 0) return;
        ApplyFrameTheme();
        InvalidateRect(_hwnd, 0, false);
    }

    /// <summary>Fensterrahmen passend zum Farbmodus: dunkler/heller Rahmen, runde Ecken, Randfarbe.</summary>
    void ApplyFrameTheme()
    {
        int dark = Theme.IsDark ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int corner = 2;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        var bc = Theme.Border;
        int border = ((int)(bc.B * 255) << 16) | ((int)(bc.G * 255) << 8) | (int)(bc.R * 255);
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    // ---- Start ---------------------------------------------------------------------------------
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var w = s_instance;
        if (w == null) return DefWindowProc(hwnd, msg, wParam, lParam);
        try { return w.Handle(hwnd, msg, wParam, lParam); }
        catch (Exception ex)
        {
            Program.LogCrash(ex);
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    public int Run()
    {
        s_instance = this;
        nint hInst = GetModuleHandle(null);
        fixed (char* cls = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = 0x0008, // CS_DBLCLKS
                lpfnWndProc = &WndProc,
                hInstance = hInst,
                hCursor = Cursor(IDC_ARROW),
                hbrBackground = 0,
                lpszClassName = cls,
            };
            if (RegisterClassEx(in wc) == 0) throw new InvalidOperationException("RegisterClassEx fehlgeschlagen: " + Marshal.GetLastWin32Error());
        }

        const uint style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_CLIPCHILDREN;
        _hwnd = CreateWindowEx(WS_EX_APPWINDOW, ClassName, "NX-MIX", style, CW_USEDEFAULT, CW_USEDEFAULT, 1280, 720, 0, 0, hInst, 0);
        if (_hwnd == 0) throw new InvalidOperationException("CreateWindowEx fehlgeschlagen: " + Marshal.GetLastWin32Error());

        _dpi = GetDpiForWindow(_hwnd);
        if (_dpi == 0) _dpi = 96;
        _scale = _dpi / 96f;

        // Schatten fuer den rahmenlosen Stil; Rahmenfarbe und Ecken folgen dem Farbmodus
        var margins = new MARGINS { Top = 1 };
        DwmExtendFrameIntoClientArea(_hwnd, in margins);

        _model.Post = a => { _posted.Enqueue(a); PostMessage(_hwnd, WM_APP, 0, 0); };
        _root = new UiRoot(_model, this, _prefs);
        ApplyFrameTheme();

        // Groesse und Lage
        double wDip = _initial?.Width > 300 ? _initial.Width : Theme.DefaultWindowWidth;
        double hDip = _initial?.Height > 300 ? _initial.Height : Theme.DefaultWindowHeight;
        _normalW = wDip; _normalH = hDip;
        var mon = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        GetMonitorInfo(mon, ref mi);
        int wpx = (int)Math.Min(wDip * _scale, mi.rcWork.Width), hpx = (int)Math.Min(hDip * _scale, mi.rcWork.Height);
        int x = mi.rcWork.Left + (mi.rcWork.Width - wpx) / 2, y = mi.rcWork.Top + (mi.rcWork.Height - hpx) / 2;
        SetWindowPos(_hwnd, 0, x, y, wpx, hpx, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        SetTimer(_hwnd, TimerId, 16, 0);
        ShowWindow(_hwnd, _initial?.Maximized == true ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL);
        UpdateWindow(_hwnd);
        _lastTick = _clock.Elapsed.TotalSeconds;
        _model.RefreshDevices();

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(in msg);
            DispatchMessage(in msg);
        }
        return 0;
    }

    // ---- Direct2D ------------------------------------------------------------------------------
    void CreateTarget()
    {
        _g?.Dispose(); _g = null;
        _rt?.Dispose(); _rt = null;
        GetClientRect(_hwnd, out var rc);
        var props = new RenderTargetProperties(RenderTargetType.Default, new Vortice.DCommon.PixelFormat(Format.Unknown, Vortice.DCommon.AlphaMode.Unknown),
            _dpi, _dpi, RenderTargetUsage.None, FeatureLevel.Default);
        var hp = new HwndRenderTargetProperties
        {
            Hwnd = _hwnd,
            PixelSize = new SizeI(Math.Max(1, rc.Width), Math.Max(1, rc.Height)),
            PresentOptions = PresentOptions.None,
        };
        _rt = Renderer.D2D.CreateHwndRenderTarget(props, hp);
        _g = new Renderer(_rt, _scale);
    }

    void Render()
    {
        if (_root == null || _minimized) return;
        try
        {
            if (_rt == null) CreateTarget();
            _rt!.BeginDraw();
            _root.Paint(_g!);
            var hr = _rt.EndDraw();
            if (hr.Failure)
            {
                if ((uint)hr.Code == D2DERR_RECREATE_TARGET) { _g?.Dispose(); _g = null; _rt.Dispose(); _rt = null; InvalidateRect(_hwnd, 0, false); }
            }
        }
        catch (Exception ex)
        {
            Program.LogCrash(ex);
            _g?.Dispose(); _g = null; _rt?.Dispose(); _rt = null;
        }
    }

    void ResizeTarget()
    {
        if (_root == null) return;
        GetClientRect(_hwnd, out var rc);
        if (rc.Width <= 0 || rc.Height <= 0) return;
        if (_rt == null) CreateTarget();
        else _rt.Resize(new SizeI(rc.Width, rc.Height));
        _root.Resize(rc.Width / _scale, rc.Height / _scale);
    }

    // ---- Nachrichten ---------------------------------------------------------------------------
    static bool Ctrl => KeyDown(VK_CONTROL);
    static bool Shift => KeyDown(VK_SHIFT);
    static bool Alt => KeyDown(VK_MENU);

    nint Handle(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_NCCALCSIZE:
                if (wParam != 0)
                {
                    var p = (NCCALCSIZE_PARAMS*)lParam;
                    if (IsZoomed(hwnd))
                    {
                        int fx = GetSystemMetricsForDpi(SM_CXSIZEFRAME, _dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, _dpi);
                        int fy = GetSystemMetricsForDpi(SM_CYSIZEFRAME, _dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, _dpi);
                        p->rgrc0.Left += fx; p->rgrc0.Right -= fx; p->rgrc0.Top += fy; p->rgrc0.Bottom -= fy;
                        // Auto-Ausblenden der Taskleiste: einen Pixel frei lassen, damit sie aufklappen kann
                        var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
                        if (GetMonitorInfo(mon, ref mi) && mi.rcWork.Bottom == mi.rcMonitor.Bottom && mi.rcWork.Top == mi.rcMonitor.Top
                            && mi.rcWork.Left == mi.rcMonitor.Left && mi.rcWork.Right == mi.rcMonitor.Right)
                            p->rgrc0.Bottom -= 1;
                    }
                }
                return 0;

            case WM_NCHITTEST:
                return HitTestNc(hwnd, lParam);

            case WM_NCMOUSEMOVE:
                {
                    if (!_trackNcLeave)
                    {
                        var t = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE | TME_NONCLIENT, hwndTrack = hwnd };
                        TrackMouseEvent(ref t); _trackNcLeave = true;
                    }
                    _root?.MouseLeave();
                    _root?.Top.SetNcHover(wParam == HTMAXBUTTON ? TitleHit.Max : TitleHit.None);
                    break;
                }
            case WM_NCMOUSELEAVE:
                _trackNcLeave = false;
                _root?.Top.SetNcHover(TitleHit.None);
                return 0;
            case WM_NCLBUTTONDOWN:
                if (wParam == HTMAXBUTTON) { _root?.Top.SetNcHover(TitleHit.Max, true); return 0; }
                break;
            case WM_NCLBUTTONUP:
                if (wParam == HTMAXBUTTON)
                {
                    _root?.Top.SetNcHover(TitleHit.Max, false);
                    SendMessage(hwnd, WM_SYSCOMMAND, (nint)(IsZoomed(hwnd) ? SC_RESTORE : SC_MAXIMIZE), 0);
                    return 0;
                }
                break;

            case WM_GETMINMAXINFO:
                {
                    var mm = (MINMAXINFO*)lParam;
                    mm->ptMinTrackSize.X = (int)(Theme.MinWindowWidth * _scale);
                    mm->ptMinTrackSize.Y = (int)(Theme.MinWindowHeight * _scale);
                    return 0;
                }

            case WM_SIZE:
                {
                    _minimized = wParam == 1; // SIZE_MINIMIZED
                    if (!_minimized)
                    {
                        ResizeTarget();
                        if (wParam != 2 && _root != null) // nicht maximiert: Normalgroesse merken
                        {
                            GetClientRect(hwnd, out var rc);
                            _normalW = rc.Width / _scale; _normalH = rc.Height / _scale;
                        }
                        InvalidateRect(hwnd, 0, false);
                    }
                    return 0;
                }

            case WM_DPICHANGED:
                {
                    _dpi = (uint)((long)wParam & 0xFFFF);
                    _scale = _dpi / 96f;
                    var r = (RECT*)lParam;
                    SetWindowPos(hwnd, 0, r->Left, r->Top, r->Width, r->Height, SWP_NOZORDER | SWP_NOACTIVATE);
                    _g?.Dispose(); _g = null; _rt?.Dispose(); _rt = null;
                    ResizeTarget();
                    InvalidateRect(hwnd, 0, false);
                    return 0;
                }

            case WM_ERASEBKGND:
                return 1;

            case WM_PAINT:
                {
                    BeginPaint(hwnd, out var ps);
                    Render();
                    EndPaint(hwnd, in ps);
                    return 0;
                }

            case WM_TIMER:
                if ((nuint)wParam == TimerId) OnTimer();
                return 0;

            case WM_APP:
                while (_posted.TryDequeue(out var a)) { a(); }
                _root?.Invalidate();
                InvalidateRect(hwnd, 0, false);
                return 0;

            case WM_MOUSEMOVE:
                {
                    if (!_trackLeave)
                    {
                        var t = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE, hwndTrack = hwnd };
                        TrackMouseEvent(ref t); _trackLeave = true;
                    }
                    _root?.Top.SetNcHover(TitleHit.None);
                    _root?.MouseMove(LoWord(lParam) / _scale, HiWord(lParam) / _scale, Ctrl, Shift, Alt);
                    return 0;
                }
            case WM_MOUSELEAVE:
                _trackLeave = false;
                _root?.MouseLeave();
                return 0;
            case WM_LBUTTONDOWN:
            case WM_LBUTTONDBLCLK:
                _root?.MouseDown(LoWord(lParam) / _scale, HiWord(lParam) / _scale, 0, msg == WM_LBUTTONDBLCLK ? 2 : 1, Ctrl, Shift, Alt);
                return 0;
            case WM_LBUTTONUP:
                _root?.MouseUp(LoWord(lParam) / _scale, HiWord(lParam) / _scale, 0, Ctrl, Shift, Alt);
                return 0;
            case WM_RBUTTONDOWN:
            case WM_RBUTTONDBLCLK:
                _root?.MouseDown(LoWord(lParam) / _scale, HiWord(lParam) / _scale, 1, 1, Ctrl, Shift, Alt);
                return 0;
            case WM_RBUTTONUP:
                _root?.MouseUp(LoWord(lParam) / _scale, HiWord(lParam) / _scale, 1, Ctrl, Shift, Alt);
                return 0;
            case WM_MOUSEWHEEL:
                {
                    var pt = new POINT { X = LoWord(lParam), Y = HiWord(lParam) };
                    ScreenToClient(hwnd, ref pt);
                    float notches = HiWord(wParam) / 120f;
                    _root?.MouseWheel(pt.X / _scale, pt.Y / _scale, notches, Ctrl, Shift, Alt);
                    return 0;
                }
            case WM_CAPTURECHANGED:
                _root?.CaptureLost();
                return 0;

            case WM_SETCURSOR:
                if (LoWord(lParam) == (int)HTCLIENT && _root != null)
                {
                    int id = _root.CurrentCursor switch
                    {
                        CursorKind.Hand => IDC_HAND, CursorKind.IBeam => IDC_IBEAM, CursorKind.SizeNS => IDC_SIZENS,
                        CursorKind.SizeWE => IDC_SIZEWE, CursorKind.No => IDC_NO, _ => IDC_ARROW,
                    };
                    SetCursor(Cursor(id));
                    return 1;
                }
                break;

            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
                if (_root != null && _root.KeyDown((int)wParam, Ctrl, Shift, Alt)) { InvalidateRect(hwnd, 0, false); return 0; }
                break;
            case WM_CHAR:
                _root?.Char((char)wParam);
                return 0;

            case WM_SETTINGCHANGE:
                if (lParam != 0 && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                {
                    _root?.SystemThemeChanged();
                    InvalidateRect(hwnd, 0, false);
                }
                break;

            case WM_CLOSE:
                _root?.SaveNow();
                _model.Shutdown();
                AudioFactory.TryRestoreAll(); // Sicherheit: alle Programm-Umleitungen beim Beenden zuruecksetzen
                DestroyWindow(hwnd);
                return 0;
            case WM_DESTROY:
                KillTimer(hwnd, TimerId);
                _g?.Dispose(); _rt?.Dispose();
                PostQuitMessage(0);
                return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    nint HitTestNc(nint hwnd, nint lParam)
    {
        var def = DefWindowProc(hwnd, WM_NCHITTEST, 0, lParam);
        if (_root == null) return def;
        var pt = new POINT { X = LoWord(lParam), Y = HiWord(lParam) };
        GetWindowRect(hwnd, out var wr);
        bool maxed = IsZoomed(hwnd);
        if (!maxed)
        {
            int b = Math.Max(4, (int)(Theme.ResizeBorder * _scale));
            bool l = pt.X < wr.Left + b, r = pt.X >= wr.Right - b, t = pt.Y < wr.Top + b, bt = pt.Y >= wr.Bottom - b;
            if (t && l) return HTTOPLEFT;
            if (t && r) return HTTOPRIGHT;
            if (bt && l) return HTBOTTOMLEFT;
            if (bt && r) return HTBOTTOMRIGHT;
            if (l) return HTLEFT;
            if (r) return HTRIGHT;
            if (t) return HTTOP;
            if (bt) return HTBOTTOM;
        }
        var c = pt;
        ScreenToClient(hwnd, ref c);
        var kind = _root.CaptionHit(c.X / _scale, c.Y / _scale);
        return kind switch
        {
            TitleHit.Drag => HTCAPTION,
            TitleHit.Max => HTMAXBUTTON,
            TitleHit.None => HTCLIENT,
            _ => HTCLIENT,
        };
    }

    void OnTimer()
    {
        if (_root == null) return;
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(0.1, now - _lastTick);
        _lastTick = now;
        if (_exitAfterMs > 0 && now * 1000 > _exitAfterMs) { PostMessage(_hwnd, WM_CLOSE, 0, 0); _exitAfterMs = 0; return; }
        if (_minimized) return;
        while (_posted.TryDequeue(out var a)) a();
        _root.Tick(dt);
        if (_root.ConsumeDirty()) InvalidateRect(_hwnd, 0, false);
    }
}
