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

    public FloatingBarSurface(BarOptions options)
    {
        _window = new FloatingBarWindow(options);

        _window.Acknowledged += (_, e) => Acknowledged?.Invoke(this, e);
        _window.ReauthRequested += (_, e) => ReauthRequested?.Invoke(this, e);
        _window.TasksRequested += (_, e) => TasksRequested?.Invoke(this, e);
        _window.AgendaRequested += (_, e) => AgendaRequested?.Invoke(this, e);
        _window.MailRequested += (_, e) => MailRequested?.Invoke(this, e);
        _window.SyncRequested += (_, e) => SyncRequested?.Invoke(this, e);
        _window.ExitRequested += (_, e) => ExitRequested?.Invoke(this, e);
    }

    public event EventHandler? Acknowledged;
    public event EventHandler? ReauthRequested;
    public event EventHandler? TasksRequested;
    public event EventHandler? AgendaRequested;
    public event EventHandler? MailRequested;
    public event EventHandler? SyncRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<string>? TaskToggled;
    public event EventHandler<string>? TaskCreated;
    public event EventHandler<string>? MeetingActivated;

    public void Show() => _window.Show();

    public void Render(ShellState state) => _window.Render(state);

    public void ToggleTasks(IReadOnlyList<TaskItem> tasks)
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

        var panel = new TasksPanel(_window.CurrentPalette, anchor);
        panel.TaskToggled += (_, id) => TaskToggled?.Invoke(this, id);
        panel.TaskCreated += (_, title) => TaskCreated?.Invoke(this, title);
        panel.Dismissed += (_, _) => CloseTasks();

        _tasks = panel;
        panel.Render(tasks);
        panel.ShowAt(anchor);
    }

    public void ToggleAgenda(IReadOnlyList<AgendaItem> agenda)
    {
        if (_agenda is not null)
        {
            CloseAgenda();
            return;
        }

        if (_window.CurrentRect is not { } anchor) return;

        CloseTasks();

        var panel = new AgendaPanel(_window.CurrentPalette, anchor);
        panel.MeetingActivated += (_, url) => MeetingActivated?.Invoke(this, url);
        panel.Dismissed += (_, _) => CloseAgenda();

        _agenda = panel;
        panel.Render(agenda, DateTimeOffset.Now);
        panel.ShowAt(anchor);
    }

    public void RefreshOpenPanel(IReadOnlyList<TaskItem> tasks, IReadOnlyList<AgendaItem> agenda)
    {
        _tasks?.Render(tasks);
        _agenda?.Render(agenda, DateTimeOffset.Now);
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
        _window.Teardown();
        _window.Close();
    }
}
