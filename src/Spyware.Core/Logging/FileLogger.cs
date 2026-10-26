using System.Text;

namespace Spyware.Core.Logging;

/// <summary>Log de texto plano (sin cifrar) con rotación simple por tamaño.</summary>
public sealed class FileLogger : ILog, IDisposable
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly int _maxFiles;
    private readonly long _maxBytes;
    private StreamWriter? _writer;

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;
    public bool MirrorToConsole { get; set; }

    public FileLogger(string directory, string fileName = "agent.log", int maxFiles = 3, int maxMb = 5)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, fileName);
        _maxFiles = Math.Max(1, maxFiles);
        _maxBytes = Math.Max(1, maxMb) * 1024L * 1024L;
    }

    public void Debug(string message) { if (MinimumLevel <= LogLevel.Debug) Write("DEBUG", message); }
    public void Info(string message) { if (MinimumLevel <= LogLevel.Info) Write("INFO ", message); }
    public void Warn(string message) { if (MinimumLevel <= LogLevel.Warn) Write("WARN ", message); }

    public void Error(string message, Exception? exception = null)
    {
        if (MinimumLevel <= LogLevel.Error) Write("ERROR", message, exception);
    }

    private void Write(string level, string message, Exception? exception = null)
    {
        lock (_gate)
        {
            try
            {
                RotateIfNeeded();

                _writer ??= new StreamWriter(
                    new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(false))
                {
                    AutoFlush = true
                };

                string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z|{level}|tid{Thread.CurrentThread.ManagedThreadId}|{message}";
                _writer.WriteLine(line);

                if (exception is not null)
                    _writer.WriteLine($"        {exception.GetType().Name}: {exception.Message}");

                if (MirrorToConsole)
                {
                    Console.WriteLine(line);
                    if (exception is not null) Console.WriteLine("         " + exception.Message);
                }
            }
            catch
            {
                // El logger nunca debe lanzar: si el disco falla, el agente sigue vivo.
            }
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_filePath);
            if (!info.Exists || info.Length < _maxBytes) return;

            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;

            for (int i = _maxFiles; i >= 1; i--)
            {
                string source = i == 1 ? _filePath : $"{_filePath}.{i - 1}";
                string target = $"{_filePath}.{i}";
                if (i == _maxFiles && File.Exists(target)) File.Delete(target);
                if (File.Exists(source)) File.Move(source, target, overwrite: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}
