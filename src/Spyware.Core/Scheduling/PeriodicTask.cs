using Spyware.Core.Logging;

namespace Spyware.Core.Scheduling;

/// <summary>
/// Tarea periódica con jitter y guarda anti-solapamiento (no encola ciclos).
/// </summary>
public sealed class PeriodicTask : IDisposable
{
    private readonly string _name;
    private readonly Action _action;
    private readonly TimeSpan _interval;
    private readonly int _jitterSeconds;
    private readonly ILog _log;

    // Calificado a propósito: con UseWindowsForms, "Timer" sería ambiguo
    // entre System.Threading.Timer y System.Windows.Forms.Timer.
    private readonly System.Threading.Timer _timer;
    private readonly Random _random = new();
    private int _executing;
    private volatile bool _stopped;

    public PeriodicTask(string name, Action action, TimeSpan interval, int jitterSeconds, ILog log)
    {
        _name = name;
        _action = action;
        _interval = interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(60) : interval;
        _jitterSeconds = Math.Max(0, jitterSeconds);
        _log = log;
        _timer = new System.Threading.Timer(OnElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start(bool runImmediately = true)
    {
        _stopped = false;
        _timer.Change(runImmediately ? TimeSpan.Zero : NextDelay(), Timeout.InfiniteTimeSpan);
        _log.Info($"[{_name}] programado cada ~{_interval.TotalSeconds:0}s (jitter ±{_jitterSeconds}s).");
    }

    private void OnElapsed(object? state)
    {
        if (_stopped) return;

        if (Interlocked.CompareExchange(ref _executing, 1, 0) == 1)
        {
            _log.Warn($"[{_name}] ciclo anterior sigue activo; se omite.");
            _timer.Change(NextDelay(), Timeout.InfiniteTimeSpan);
            return;
        }

        Task.Run(RunCycle);
    }

    private void RunCycle()
    {
        try
        {
            _action();
        }
        catch (Exception ex)
        {
            _log.Error($"[{_name}] fallo del ciclo.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _executing, 0);
            if (!_stopped) _timer.Change(NextDelay(), Timeout.InfiniteTimeSpan);
        }
    }

    private TimeSpan NextDelay() => Jitter.Apply(_interval, _jitterSeconds, _random);

    public void Stop()
    {
        _stopped = true;
        try { _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }
}
