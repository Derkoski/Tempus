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

    public IReadOnlyList<AgendaItem> Agenda { get; init; } = [];

    public IReadOnlyList<TaskItem> Tasks { get; init; } = [];

    /// <summary>Não lidos na caixa de entrada. Só a contagem — nunca conteúdo de mensagem.</summary>
    public int? UnreadMail { get; init; }

    /// <summary>Quando os dados acima foram obtidos. <c>null</c> = nunca sincronizou.</summary>
    public DateTimeOffset? LastSuccessAt { get; init; }

    /// <summary>Texto curto para a barra quando algo está errado.</summary>
    public string? Message { get; init; }

    /// <summary>Lista padrão do Google Tasks, destino de tarefas criadas na barra.</summary>
    public string? DefaultTaskListId { get; init; }

    /// <summary>
    /// Dados velhos são pior que nenhum dado (regra 10): passados 10 minutos sem sync bem
    /// sucedida, a barra vai para <c>Offline</c> e os contadores viram <c>—</c>.
    /// </summary>
    public bool IsStale(DateTimeOffset now) =>
        LastSuccessAt is null || now - LastSuccessAt.Value > TimeSpan.FromMinutes(10);

    public bool IsUsable(DateTimeOffset now) => Health is SyncHealth.Ok or SyncHealth.Failing
        && !IsStale(now);
}
