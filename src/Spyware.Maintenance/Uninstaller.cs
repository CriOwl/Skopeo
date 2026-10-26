using System.Diagnostics;
using Spyware.Core.Configuration;
using Spyware.Core.Logging;
using Spyware.Core.Paths;
using Spyware.Persistence;

namespace Spyware.Maintenance;

public sealed record UninstallOptions(bool RemoveData, bool RemoveStartupFolder, bool StopProcess);

public static class Uninstaller
{
    public static int Run(AgentPaths paths, AgentConfig config, ILog log, UninstallOptions options)
    {
        string valueName = config.Persistence.RegistryValueName;

        if (options.StopProcess)
            StopRunningAgents(log);

        var registry = new RegistryRunInstaller();
        registry.Remove(valueName, log);
        Console.WriteLine("[ok] Run key eliminada (si existía): " + valueName);

        var startup = new StartupFolderInstaller();
        if (options.RemoveStartupFolder || config.Persistence.StartupFolder)
        {
            startup.Remove(valueName, log);
            Console.WriteLine("[ok] Acceso directo de Inicio eliminado (si existía).");
        }

        Try(() =>
        {
            if (Directory.Exists(paths.TempDirectory))
                Directory.Delete(paths.TempDirectory, recursive: true);
        }, log, "temp");

        if (options.RemoveData)
        {
            Try(() =>
            {
                if (Directory.Exists(paths.StorageDirectory))
                    Directory.Delete(paths.StorageDirectory, recursive: true);
            }, log, "storage");

            Try(() =>
            {
                if (File.Exists(paths.ConfigFile)) File.Delete(paths.ConfigFile);
                if (File.Exists(paths.HealthFile)) File.Delete(paths.HealthFile);
            }, log, "config/health");

            Try(() =>
            {
                if (Directory.Exists(paths.LogDirectory))
                    Directory.Delete(paths.LogDirectory, recursive: true);
            }, log, "logs");

            Console.WriteLine("[ok] Datos locales eliminados (estaban sin cifrar).");
        }
        else
        {
            Console.WriteLine("[i] Se conservan los datos locales: " + paths.StorageDirectory);
        }

        return 0;
    }

    /// <summary>Mata otros procesos del mismo ejecutable (deja el actual, que es el desinstalador).</summary>
    private static void StopRunningAgents(ILog log)
    {
        // El proceso puede llamarse "SpywareAgent" (apphost) o "dotnet" (si se
        // arrancó con "dotnet SpywareAgent.dll"): se comprueban ambos nombres y
        // solo se termina lo que pertenece al agente.
        var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SpywareAgent",
            Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty)
        };
        candidateNames.Remove(string.Empty);

        foreach (string processName in candidateNames)
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (process.Id == Environment.ProcessId) continue;

                    string? path = null;
                    try { path = process.MainModule?.FileName; }
                    catch { /* proceso de otro usuario o protegido */ }

                    bool belongsToAgent =
                        path is not null &&
                        path.Contains("SpywareLab", StringComparison.OrdinalIgnoreCase);
                    if (!belongsToAgent) continue;

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    log.Info($"Proceso {process.Id} terminado durante la desinstalación.");
                }
                catch (Exception ex)
                {
                    log.Warn($"No se pudo terminar el proceso {process.Id}: {ex.Message}");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    private static void Try(Action action, ILog log, string label)
    {
        try { action(); }
        catch (Exception ex) { log.Warn($"No se pudo limpiar {label}: {ex.Message}"); }
    }
}
