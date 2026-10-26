namespace Spyware.Core.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
    None = 4
}

public interface ILog
{
    LogLevel MinimumLevel { get; set; }
    bool MirrorToConsole { get; set; }

    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}
