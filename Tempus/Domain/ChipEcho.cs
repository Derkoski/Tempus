namespace Tempus.Domain;

/// <summary>
/// Quando o chip de motivo está apenas repetindo o que o slot de tempo já contou (D-033).
/// <para>
/// A barra tem dois vocabulários (§0.5), e durante o ciclo de call eles frequentemente narram o
/// <b>mesmo acontecimento</b>: o slot diz "Ocupado" e o chip diz "Daily começou às 14:00". Dizer
/// duas vezes gasta o recurso mais escasso do produto (regra 1) para não acrescentar nada.
/// </para>
/// <para>
/// <b>O slot é o dono da narrativa de call.</b> O chip existe para o que o slot não sabe dizer —
/// tarefas vencidas, fronteiras do dia, ou uma pergunta que só ele faz.
/// </para>
/// </summary>
internal static class ChipEcho
{
    /// <summary>
    /// Quais sinais cada humor já conta. <b>Tabela explícita, e não regra por categoria</b>: a
    /// regra "todo sinal de categoria <c>Call</c> é eco" pareceria mais elegante e estaria errada.
    /// <para>
    /// <c>MeetingAmbiguous</c> é de categoria <c>Call</c> e fala do mesmo evento, mas ali o chip
    /// está <b>fazendo uma pergunta</b> que o slot não tem como fazer — e cujo clique abre o
    /// seletor do §8. Calá-lo quebraria o gesto. Ele não aparece em linha nenhuma, então nunca é
    /// suprimido.
    /// </para>
    /// </summary>
    private static readonly Dictionary<TimeMood, string[]> AlreadyTold = new()
    {
        [TimeMood.Approaching] = [SignalNames.MeetingUpcoming],
        [TimeMood.Imminent] = [SignalNames.MeetingImminent],
        [TimeMood.InMeeting] = [SignalNames.MeetingStarted, SignalNames.MeetingBackToBack],
        [TimeMood.EndingSoon] = [SignalNames.MeetingStarted, SignalNames.MeetingBackToBack],
        [TimeMood.Overrun] = [SignalNames.MeetingRanIntoNext, SignalNames.MeetingEnded],
    };

    /// <summary>
    /// True quando o chip não acrescenta nada ao que o slot já mostra e deve ficar quieto.
    /// <para>
    /// <b>Só de pintura.</b> O sinal continua vencendo a arbitragem (§4), alimentando o toast (§5)
    /// e aceitando o reconhecimento (§10) — some o desenho, não o alarme.
    /// </para>
    /// </summary>
    public static bool Repeats(TimeMood mood, string? signalName, string? timeEventId, string? signalEventId)
    {
        if (signalName is null) return false;

        // Sem evento dos dois lados não há como afirmar que falam do mesmo — e na dúvida o chip
        // fala. Perder um alarme é caro; repetir uma informação, não.
        if (timeEventId is null || signalEventId is null) return false;
        if (!string.Equals(timeEventId, signalEventId, StringComparison.Ordinal)) return false;

        return AlreadyTold.TryGetValue(mood, out var told) && told.Contains(signalName);
    }
}
