using System.Globalization;
using System.Runtime.InteropServices;
using NxMix.App.Model;
using NxMix.App.Native;
using NxMix.App.Pages;
using NxMix.App.Ui;
using NxMix.Audio;
using NxMix.Engine;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;

namespace NxMix.App;

internal static class Program
{
    sealed class Options
    {
        public string? Shot, SelfTest, DataDir, Show;
        public string Page = "mixer";
        public int W = 1600, H = 900;
        public float Scale = 1f;
        public bool Demo, HostRunning, ForceDemoEngine;
        /// <summary>--restorer unsupported: Screenshot mit "Windows unterstuetzt es nicht".</summary>
        public string? Restorer;
        /// <summary>Screenshots: --theme light|dark, --touch, --expanded (Seitenleiste ausgeklappt).</summary>
        public string Theme = "dark";
        public bool Touch, Expanded;
        public double T = 4.2;
        public int ExitAfterMs;
    }

    static Options Parse(string[] a)
    {
        var o = new Options();
        string? Next(ref int i) => i + 1 < a.Length ? a[++i] : null;
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i].ToLowerInvariant())
            {
                case "--shot": o.Shot = Next(ref i); break;
                case "--page": o.Page = (Next(ref i) ?? "mixer").ToLowerInvariant(); break;
                case "--size":
                    {
                        var s = (Next(ref i) ?? "").ToLowerInvariant().Split('x');
                        if (s.Length == 2 && int.TryParse(s[0], out int w) && int.TryParse(s[1], out int h)) { o.W = w; o.H = h; }
                        break;
                    }
                case "--scale": float.TryParse(Next(ref i), NumberStyles.Float, CultureInfo.InvariantCulture, out o.Scale); break;
                case "--t": double.TryParse(Next(ref i), NumberStyles.Float, CultureInfo.InvariantCulture, out o.T); break;
                case "--empty": o.Demo = false; break;
                case "--demo": o.Demo = true; break;
                case "--host-running": o.HostRunning = true; break;
                case "--show": o.Show = Next(ref i); break;
                case "--selftest": o.SelfTest = Next(ref i); break;
                case "--data-dir": o.DataDir = Next(ref i); break;
                case "--exit-after": int.TryParse(Next(ref i), out o.ExitAfterMs); break;
                case "--engine": o.ForceDemoEngine = string.Equals(Next(ref i), "demo", StringComparison.OrdinalIgnoreCase); break;
                case "--restorer": o.Restorer = Next(ref i); break;
                case "--theme": o.Theme = (Next(ref i) ?? "dark").ToLowerInvariant(); break;
                case "--touch": o.Touch = true; break;
                case "--expanded": o.Expanded = true; break;
            }
        }
        return o;
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            var o = Parse(args);
            if (o.DataDir != null) ProjectStore.Directory = o.DataDir;
            if (o.Shot != null) return RunShot(o);
            if (o.SelfTest != null) return SelfTest.Run(o.SelfTest);
            return RunWindow(o);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            return 1;
        }
    }

    // ---- normaler Start ------------------------------------------------------------------------
    static int RunWindow(Options o)
    {
        // Sicherheit zuerst: Umleitungen, die ein frueherer Lauf (auch nach einem Absturz) hinterlassen hat, zuruecksetzen
        AudioFactory.TryRestoreAll();
        if (o.ForceDemoEngine) EngineFactory.ForceDemo = true;
        var dto = ProjectStore.TryLoadAuto();
        MixerSettings settings = AppModel.EmptySettings();
        List<DeviceBinding> bindings = new();
        if (dto != null)
        {
            try { (settings, bindings) = ProjectStore.FromDto(dto); }
            catch { settings = AppModel.EmptySettings(); bindings = new(); dto = null; }
        }
        var model = new AppModel(settings, bindings, EngineFactory.Create, AudioFactory.CreateCatalog, AudioFactory.CreateHost,
            AudioFactory.HostAvailable, AudioFactory.HostUnavailableReason);
        if (dto != null)
        {
            model.InitOptions(dto.BlockFrames, dto.Exclusive);
            model.InitAppLinks(ProjectStore.AppLinksFromDto(dto)); // fehlendes Feld in alten Dateien = keine Links
        }
        var win = new MainWindow(model, dto?.Window, o.ExitAfterMs, UiPrefs.Load());
        return win.Run();
    }

    // ---- Screenshot ----------------------------------------------------------------------------
    static int RunShot(Options o)
    {
        ProjectStore.Enabled = false;
        EngineFactory.ForceDemo = true;
        double clockT = o.T;
        EngineFactory.DemoClock = () => clockT;

        MixerSettings settings = AppModel.EmptySettings();
        List<DeviceBinding> bindings = new();
        if (o.Demo) (settings, bindings) = DemoProject.Create();
        var model = new AppModel(settings, bindings, EngineFactory.Create, () => new DemoCatalog(), () => new DemoHost(o.HostRunning), true, null)
        { Post = a => a() };
        model.SetDevicesNow(new DemoCatalog().List());
        // App-Links: erfundene Programme, Attrappen fuer Katalog und Umleitung; --demo zeigt Links in verschiedenen Zustaenden
        model.RunInline = true;
        model.AppCatalogFactory = () => new DemoAppCatalog();
        model.RestorerFactory = () => o.Restorer == "unsupported" ? new DemoRestorer(false, "Demo: Diese Windows-Version kennt keine Pro-App-Umleitung.") : new DemoRestorer();
        if (o.Demo)
        {
            model.InitAppLinks(DemoProject.AppLinks());
            model.LinkStatusSource = DemoProject.LinkStatuses;
        }
        if (o.HostRunning) model.AttachHostForShot(new DemoHost(true));

        var prefs = new UiPrefs
        {
            Theme = o.Theme == "light" ? ThemeMode.Light : ThemeMode.Dark,
            Density = o.Touch ? Density.Touch : Density.Mouse,
            SidebarExpanded = o.Expanded,
        };
        var root = new UiRoot(model, new NullShell(), prefs);
        float dipW = o.W / o.Scale, dipH = o.H / o.Scale;
        root.Resize(dipW, dipH);
        int page = o.Page switch { "matrix" => 1, "devices" => 2, "settings" => 3, _ => 0 };
        root.SwitchPage(page);
        // Zeit vorspulen, damit Meter und Spitzenhalten natuerlich aussehen
        for (int k = 0; k < 120; k++) { clockT = o.T - 2.0 + k / 60.0; root.Tick(1f / 60f); }
        clockT = o.T;
        root.Tick(1f / 60f);
        if (o.Show != null) ShowOverlay(root, o.Show);

        RenderRoot(root, o.W, o.H, o.Scale, o.Shot);
        return 0;
    }

    /// <summary>Zeichnet die Oberflaeche offscreen (D2D WIC-Bitmap) und schreibt optional eine PNG-Datei.</summary>
    public static void RenderRoot(UiRoot root, int pw, int ph, float scale, string? pngPath)
    {
        float dpi = 96f * scale;
        using var wic = new IWICImagingFactory();
        using var bmp = wic.CreateBitmap((uint)pw, (uint)ph, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
        var props = new RenderTargetProperties(RenderTargetType.Default,
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), dpi, dpi, RenderTargetUsage.None, FeatureLevel.Default);
        using (var rt = Renderer.D2D.CreateWicBitmapRenderTarget(bmp, props))
        using (var g = new Renderer(rt, scale, false))
        {
            rt.BeginDraw();
            root.Paint(g);
            rt.EndDraw();
        }
        if (pngPath == null) return;
        string full = Path.GetFullPath(pngPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream = wic.CreateStream(full, FileAccess.Write);
        using var enc = wic.CreateEncoder(ContainerFormatGuids.Png);
        enc.Initialize(stream);
        using var frame = enc.CreateNewFrame(out _);
        frame.Initialize(null);
        frame.SetSize((uint)pw, (uint)ph);
        var pf = Vortice.WIC.PixelFormat.Format32bppPBGRA;
        frame.SetPixelFormat(ref pf);
        frame.WriteSource(bmp);
        frame.Commit();
        enc.Commit();
    }

    /// <summary>Kanal-Blatt oeffnen (Mixer-Seite) - fuer Screenshots und Selbsttest.</summary>
    public static ChannelSheet? Sheet(UiRoot root, bool input, int index)
    {
        root.SwitchPage(0);
        return ((MixerPage)root.Pages[0]).OpenSheet(input, Math.Clamp(index, 0, Math.Max(0, (input ? root.Model.InputCount : root.Model.OutputCount) - 1)));
    }

    /// <summary>App-Link-Auswahl fuer einen Eingang oeffnen (ueber dessen Kanal-Blatt).</summary>
    public static void Picker(UiRoot root, int input, bool manual = false)
    {
        var s = Sheet(root, true, input);
        for (int i = 0; i < 20; i++) root.Tick(1f / 60f);
        s?.OpenPicker(manual);
    }

    static void ShowOverlay(UiRoot root, string what)
    {
        switch (what)
        {
            case "project-menu": root.ShowProjectMenu(root.Top.MenuRect); break;
            case "new-dialog": root.NewProjectWithConfirm(); break;
            case "start-dialog": ((DevicesPage)root.Pages[2]).ShowStartDialog(); break;
            case "sheet-out": Sheet(root, false, 2); break;                 // Hochtoener mit Gain-Sperre
            case "sheet-in": Sheet(root, true, 6); break;                   // Spotify (Stereo-Link)
            case "sheet-scroll":
                {
                    var s = Sheet(root, false, 5);
                    for (int i = 0; i < 20; i++) root.Tick(1f / 60f);
                    if (s != null) root.MouseWheel(root.W - 100, root.H - 200, -12f, false, false, false);
                    break;
                }
            case "add-menu":
                {
                    var b = ((MixerPage)root.Pages[0]).Inputs.AddButton;
                    root.MouseDown(b.Bounds.CX, b.Bounds.CY, 0, 1, false, false, false);
                    root.MouseUp(b.Bounds.CX, b.Bounds.CY, 0, false, false, false);
                    break;
                }
            case "rename": Sheet(root, true, 0)?.BeginRename(); break;
            case "applink-picker": Picker(root, 10); break;          // freier Eingang: Liste + Mono/Stereo
            case "applink-picker-linked": Picker(root, 8); break;    // Chrome gebunden: "Verknuepfung loesen"
            case "applink-picker-pair": Picker(root, 7); break;      // Stereo-Link (zweite Haelfte)
            case "applink-conflict": Picker(root, 2); break;         // haengt an einer Geraete-Zuordnung
            case "applink-manual": Picker(root, 10, true); break;
            case "applink-problem": // Start mit App-Links, aber ohne Parkplatz: Erklaerung + Alternativen
                root.Model.SetParking(null);
                root.SwitchPage(2);
                ((DevicesPage)root.Pages[2]).ShowLinkProblemDialog(root.Model.AppLinkProblem() ?? "");
                break;
            case "applink-filter":
                Picker(root, 10);
                foreach (char c in "ch") root.Char(c);
                break;
            case "tooltip":
                {
                    var strips = ((MixerPage)root.Pages[0]).Outputs.Strips;
                    if (strips.Count > 2)
                    {
                        var b = strips[2].Bounds;
                        root.MouseMove(b.CX - 4, b.Y + b.H * 0.55f, false, false, false);
                        for (int i = 0; i < 60; i++) root.Tick(1f / 60f);
                    }
                    break;
                }
        }
        root.Tick(1f / 60f);
    }

    // ---- Fehlerprotokoll (neben der Exe) -------------------------------------------------------
    public static void LogCrash(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "nx-mix-error.log"), $"[{DateTime.Now:s}] {ex}\n\n");
        }
        catch { /* nichts zu tun */ }
    }
}
