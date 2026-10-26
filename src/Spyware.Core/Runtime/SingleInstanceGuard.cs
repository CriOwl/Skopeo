using System.Security.Cryptography;
using System.Text;
using Spyware.Core.Logging;

namespace Spyware.Core.Runtime;

/// <summary>
/// Instancia única. Intenta Global\ y, si el usuario no tiene SeCreateGlobalPrivilege,
/// cae a Local\ (por sesión). Mantiene RF-01: no exige privilegios administrativos.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private Mutex? _mutex;

    public bool IsFirstInstance { get; private set; }
    public string MutexName { get; private set; } = string.Empty;

    public static string BuildName(string seed)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes((seed ?? "agent").Trim().ToLowerInvariant()));
        return "SpywareLab.SpywareAgent." + Convert.ToHexString(hash.AsSpan(0, 8));
    }

    public bool TryAcquire(string seed, ILog log)
    {
        string baseName = BuildName(seed);

        foreach (string scope in new[] { "Global\\", "Local\\" })
        {
            string fullName = scope + baseName;
            try
            {
                bool createdNew;
                var candidate = new Mutex(initiallyOwned: true, fullName, out createdNew);

                if (createdNew)
                {
                    _mutex = candidate;
                    MutexName = fullName;
                    IsFirstInstance = true;
                    return true;
                }

                candidate.Dispose();
                MutexName = fullName;
                IsFirstInstance = false;
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                log.Warn($"{scope} no es accesible para este usuario; se prueba el siguiente alcance.");
            }
        }

        log.Warn("No se pudo crear el mutex: no hay garantía de instancia única.");
        IsFirstInstance = true;
        return true;
    }

    public static bool IsAgentRunning(string seed)
    {
        foreach (string scope in new[] { "Global\\", "Local\\" })
        {
            try
            {
                if (Mutex.TryOpenExisting(scope + BuildName(seed), out Mutex? existing))
                {
                    existing.Dispose();
                    return true;
                }
            }
            catch (UnauthorizedAccessException) { return true; }
        }

        return false;
    }

    public void Dispose()
    {
        try
        {
            if (IsFirstInstance) _mutex?.ReleaseMutex();
        }
        catch (ApplicationException) { }

        _mutex?.Dispose();
        _mutex = null;
    }
}
