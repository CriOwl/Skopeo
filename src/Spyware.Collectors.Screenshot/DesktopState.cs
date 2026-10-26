using System.Runtime.InteropServices;
using Spyware.Core.Logging;

namespace Spyware.Collectors.Screenshot;

/// <summary>
/// Detecta si el escritorio de entrada es el "seguro" (UAC / lock screen).
/// En ese caso CopyFromScreen devuelve negro o lanza excepción (limitación documentada).
/// </summary>
public static class DesktopState
{
    private const uint DesktopReadObjects = 0x0002;
    private const uint DesktopWriteObjects = 0x0080;
    private const uint DesktopSwitchDesktop = 0x0100;
    private const uint ReadControl = 0x00020000;
    private const int UoiName = 2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetUserObjectInformationW(IntPtr hObj, int nIndex, IntPtr pvInfo, int nLength, out int lpLengthRequired);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public static bool IsSecureOrLocked(ILog? log = null)
    {
        IntPtr desktop = IntPtr.Zero;
        try
        {
            desktop = OpenInputDesktop(0, false, DesktopReadObjects | DesktopWriteObjects | DesktopSwitchDesktop | ReadControl);
            if (desktop == IntPtr.Zero)
            {
                log?.Debug("OpenInputDesktop denegado: escritorio bloqueado o seguro.");
                return true;
            }

            string name = ReadName(desktop);
            if (string.IsNullOrEmpty(name))
                return GetForegroundWindow() == IntPtr.Zero;

            return !name.Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            log?.Debug("Detección de escritorio falló: " + ex.Message);
            return GetForegroundWindow() == IntPtr.Zero;
        }
        finally
        {
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
        }
    }

    private static string ReadName(IntPtr desktop)
    {
        IntPtr buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (!GetUserObjectInformationW(desktop, UoiName, buffer, 512, out _))
                return string.Empty;

            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
