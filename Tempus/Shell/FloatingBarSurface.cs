using Tempus.Domain;

namespace Tempus.Shell;

/// <summary>
/// Implementação primária de <see cref="IShellSurface"/>: a barra flutuante sobre a taskbar, mais
/// os painéis S2/S3 ancorados nela. Fina de propósito — existe para que o resto do app dependa da
/// interface e não das janelas, deixando o fallback de tray (D-002) a uma troca de construtor.
/// </summary>
internal sealed class FloatingBarSurface : IShellSurface
{
    private readonly FloatingBarWindow _window;

    private TasksPanel? _tasks;
    private AgendaPanel? _agenda;
    private PautaPanel? _pauta;

    public FloatingBarSurface(BarOptions options)
    {
        _window = new FloatingBarWindow(options);

        _window.Acknowledged += (_, e) => Acknowledged?.Invoke(this, e);
        _window.BreakDismissToggled += (_, e) => BreakDismissToggled?.Invoke(this, e);
        _window.BreakTaken += (_, e) => BreakTaken?.Invoke(this, e);
        _window.BreakPostponed += (_, e) => BreakPostponed?.Invoke(this, e);
        _window.BreakStartedNow += (_, e) => BreakStartedNow?.Invoke(this, e);
        _window.ActiveEventChosen += (_, id) => ActiveEventChosen?.Invoke(this, id);
        _window.MeetingLeftToggled += (_, occurrence) => MeetingLeftToggled?.Invoke(this, occurrence);
        _window.ReauthRequested += (_, e) => ReauthRequested?.Invoke(this, e);
        _window.SyncRetryRequested += (_, e) => SyncRetryRequested?.Invoke(this, e);
        _window.PautaRequested += (_, id) => PautaRequested?.Invoke(this, id);
        _window.TasksRequested += (_, e) => TasksRequested?.Invoke(this, e);
        _window.AgendaRequested += (_, e) => AgendaRequested?.Invoke(this, e);
        _window.MailRequested += (_, e) => MailRequested?.Invoke(this, e);
        _window.SyncRequested += (_, e) => SyncRequested?.Invoke(this, e);
        _window.SettingsRequested += (_, e) => SettingsRequested?.Invoke(this, e);
        _window.ExitRequested += (_, e) => ExitRequested?.Invoke(this, e);
        _window.MeetingActivated += (_, url) => MeetingActivated?.Invoke(this, url);
    }

    public event EventHandler? Acknowledged;
    public event EventHandler? BreakDismissToggled;
    public event EventHandler? BreakTaken;
    public event EventHandler? BreakPostponed;
    public event EventHandler? BreakStartedNow;
    public event EventHandler<string>? ActiveEventChosen;

    /// <summary>"Já saí desta reunião", ou o desfazer disso (D-041).</summary>
    public event EventHandler<string>? MeetingLeftToggled;
    public event EventHandler? ReauthRequested;

    /// <summary>Barra cinza por sync caído: tentar de novo, sem passar pelo navegador.</summary>
    public event EventHandler? SyncRetryRequested;

    /// <summary>Abrir a pauta de uma reunião (D-052). Carrega o id do evento.</summary>
    public event EventHandler<string>? PautaRequested;

    /// <summary>Novo assunto de pauta. Carrega o id do evento e o texto.</summary>
    public event EventHandler<(string EventId, string Text)>? PautaItemCreated;

    /// <summary>Riscar ou desriscar um assunto. Carrega o id do assunto.</summary>
    public event EventHandler<string>? PautaItemToggled;

    /// <summary>Excluir um assunto. Carrega o id.</summary>
    public event EventHandler<string>? PautaItemDeleted;
    public event EventHandler? TasksRequested;
    public event EventHandler? AgendaRequested;
    public event EventHandler? MailRequested;
    public event EventHandler? SyncRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<string>? TaskToggled;

    /// <summary>Novo vencimento de uma tarefa; data nula apaga (D-042).</summary>
    public event EventHandler<(string Id, DateOnly? Due)>? TaskRescheduled;

    /// <summary>Novo título de uma tarefa (D-045).</summary>
    public event EventHandler<(string Id, string Title)>? TaskRenamed;
    public event EventHandler<string>? TaskCreated;
    public event EventHandler<string>? TaskDeleted;

    /// <summary>"Tentar de novo" numa escrita que falhou. Carrega o id da <i>intenção</i>.</summary>
    public event EventHandler<string>? TaskWriteRetried;

    /// <summary>"Deixa pra lá": abandona a intenção sem tocar no Google.</summary>
    public event EventHandler<string>? TaskWriteDiscarded;

    /// <summary>Desmarcar uma concluída, devolvendo-a à lista de abertas.</summary>
    public event EventHandler<string>? TaskReopened;

    public event EventHandler<string>? MeetingActivated;

    public void Show() => _window.Show();

    public void Render(ShellState state) => _window.Render(state);

    public void ToggleTasks(IReadOnlyList<TaskRow> tasks)
    {
        if (_tasks is not null)
        {
            CloseTasks();
            return;
        }

        // Sem retângulo, a barra está escondida (tela cheia, taskbar recolhida, explorer caindo).
        // Abrir um painel ancorado em nada seria pior que não abrir.
        if (_window.CurrentRect is not { } anchor) return;

        CloseAgenda(); // um painel por vez
        ClosePauta();

        var panel = new TasksPanel(_window.CurrentPalette, anchor);
        panel.TaskToggled += (_, id) => TaskToggled?.Invoke(this, id);
        panel.TaskRescheduled += (_, e) => TaskRescheduled?.Invoke(this, e);
        panel.TaskRenamed += (_, e) => TaskRenamed?.Invoke(this, e);
        panel.TaskCreated += (_, title) => TaskCreated?.Invoke(this, title);
        panel.TaskDeleted += (_, id) => TaskDeleted?.Invoke(this, id);
        panel.WriteRetried += (_, id) => TaskWriteRetried?.Invoke(this, id);
        panel.WriteDiscarded += (_, id) => TaskWriteDiscarded?.Invoke(this, id);
        panel.TaskReopened += (_, id) => TaskReopened?.Invoke(this, id);
        panel.Dismissed += (_, _) => CloseTasks();

        _tasks = panel;
        panel.Render(tasks);
        panel.ShowAt(anchor);
    }

    public void ToggleAgenda(
        IReadOnlyList<AgendaItem> agenda,
        IReadOnlyList<BreakSlot> breaks,
        WorkDayOptions work,
        IReadOnlyList<TaskRow> pauta)
    {
        if (_agenda is not null)
        {
            CloseAgenda();
            return;
        }

        if (_window.CurrentRect is not { } anchor) return;

        CloseTasks();
        ClosePauta();

        var panel = new AgendaPanel(_window.CurrentPalette, anchor);
        panel.MeetingActivated += (_, url) => MeetingActivated?.Invoke(this, url);
        panel.PautaRequested += (_, id) => PautaRequested?.Invoke(this, id);
        panel.Dismissed += (_, _) => CloseAgenda();

        _agenda = panel;
        panel.Render(agenda, breaks, DateTimeOffset.Now, work, Pauta.CountsFor(pauta));
        panel.ShowAt(anchor);
    }

    /// <summary>
    /// Abre a pauta de uma reunião, ou fecha se já for a mesma. Clicar no contador com a pauta de
    /// <b>outra</b> reunião aberta troca de reunião em vez de fechar — é o que o gesto quer dizer.
    /// </summary>
    public void TogglePauta(AgendaItem meeting, IReadOnlyList<TaskRow> items)
    {
        if (_pauta is { } aberto)
        {
            var mesma = aberto.EventId == meeting.Id;
            ClosePauta();

            if (mesma) return;
        }

        if (_window.CurrentRect is not { } anchor) return;

        CloseTasks();
        CloseAgenda();

        var panel = new PautaPanel(_window.CurrentPalette, anchor);
        panel.ItemCreated += (_, text) => PautaItemCreated?.Invoke(this, (meeting.Id, text));
        panel.ItemToggled += (_, id) => PautaItemToggled?.Invoke(this, id);
        panel.ItemDeleted += (_, id) => PautaItemDeleted?.Invoke(this, id);
        panel.WriteRetried += (_, id) => TaskWriteRetried?.Invoke(this, id);
        panel.WriteDiscarded += (_, id) => TaskWriteDiscarded?.Invoke(this, id);
        panel.Dismissed += (_, _) => ClosePauta();

        _pauta = panel;
        panel.Render(meeting, items);
        panel.ShowAt(anchor);
    }

    public void RefreshOpenPanel(
        IReadOnlyList<TaskRow> tasks,
        IReadOnlyList<AgendaItem> agenda,
        IReadOnlyList<BreakSlot> breaks,
        WorkDayOptions work,
        IReadOnlyList<TaskRow> pauta)
    {
        _tasks?.Render(tasks);
        _agenda?.Render(agenda, breaks, DateTimeOffset.Now, work, Pauta.CountsFor(pauta));

        // A reunião é reencontrada a cada rodada porque o retrato é novo — mas se ela saiu da
        // janela (apagada, ou passada dos 15 dias), o painel fica com o que tem em vez de piscar
        // vazio. Ele morre no próximo clique fora, que é o ciclo de vida normal de um painel.
        if (_pauta is { EventId: { } id }
            && agenda.FirstOrDefault(a => a.Id == id) is { } meeting)
        {
            _pauta.Render(meeting, Pauta.RowsFor(pauta, id));
        }
    }

    private void ClosePauta()
    {
        if (_pauta is null) return;

        var panel = _pauta;
        _pauta = null; // antes de Close(), senão Deactivated reentra aqui
        panel.Close();
    }

    private void CloseTasks()
    {
        if (_tasks is null) return;

        var panel = _tasks;
        _tasks = null; // antes de Close(), senão Deactivated reentra aqui
        panel.Close();
    }

    private void CloseAgenda()
    {
        if (_agenda is null) return;

        var panel = _agenda;
        _agenda = null;
        panel.Close();
    }

    public void Dispose()
    {
        CloseTasks();
        CloseAgenda();
        ClosePauta();
        _window.Teardown();
        _window.Close();
    }
}
