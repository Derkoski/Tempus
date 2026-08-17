using static Tempus.Interop.NativeMethods;

namespace Tempus.Interop;

/// <summary>
/// Avisa quando outra janela vira a ativa, em qualquer processo. Existe por um motivo só: é
/// quando o explorer ergue o <c>Shell_TrayWnd</c> e enterra a barra atrás dele (D-014).
/// <para>
/// Não é polling. O sistema entrega o evento; em repouso este tipo não custa nada.
/// </para>
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    // Campo, e não variável local nem lambda passada direto: o delegate é a ponte que o sistema
    // chama de fora do runtime. Sem uma referência forte viva, o GC o coleta e o callback
    // aterrissa em memória liberada — falha rara, distante e ilegível no dump.
    private readonly WinEventProc _callback;

    private IntPtr _hook;

    /// <summary>
    /// Instala o hook. <b>Precisa</b> ser chamado na thread de UI: com
    /// <c>WINEVENT_OUTOFCONTEXT</c> o sistema entrega o callback pela fila de mensagens da thread
    /// que instalou, então <see cref="Changed"/> já chega na thread certa — sem marshaling e sem
    /// lock no caminho quente.
    /// </summary>
    public ForegroundWatcher()
    {
        _callback = OnWinEvent;

        _hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback,
            processId: 0, threadId: 0, // qualquer processo, qualquer thread
            WINEVENT_OUTOFCONTEXT);
    }

    /// <summary>A janela ativa mudou. Nada é dito sobre <i>qual</i> — a barra não precisa saber.</summary>
    public event EventHandler? Changed;

    /// <summary>False se o sistema recusou o hook. A barra continua com o heartbeat de 1s.</summary>
    public bool IsActive => _hook != IntPtr.Zero;

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd,
        int objectId, int childId, uint threadId, uint timestamp)
    {
        // objectId != OBJID_WINDOW são eventos de foco em controles filhos; irrelevantes aqui.
        if (objectId != 0) return;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;

        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }
}
