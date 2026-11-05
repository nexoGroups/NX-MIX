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
        var win = new MainWindow(model, dto?.Window, o.ExitAfterMs);
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

        var root = new UiRoot(model, new NullShell());
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

    static void ShowOverlay(UiRoot root, string what)
    {
        switch (what)
        {
            case "project-menu": root.ShowProjectMenu(root.Title.MenuRect); break;
            case "new-dialog": root.NewProjectWithConfirm(); break;
            case "start-dialog": ((DevicesPage)root.Pages[2]).ShowStartDialog(); break;
            case "lock-menu": ((MixerPage)root.Pages[0]).DebugOpen(false, Math.Min(2, root.Model.OutputCount - 1), "lock"); break;
            case "channel-menu": ((MixerPage)root.Pages[0]).DebugOpen(false, Math.Min(1, root.Model.OutputCount - 1), "channel"); break;
            case "rename": ((MixerPage)root.Pages[0]).DebugOpen(true, 0, "rename"); break;
            case "applink-picker": ((MixerPage)root.Pages[0]).DebugOpen(true, 10, "applink"); break;        // freier Eingang: Liste + Mono/Stereo
            case "applink-picker-linked": ((MixerPage)root.Pages[0]).DebugOpen(true, 8, "applink"); break;  // Chrome gebunden: Haken + "Verknuepfung loesen"
            case "applink-picker-pair": ((MixerPage)root.Pages[0]).DebugOpen(true, 7, "applink"); break;    // Stereo-Link (zweite Haelfte angeklickt)
            case "applink-conflict": ((MixerPage)root.Pages[0]).DebugOpen(true, 2, "applink"); break;       // haengt an einer Geraete-Zuordnung
            case "applink-manual": ((MixerPage)root.Pages[0]).DebugOpen(true, 10, "applink-manual"); break;
            case "applink-problem": // Start mit App-Links, aber ohne Parkplatz: Erklaerung + Alternativen
                root.Model.SetParking(null);
                root.SwitchPage(2);
                ((DevicesPage)root.Pages[2]).ShowLinkProblemDialog(root.Model.AppLinkProblem() ?? "");
                break;
            case "applink-filter":
                ((MixerPage)root.Pages[0]).DebugOpen(true, 10, "applink");
                foreach (char c in "ch") root.Char(c);
                break;
            case "applink-tooltip":
                {
                    var strips = ((MixerPage)root.Pages[0]).Inputs.Strips;
                    if (strips.Count > 9)
                    {
                        var lr = strips[9].LinkRect;
                        root.MouseMove(lr.CX, lr.CY, false, false, false);
                        for (int i = 0; i < 60; i++) root.Tick(1f / 60f);
                    }
                    break;
                }
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
