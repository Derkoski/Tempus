namespace Tempus.Domain;

/// <summary>
/// Junta os compromissos vindos de vários calendários num só dia (D-034).
/// <para>
/// Existe por causa de um risco concreto: uma reunião pode chegar pelos <b>dois</b> caminhos — o
/// convite no Google e o mesmo evento no calendário importado do Teams. Dois eventos idênticos e
/// sobrepostos fazem <see cref="ActiveEvent.Candidates"/> devolver dois, e a barra passa a
/// perguntar "2 reuniões agora — qual?" (§8) <b>sobre a mesma call</b>.
/// </para>
/// <para>
/// O seletor do §8 existe para ambiguidade de verdade. Disparar por duplicata o transformaria em
/// ruído diário e ensinaria a ignorá-lo — que é o pior desfecho para qualquer coisa nesta barra.
/// </para>
/// </summary>
internal static class AgendaMerge
{
    /// <summary>
    /// Remove repetições, preservando a ordem de chegada.
    /// <para>
    /// <b>Vence o primeiro</b>, e por isso quem chama passa o calendário principal na frente: é
    /// dele que vêm o RSVP e o <c>conferenceData</c>, que o calendário assinado não tem. Manter o
    /// segundo perderia o link da call e o "recusado".
    /// </para>
    /// </summary>
    public static IReadOnlyList<AgendaItem> Dedupe(IReadOnlyList<AgendaItem> items)
    {
        var seen = new HashSet<string>();
        var merged = new List<AgendaItem>(items.Count);

        foreach (var item in items)
            if (seen.Add(KeyOf(item)))
                merged.Add(item);

        return merged;
    }

    /// <summary>
    /// Mesmo título, mesmo início e mesmo fim: é o mesmo compromisso.
    /// <para>
    /// O <b>id não serve</b> — é justamente o que difere entre as duas cópias, porque cada
    /// calendário emite o seu. E o horário sozinho também não: duas reuniões diferentes marcadas
    /// para as 14:00 são ambiguidade real, e é obrigação do §8 perguntar.
    /// </para>
    /// </summary>
    private static string KeyOf(AgendaItem item) =>
        $"{Normalize(item.Title)}|{item.Start:O}|{item.End:O}";

    /// <summary>
    /// Caixa e espaços não distinguem compromisso. Duas importações do mesmo convite costumam
    /// diferir num espaço a mais ou numa letra maiúscula, e tratá-las como eventos distintos
    /// devolveria exatamente o falso positivo que esta classe existe para evitar.
    /// </summary>
    private static string Normalize(string title) =>
        string.Join(' ', title.Split(' ', StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries)).ToLowerInvariant();
}
