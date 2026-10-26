using Spyware.Core.Configuration;
using Spyware.Core.Logging;
using Spyware.Core.Paths;

namespace Spyware.Storage;

/// <summary>Retención por edad y por tamaño; evita acumular datos indefinidamente.</summary>
public sealed class RetentionPolicy
{
    private readonly AgentPaths _paths;
    private readonly LocalEventRepository _repository;
    private readonly SpoolManager _spool;
    private readonly StorageSection _config;
    private readonly ILog _log;

    public RetentionPolicy(AgentPaths paths, LocalEventRepository repository, SpoolManager spool,
                           StorageSection config, ILog log)
    {
        _paths = paths;
        _repository = repository;
        _spool = spool;
        _config = config;
        _log = log;
    }

    public void Apply()
    {
        try
        {
            PruneByAge();
            PruneBySize();
            _spool.PruneTempDirectory(TimeSpan.FromMinutes(30));
        }
        catch (Exception ex)
        {
            _log.Error("La política de retención falló.", ex);
        }
    }

    private void PruneByAge()
    {
        if (_config.MaxDays <= 0) return;

        DateTime cutoff = DateTime.UtcNow.AddDays(-_config.MaxDays);
        IReadOnlyList<string> orphans = _repository.DeleteOlderThan(cutoff);

        int deletedFiles = 0;
        foreach (string path in orphans)
            if (_spool.TryDeleteFile(path)) deletedFiles++;

        PruneOrphanFiles(cutoff);

        if (orphans.Count > 0 || deletedFiles > 0)
            _log.Info($"Retención por edad (>{_config.MaxDays} días): filas depuradas, archivos borrados {deletedFiles}.");
    }

    /// <summary>
    /// Barrido de archivos sin fila: los volcados de notas y los lotes exportados no tienen
    /// columna de ruta en la base, así que la purga por filas nunca los alcanza y crecerían
    /// para siempre. Se borran los anteriores al corte que ninguna fila ya referencia.
    /// </summary>
    private void PruneOrphanFiles(DateTime cutoffUtc)
    {
        var referenced = new HashSet<string>(
            _repository.GetReferencedFilePaths(), StringComparer.OrdinalIgnoreCase);

        int removed = 0;

        // EnumerateOldestManagedFiles viene ordenado de antiguo a nuevo: al llegar al corte se acaba.
        foreach (SpoolFile file in _spool.EnumerateOldestManagedFiles())
        {
            if (file.LastWriteUtc >= cutoffUtc) break;
            if (referenced.Contains(file.Path)) continue;
            if (!_spool.TryDeleteFile(file.Path)) continue;

            removed++;
        }

        if (removed > 0)
            _log.Info($"Retención por edad (>{_config.MaxDays} días): {removed} archivos sin fila borrados.");
    }

    private void PruneBySize()
    {
        if (_config.MaxSizeMb <= 0) return;

        long limit = (long)_config.MaxSizeMb * 1024 * 1024;
        long current = _spool.GetManagedStorageBytes();
        if (current <= limit) return;

        var victims = new List<string>();
        foreach (SpoolFile file in _spool.EnumerateOldestManagedFiles())
        {
            if (current <= limit) break;
            if (file.Path.Equals(_paths.StorageDb, StringComparison.OrdinalIgnoreCase)) continue;
            if (!_spool.TryDeleteFile(file.Path)) continue;

            current -= file.Length;
            victims.Add(file.Path);
        }

        int rows = _repository.DeleteByPaths(victims);
        _log.Info($"Retención por tamaño ({_config.MaxSizeMb} MB): archivos {victims.Count}, filas {rows}.");
    }
}
