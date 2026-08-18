namespace Tempus.Domain;

/// <summary>
/// Qual reunião está valendo quando duas se sobrepõem (<c>SEVERITY.md</c> §8, D-009).
/// <para>
/// O Tempus não tem como saber em qual você está — não há detecção de presença (D-006). Ele
/// <b>pergunta</b>, e enquanto não há resposta assume um padrão determinístico, para a barra nunca
/// ficar sem estado enquanto espera um clique que pode nunca vir.
/// </para>
/// </summary>
internal static class ActiveEvent
{
    /// <summary>As reuniões que cobrem o instante atual. Duas ou mais é o caso ambíguo.</summary>
    public static IReadOnlyList<AgendaItem> Candidates(
        IReadOnlyList<AgendaItem> agenda, DateTimeOffset now) =>
        [.. agenda.Where(e => !e.IsAllDay && e.IsRunningAt(now)).OrderBy(e => e.Start)];

    /// <summary>
    /// A escolha do usuário, se ainda vale, ou o padrão determinístico.
    /// <para>
    /// A ordem do §8 é <c>accepted &gt; tentative &gt; needsAction</c>, desempatando pelo início
    /// mais cedo e depois pelo fim mais cedo. A intuição por trás: entre duas reuniões
    /// simultâneas, aquela que você confirmou é a que provavelmente está atendendo.
    /// </para>
    /// </summary>
    public static AgendaItem? Choose(IReadOnlyList<AgendaItem> candidates, string? chosenId)
    {
        if (candidates.Count == 0) return null;

        // A escolha vale só enquanto o evento escolhido ainda é candidato — quando ele termina,
        // reavaliar (§8). Sem essa checagem, uma escolha velha calaria a próxima ambiguidade.
        if (chosenId is not null && candidates.FirstOrDefault(c => c.Id == chosenId) is { } escolhido)
            return escolhido;

        return candidates
            .OrderBy(c => Rank(c.Rsvp))
            .ThenBy(c => c.Start)
            .ThenBy(c => c.End)
            .First();
    }

    public static bool Overlaps(AgendaItem a, AgendaItem b) => a.Start < b.End && b.Start < a.End;

    private static int Rank(Rsvp rsvp) => rsvp switch
    {
        Rsvp.Accepted => 0,
        Rsvp.Tentative => 1,
        Rsvp.NeedsAction => 2,

        // Sem convidados é compromisso próprio: vem antes de um convite não respondido, porque
        // você o criou, mas depois dos que confirmou explicitamente.
        _ => 1,
    };
}
