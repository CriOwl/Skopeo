using System.Text.Json;
using System.Text.Json.Serialization;
using Spyware.Core.Logging;
using Spyware.Core.Paths;

namespace Spyware.Maintenance;

public sealed record HealthSnapshot
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    public required string AgentId { get; init; }
    public int ProcessId { get; init; }
    public required string AgentVersion { get; init; }
    public required string MachineName { get; init; }
    public required string UserName { get; init; }
    public int SessionId { get; init; }
    public required string Token { get; init; }
    public required string Persistence { get; init; }
    public required IReadOnlyDictionary<string, bool> Collectors { get; init; }

    public IReadOnlyDictionary<string, int> Storage { get; init; } = new Dictionary<string, int>();

    public long StorageBytes { get; init; }
    public int PendingEvents { get; init; }
    public string? LastError { get; init; }
}

public static class HealthCheck
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Write(AgentPaths paths, HealthSnapshot snapshot, ILog? log = null)
    {
        try
        {
            Directory.CreateDirectory(paths.RootDirectory);

            string temporary = paths.HealthFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, Options));
            File.Move(temporary, paths.HealthFile, overwrite: true);
        }
        catch (Exception ex)
        {
            log?.Warn("No se pudo escribir health.json: " + ex.Message);
        }
    }

    public static HealthSnapshot? Read(AgentPaths paths)
    {
        try
        {
            return File.Exists(paths.HealthFile)
                ? JsonSerializer.Deserialize<HealthSnapshot>(File.ReadAllText(paths.HealthFile), Options)
                : null;
        }
        catch { return null; }
    }
}
