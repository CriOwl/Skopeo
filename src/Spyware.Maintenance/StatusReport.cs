using Spyware.Core.Configuration;
using Spyware.Core.Logging;
using Spyware.Core.Paths;
using Spyware.Core.Runtime;
using Spyware.Persistence;
using Spyware.Storage;

namespace Spyware.Maintenance;

public static class StatusReport
{
    public static int Print(AgentPaths paths, AgentConfig config, ILog log)
    {
        Console.WriteLine("SpywareAgent — estado");
        Console.WriteLine("---------------------");
        Console.WriteLine($"Agente           : {config.AgentName} [{config.AgentId}]");
        Console.WriteLine($"Directorio       : {paths.RootDirectory}   (datos SIN CIFRAR)");
        Console.WriteLine($"PID consulta     : {ProcessContext.ProcessId}");
        Console.WriteLine($"Usuario/sesión   : {ProcessContext.DomainName}\\{ProcessContext.UserName} (sesión {ProcessContext.SessionId})");
        Console.WriteLine($"Token            : {ProcessContext.DescribeToken()}");
        Console.WriteLine($"Agente corriendo : {(SingleInstanceGuard.IsAgentRunning(config.MutexSeed) ? "sí" : "no")}");

        var registry = new RegistryRunInstaller();
        string executable = ProcessContext.ExecutablePath;
        string? runValue = registry.GetValue(config.Persistence.RegistryValueName);
        string? installedExecutable = runValue?.Trim().Trim('"');
        bool installedExists = !string.IsNullOrEmpty(installedExecutable) && File.Exists(installedExecutable);
        bool sameExecutable = registry.IsInstalled(config.Persistence.RegistryValueName, executable);

        Console.WriteLine($"Run key          : {(runValue is null ? "no instalada" : "instalada")}");
        Console.WriteLine($"  valor          : {runValue ?? "-"}");
        Console.WriteLine($"  exe instalado  : {(runValue is null ? "-" : installedExists ? "existe" : "falta en disco")}");
        Console.WriteLine($"  coincide exe   : {(sameExecutable ? "sí" : "no")}" +
            $"{(sameExecutable || !installedExists ? string.Empty : " (el Run key apunta a otra copia del binario)")}");
        Console.WriteLine($"Inicio (Startup) : {(new StartupFolderInstaller().IsInstalled(config.Persistence.RegistryValueName) ? "sí" : "no")}");

        HealthSnapshot? health = HealthCheck.Read(paths);
        if (health is not null)
        {
            Console.WriteLine($"Último latido    : {health.TimestampUtc:O}   PID {health.ProcessId}");
            Console.WriteLine($"  token          : {health.Token}");

            foreach ((string collector, bool running) in health.Collectors)
                Console.WriteLine($"  collector {collector,-10}: {(running ? "activo" : "detenido")}");
        }
        else
        {
            Console.WriteLine("Último latido    : sin health.json");
        }

        using var repository = new LocalEventRepository(paths.StorageDb, log);
        repository.Initialize();

        Console.WriteLine("Contenido de storage.db:");
        foreach ((string key, int value) in repository.GetStats())
            Console.WriteLine($"  {key,-26}: {value}");

        Console.WriteLine($"  tamaño db (bytes)       : {repository.GetDatabaseBytes()}");
        Console.WriteLine($"  tamaño total (bytes)    : {new SpoolManager(paths, log).GetManagedStorageBytes()}");

        return 0;
    }
}
