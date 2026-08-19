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

    /// <summary>
    /// Abre o painel S2, ou fecha se já estiver aberto. Recebe <see cref="TaskRow"/>, e não
    /// <c>TaskItem</c>, porque a lista que a tela mostra inclui o que o usuário acabou de fazer e
    /// ainda não subiu (D-029).
    /// </summary>
    void ToggleTasks(IReadOnlyList<TaskRow> tasks);

    /// <summary>Abre o painel S3, ou fecha se já estiver aberto.</summary>
    void ToggleAgenda(IReadOnlyList<AgendaItem> agenda, IReadOnlyList<BreakSlot> breaks);

    /// <summary>
    /// Redesenha o painel aberto, se houver, sem alternar visibilidade. Usado quando os dados
    /// mudam por baixo de um painel já aberto — concluir uma tarefa, por exemplo.
    /// </summary>
    void RefreshOpenPanel(
        IReadOnlyList<TaskRow> tasks,
        IReadOnlyList<AgendaItem> agenda,
        IReadOnlyList<BreakSlot> breaks);

    /// <summary>Usuário clicou no alerta — "eu vi" (<c>SEVERITY.md</c> §1.1).</summary>
    event EventHandler? Acknowledged;

    /// <summary>
    /// Alterna a dispensa da folga de <b>hoje</b>. Não liga nem desliga a funcionalidade: isso é
    /// configuração de instalação, mora no appsettings e não tem gesto na barra.
    /// </summary>
    event EventHandler? BreakDismissToggled;

    /// <summary>Clique no slot da pausa: "tirei essa" (D-006, D-023).</summary>
    event EventHandler? BreakTaken;

    /// <summary>"Agora não": empurra a pausa para frente (D-019).</summary>
    event EventHandler? BreakPostponed;

    /// <summary>"Estou tirando agora": fixa a pausa do período na hora atual.</summary>
    event EventHandler? BreakStartedNow;

    /// <summary>Escolha do evento ativo entre reunioes sobrepostas (SEVERITY 8, D-009).</summary>
    event EventHandler<string>? ActiveEventChosen;

    /// <summary>Usuário clicou na barra offline e quer reautenticar (<c>SEVERITY.md</c> §6).</summary>
    event EventHandler? ReauthRequested;

    /// <summary>Painel de tarefas, superfície S2 do SPEC.</summary>
    event EventHandler? TasksRequested;

    /// <summary>Painel de agenda, superfície S3 do SPEC.</summary>
    event EventHandler? AgendaRequested;

    event EventHandler? MailRequested;

    event EventHandler? SyncRequested;

    /// <summary>Abrir a tela de configuração (D-020).</summary>
    event EventHandler? SettingsRequested;

    event EventHandler? ExitRequested;

    /// <summary>Tarefa marcada ou desmarcada no painel S2. Carrega o id.</summary>
    event EventHandler<string>? TaskToggled;

    /// <summary>Tarefa criada no painel S2. Carrega o título.</summary>
    event EventHandler<string>? TaskCreated;

    /// <summary>Exclus�o confirmada de tarefa. Carrega o id. N�o tem volta (D-024).</summary>
    event EventHandler<string>? TaskDeleted;

    /// <summary>Evento com Meet acionado no painel S3. Carrega a URL.</summary>
    event EventHandler<string>? MeetingActivated;
}
