namespace Tempus.Domain;

/// <summary>O que o usuário mandou fazer. Editar título chega na fatia seguinte.</summary>
internal enum WriteKind
{
    Create,
    Complete,
    Delete,

    /// <summary>
    /// Desfaz um <see cref="Complete"/>. É a metade que faltava para concluir poder ser um clique
    /// só: excluir pede dois cliques porque não tem volta, e concluir só pode pedir um porque tem
    /// (D-030).
    /// </summary>
    Reopen,

    /// <summary>
    /// Define ou apaga o vencimento (D-042). <see cref="PendingWrite.Due"/> nulo significa
    /// <b>apagar</b>, e não "não mexer" — a distinção importa, porque no caminho da API as duas
    /// coisas se parecem perigosamente.
    /// </summary>
    Reschedule,
}

internal enum WriteState
{
    /// <summary>Ainda vai acontecer, ou está entre tentativas.</summary>
    Pending,

    /// <summary>Desistiu de tentar sozinha. Espera o usuário mandar repetir ou descartar.</summary>
    Failed,
}

/// <summary>
/// Uma intenção de escrita, como dado.
/// <para>
/// Existe porque hoje a intenção do usuário só existe enquanto a chamada HTTP dura: se ela falha,
/// o clique some sem deixar rastro e sem avisar ninguém. Transformá-la em registro é o que permite
/// mostrar na tela antes de confirmar, repetir depois, e sobreviver a um restart.
/// </para>
/// </summary>
internal sealed record PendingWrite
{
    /// <summary>Identidade da <b>intenção</b>, não da tarefa. Uma criação ainda não tem tarefa.</summary>
    public required string Id { get; init; }

    public required WriteKind Kind { get; init; }

    /// <summary>Alvo no Google. <c>null</c> em <see cref="WriteKind.Create"/>.</summary>
    public string? TaskId { get; init; }

    /// <summary>A API endereça tarefa por (lista, id) — sem a lista não dá para escrever.</summary>
    public string? ListId { get; init; }

    /// <summary>Título de uma criação. É o único conteúdo que <b>só</b> existe aqui.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// O vencimento pretendido, em <see cref="WriteKind.Reschedule"/>. <c>null</c> quer dizer
    /// <b>apagar a data</b> — só é lido quando o verbo é esse, então não há ambiguidade com
    /// "campo não preenchido".
    /// <para>
    /// Data, e não instante: a API descarta a hora e grava só o dia, e o próprio contrato diz
    /// que não é possível ler nem escrever horário de tarefa (D-031).
    /// </para>
    /// </summary>
    public DateOnly? Due { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Quando a próxima tentativa é permitida. <c>null</c> = agora.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    public int Attempts { get; init; }

    public WriteState State { get; init; } = WriteState.Pending;

    /// <summary>Motivo legível da última falha, para a linha do painel ter o que dizer.</summary>
    public string? Failure { get; init; }

    /// <summary>
    /// Id da linha provisória que uma criação ocupa na tela até o Google devolver o id de verdade.
    /// O prefixo evita colisão com id do Google e torna óbvio, em qualquer log, que a linha é local.
    /// </summary>
    public string ProvisionalId => $"pending:{Id}";

    public static PendingWrite For(WriteKind kind, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        Kind = kind,
        CreatedAt = now,
    };
}
