using Tempus.Domain;

namespace Tempus.Shell;

/// <summary>
/// A superfície onde o Tempus aparece.
/// <para>
/// D-002: toda renderização fica atrás desta interface. A Deskband morreu no Windows 11, então
/// a implementação primária é uma barra flutuante posicionada sobre a taskbar — abordagem que
/// entrega o visual pretendido mas é sensível a mudanças do shell. Se um update do Windows
/// quebrar o posicionamento, o fallback (<c>TraySurface</c>, ícones na área de notificação) entra
/// como troca de implementação, não de arquitetura.
/// </para>
/// <para>
/// Nenhuma lógica de domínio pode conhecer a implementação concreta.
/// </para>
/// </summary>
internal interface IShellSurface : IDisposable
{
    void Show();

    /// <summary>Idempotente: chamar com o mesmo estado duas vezes não deve causar efeito visual.</summary>
    void Render(ShellState state);

    /// <summary>Abre o painel S2, ou fecha se já estiver aberto.</summary>
    void ToggleTasks(IReadOnlyList<TaskItem> tasks);

    /// <summary>Abre o painel S3, ou fecha se já estiver aberto.</summary>
    void ToggleAgenda(IReadOnlyList<AgendaItem> agenda);

    /// <summary>
    /// Redesenha o painel aberto, se houver, sem alternar visibilidade. Usado quando os dados
    /// mudam por baixo de um painel já aberto — concluir uma tarefa, por exemplo.
    /// </summary>
    void RefreshOpenPanel(IReadOnlyList<TaskItem> tasks, IReadOnlyList<AgendaItem> agenda);

    /// <summary>Usuário clicou no alerta — "eu vi" (<c>SEVERITY.md</c> §1.1).</summary>
    event EventHandler? Acknowledged;

    /// <summary>Usuário clicou na barra offline e quer reautenticar (<c>SEVERITY.md</c> §6).</summary>
    event EventHandler? ReauthRequested;

    /// <summary>Painel de tarefas, superfície S2 do SPEC.</summary>
    event EventHandler? TasksRequested;

    /// <summary>Painel de agenda, superfície S3 do SPEC.</summary>
    event EventHandler? AgendaRequested;

    event EventHandler? MailRequested;

    event EventHandler? SyncRequested;

    event EventHandler? ExitRequested;

    /// <summary>Tarefa marcada ou desmarcada no painel S2. Carrega o id.</summary>
    event EventHandler<string>? TaskToggled;

    /// <summary>Tarefa criada no painel S2. Carrega o título.</summary>
    event EventHandler<string>? TaskCreated;

    /// <summary>Evento com Meet acionado no painel S3. Carrega a URL.</summary>
    event EventHandler<string>? MeetingActivated;
}
