namespace Tempus.Domain;

/// <summary>Como uma tarefa aberta chega até a UI. Espelha o essencial do Google Tasks.</summary>
internal sealed record TaskItem
{
    public required string Id { get; init; }

    /// <summary>
    /// Lista do Google Tasks a que pertence. Necessário para escrever: a API endereça tarefa por
    /// (lista, id), não por id sozinho.
    /// </summary>
    public string? ListId { get; init; }

    public required string Title { get; init; }

    /// <summary>
    /// Notas da tarefa. O Tempus não as exibe em lugar nenhum — elas existem para carregar o
    /// vínculo <c>evento:&lt;id&gt;</c> de um assunto de pauta com a reunião dele (D-052).
    /// <para>
    /// Lido <b>e</b> preservado: o <c>Reschedule</c> faz <c>get</c> antes de <c>update</c>
    /// justamente para não apagar este campo, e a razão vale para qualquer escrita futura.
    /// </para>
    /// </summary>
    public string? Notes { get; init; }

    /// <summary>Vencimento. O Google Tasks guarda data sem hora, então <see cref="DateOnly"/>.</summary>
    public DateOnly? Due { get; init; }

    public bool IsCompleted { get; init; }

    /// <summary>
    /// Quando foi concluída. Serve para ordenar a lista de concluídas da mais recente para a mais
    /// antiga — que é a ordem em que a tarefa marcada sem querer está no topo, ao alcance do
    /// clique que a desfaz (D-030).
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    public TaskBucket Bucket(DateOnly today) => Due switch
    {
        null => TaskBucket.NoDate,
        var d when d < today => TaskBucket.Overdue,
        var d when d == today => TaskBucket.Today,
        _ => TaskBucket.Later,
    };
}

/// <summary>
/// Agrupamento do painel S2. A ordem do enum é a ordem de exibição — vencidas primeiro,
/// porque são as únicas que alimentam um sinal de severidade (<c>TaskOverdue</c>).
/// </summary>
internal enum TaskBucket
{
    Overdue,
    Today,
    Later,
    NoDate,
}

internal static class TaskBucketNames
{
    public static string Label(this TaskBucket bucket) => bucket switch
    {
        TaskBucket.Overdue => "Vencidas",
        TaskBucket.Today => "Hoje",
        TaskBucket.Later => "Depois",
        _ => "Sem data",
    };
}
