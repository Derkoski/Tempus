namespace Tempus.Domain;

/// <summary>
/// Categoria do sinal, usada como <b>desempate</b> quando duas severidades empatam
/// (<c>SEVERITY.md</c> §4). Os valores ordenam da mais alta para a mais baixa.
/// </summary>
internal enum SignalCategory
{
    /// <summary>Nunca compete: e-mail é sempre nível 0 (§2.4).</summary>
    Mail = 0,

    Tasks = 1,
    DayBoundary = 2,

    /// <summary>Vence os empates porque é <b>irreversível no tempo</b>: reunião perdida não volta.</summary>
    Call = 3,
}

/// <summary>
/// Um sinal avaliado: severidade, motivo legível e o que o identifica para supressão.
/// <para>
/// Sinais são <b>independentes entre si</b> (§2). Nenhum sabe da existência dos outros; quem
/// escolhe o vencedor é a arbitragem (§4). É o que permite acrescentar um sinal novo sem reabrir
/// os que já existem.
/// </para>
/// </summary>
internal sealed record Signal
{
    public required string Name { get; init; }
    public required Severity Severity { get; init; }

    /// <summary>Motivo legível. Nunca vazio em severidade ≥ 1 — é a invariante I2.</summary>
    public required string Reason { get; init; }

    public required SignalCategory Category { get; init; }

    /// <summary>
    /// Identidade para reconhecimento (§7). Ligado a evento é
    /// <c>(sinal, eventId, início, fim)</c>; sem evento é <c>(sinal, dataLocal)</c>, o que faz o
    /// reconhecimento valer até a virada do dia.
    /// </summary>
    public required string Occurrence { get; init; }

    /// <summary>
    /// O evento de que este sinal fala, ou <c>null</c> nos que não falam de nenhum — fronteiras do
    /// dia e tarefas. Explícito, e não extraído da <see cref="Occurrence"/>, pelo mesmo motivo do
    /// <see cref="TimeStatus.EventId"/>: comparar por pedaço de string quebraria em silêncio.
    /// </summary>
    public string? EventId { get; init; }

    /// <summary>
    /// Some sozinho depois de uma janela, sem exigir clique. Vale para o que é passageiro por
    /// natureza — uma reunião isolada que acabou não merece cobrar gesto do usuário.
    /// </summary>
    public bool SelfClearing { get; init; }

    /// <summary>
    /// Desde quando o sinal está no ar. <b>Derivado do calendário e do relógio</b>, nunca medido:
    /// <c>MeetingRanIntoNext</c> nasce no fim marcado da reunião e <c>DayEnded</c> às 17:00.
    /// <para>
    /// É o que permite a escalada do nível 3 (§1.1) sem guardar estado — a mesma escolha do D-025
    /// para o humor temporal. Sem isto seria preciso um relógio por sinal, que morreria no
    /// primeiro restart.
    /// </para>
    /// </summary>
    public required DateTimeOffset Since { get; init; }

    /// <summary>Nível 3 no ar há tempo demais sem reconhecimento: deve piscar (§1.1, I8).</summary>
    public bool IsEscalatedAt(DateTimeOffset now, TimeSpan after) =>
        Severity == Severity.Critical && now - Since >= after;

    public static string OccurrenceFor(string name, DateOnly day) => $"{name}|{day:yyyy-MM-dd}";

    public static string OccurrenceFor(string name, AgendaItem item) =>
        $"{name}|{item.Id}|{item.Start:O}|{item.End:O}";
}
