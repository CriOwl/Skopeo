using System.Text.Json;
using System.Text.Json.Serialization;
using Spyware.Core.Logging;
using Spyware.Core.Paths;

namespace Spyware.Core.Configuration;

public static class ConfigLoader
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AgentConfig LoadOrCreate(AgentPaths paths, ILog log)
    {
        paths.EnsureDirectories();

        if (!File.Exists(paths.ConfigFile))
        {
            AgentConfig fresh = AgentConfig.CreateDefault();
            Save(paths, fresh, log);
            log.Info($"Configuración creada en {paths.ConfigFile}");
            return fresh;
        }

        try
        {
            AgentConfig? loaded = JsonSerializer.Deserialize<AgentConfig>(
                File.ReadAllText(paths.ConfigFile), Json);

            if (loaded is null)
                throw new InvalidDataException("config.json vacío o inválido.");

            bool dirty = false;
            if (string.IsNullOrWhiteSpace(loaded.AgentId))
            {
                loaded.AgentId = Guid.NewGuid().ToString("N");
                dirty = true;
            }

            if (dirty) Save(paths, loaded, log);
            return loaded;
        }
        catch (Exception ex)
        {
            log.Error("config.json corrupto; se restauran los valores por defecto.", ex);
            TryBackup(paths, log);
            AgentConfig fallback = AgentConfig.CreateDefault();
            Save(paths, fallback, log);
            return fallback;
        }
    }

    public static void Save(AgentPaths paths, AgentConfig config, ILog log)
    {
        try
        {
            paths.EnsureDirectories();
            File.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(config, Json));
        }
        catch (Exception ex)
        {
            log.Warn("No se pudo escribir config.json: " + ex.Message);
        }
    }

    public static Logging.LogLevel ParseLevel(string? level) =>
        (level ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "debug" or "trace" => Logging.LogLevel.Debug,
            "warning" or "warn" => Logging.LogLevel.Warn,
            "error" => Logging.LogLevel.Error,
            "none" => Logging.LogLevel.None,
            _ => Logging.LogLevel.Info
        };

    private static void TryBackup(AgentPaths paths, ILog log)
    {
        try
        {
            string bad = paths.ConfigFile + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Copy(paths.ConfigFile, bad, overwrite: true);
            log.Info("Copia del archivo corrupto en " + bad);
        }
        catch (Exception ex)
        {
            log.Warn("No se pudo respaldar config.json: " + ex.Message);
        }
    }
}
