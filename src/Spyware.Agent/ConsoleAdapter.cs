using System.Runtime.InteropServices;
using System.Text;

namespace Spyware.Agent;

/// <summary>
/// Al ser WinExe no hay consola heredada: para los verbos CLI se engancha la
/// consola del proceso padre si existe.
/// </summary>
internal static class ConsoleAdapter
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nConsoleHandle);

    public static bool AttachToParentConsole()
    {
        if (!AttachConsole(AttachParentProcess)) return false;

        try { Redirect(StdOutputHandle, Console.Out, Console.SetOut); } catch { }
        try { Redirect(StdErrorHandle, Console.Error, Console.SetError); } catch { }

        return true;
    }

    public static void Detach()
    {
        try { FreeConsole(); } catch { }
    }

    private static void Redirect(int handle, TextWriter current, Action<TextWriter> setter)
    {
        IntPtr pointer = GetStdHandle(handle);
        if (pointer == IntPtr.Zero || pointer == new IntPtr(-1)) return;

        var stream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(pointer, true), FileAccess.Write);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        setter(writer);
    }
}
