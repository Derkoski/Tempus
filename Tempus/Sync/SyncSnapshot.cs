using Tempus.Domain;

namespace Tempus.Sync;

internal enum SyncHealth
{
    /// <summary>Sem client_secret.json — o app nunca foi configurado.</summary>
    NotConfigured,

    /// <summary>Token ausente, expirado ou revogado. Rotina semanal em modo Testing (D-003).</summary>
    NeedsAuth,

    /// <summary>Autenticado, mas a última chamada falhou (rede, quota, 5xx).</summary>
    Failing,

    Ok,
}

/// <summary>
/// O resultado de uma rodada de sincronização. Imutável de propósito: a UI recebe um retrato
/// completo e não precisa saber de onde cada pedaço veio.
/// </summary>
internal sealed record SyncSnapshot
{
    public static readonly SyncSnapshot Starting = new()
    {
        Health = SyncHealth.NeedsAuth,
        Message = "Conectando…",
    };

    public SyncHealth Health { get; init; } = SyncHealth.NeedsAuth;

    /// <summary>Compromissos de hoje. Alimenta o humor temporal.</summary>
    public IReadOnlyList<AgendaItem> Agenda { get; init; } = [];

    /// <summary>Compromissos de amanhã em diante. Alimenta o <see cref="Domain.Lookahead"/>.</summary>
    public IReadOnlyList<AgendaItem> Upcoming { get; init; } = [];

    /// <summary>
    /// Tarefas de verdade — <b>já sem</b> os assuntos de pauta, separados em
    /// <see cref="Domain.Pauta.Split"/>. É o que alimenta o contador, o painel S2 e o §2.3.
    /// </summary>
    public IReadOnlyList<TaskItem> Tasks { get; init; } = [];

    /// <summary>
    /// Os assuntos de pauta de todas as reuniões da janela (D-052). Cruzados com um evento por
    /// <see cref="Domain.Pauta.For"/>; nunca contados como trabalho aberto.
    /// </summary>
    public IReadOnlyList<TaskItem> Pauta { get; init; } = [];

    /// <summary>Não lidos na caixa de entrada. Só a contagem — nunca conteúdo de mensagem.</summary>
    public int? UnreadMail { get; init; }

    /// <summary>Quando os dados acima foram obtidos. <c>null</c> = nunca sincronizou.</summary>
    public DateTimeOffset? LastSuccessAt { get; init; }

    /// <summary>Texto curto para a barra quando algo está errado.</summary>
    public string? Message { get; init; }

    /// <summary>Lista padrão do Google Tasks, destino de tarefas criadas na barra.</summary>
    public string? DefaultTaskListId { get; init; }

    /// <summary>
    /// A lista <c>Tempus · pautas</c>, ou <c>null</c> enquanto ela não existir — que é o estado de
    /// quem nunca adicionou um assunto, e nele nada muda.
    /// </summary>
    public string? PautaListId { get; init; }

    /// <summary>
    /// Dados velhos são pior que nenhum dado (regra 10): passados 10 minutos sem sync bem
    /// sucedida, a barra vai para <c>Offline</c> e os contadores viram <c>—</c>.
    /// </summary>
    public bool IsStale(DateTimeOffset now) =>
        LastSuccessAt is null || now - LastSuccessAt.Value > TimeSpan.FromMinutes(10);

    public bool IsUsable(DateTimeOffset now) => Health is SyncHealth.Ok or SyncHealth.Failing
        && !IsStale(now);
}
