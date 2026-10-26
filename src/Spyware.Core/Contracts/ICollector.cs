namespace Spyware.Core.Contracts;

public interface ICollector
{
    string Name { get; }
    void Start();
    void Stop();
}
