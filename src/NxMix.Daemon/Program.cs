using NxMix.Daemon.ControlApi;
using NxMix.Ipc;

namespace NxMix.Daemon;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool headless = false;
        string? customKey = null;
        string? customDataDir = null;
        int? customPort = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase))
                headless = true;
            else if (string.Equals(a, "--key", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                customKey = args[++i];
            else if (string.Equals(a, "--data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                customDataDir = args[++i];
            else if (string.Equals(a, "--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[++i], out int p))
                customPort = p;
        }

        using var singleInstance = new Mutex(initiallyOwned: true, IpcNames.DaemonMutexName(customKey), out bool createdNew);
        if (!createdNew)
        {
            // Hintergrund-Engine läuft bereits in dieser Benutzersitzung -> sofort sauber beenden.
            return 0;
        }

        using var quitSignal = new ManualResetEventSlim(false);
        TrayIconHost? tray = null;

        SystemConfig? overrideCfg = null;
        if (customPort.HasValue)
        {
            var tmpPaths = new Persistence.DaemonPaths(customDataDir);
            var loaded = new Persistence.SystemConfigStore(tmpPaths).Load();
            overrideCfg = loaded with { CompanionApiEnabled = true, CompanionApiPort = customPort.Value };
        }

        using var controller = new DaemonEngineController(new DaemonEngineOptions
        {
            CustomDataDir = customDataDir,
            CustomIpcKey = customKey,
            SkipInitialDeviceScan = headless && customKey != null,
            InitialSystemConfigOverride = overrideCfg,
            ShutdownRequestedCallback = () =>
            {
                quitSignal.Set();
                tray?.RequestExit();
            },
        });

        using var pipeServer = new NamedPipeControlServer(controller, customKey);
        pipeServer.Start();

        using var companion = new CompanionWebSocketGateway(controller);

        if (headless)
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                quitSignal.Set();
            };
            quitSignal.Wait();
        }
        else
        {
            using var trayHost = new TrayIconHost(controller);
            tray = trayHost;
            trayHost.RunMessageLoop();
        }

        return 0;
    }
}
