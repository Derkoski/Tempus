namespace Tempus.Domain;

/// <summary>
/// "Já saí desta reunião" — a call acabou antes do horário marcado (D-041).
/// <para>
/// É a forma do <b>D-006</b>: o Tempus não infere presença, o usuário declara. Sem isto a barra
/// continuava contando <c>faltam 22 min</c> de uma reunião que tinha acabado, virava âmbar em
/// <c>Encerrando</c>, e — o pior — acendia o vermelho de <c>MeetingRanIntoNext</c> quando a
/// seguinte começava. Nível 3 falso, por uma reunião que o usuário já tinha deixado.
/// </para>
/// <para>
/// Função pura de (agenda, encerradas) → agenda (regra 8). Quem persiste é o
/// <c>AcknowledgementStore</c>, que já guarda ocorrências por dia.
/// </para>
/// </summary>
internal static class MeetingLeft
{
    /// <summary>
    /// Prefixo próprio no mesmo espaço de nomes das outras ocorrências (§7). O conjunto persistido
    /// já é heterogêneo — <c>Overrun|…</c>, <c>DayEnded|2026-08-21</c> —, então isto é mais uma
    /// ocorrência com nome, e não um conceito novo empurrado para dentro de um lugar alheio.
    /// </summary>
    public const string Name = "MeetingLeft";

    /// <summary>
    /// Identidade pelo §7: <c>(sinal, evento, início, fim)</c>. Levar início e fim tem
    /// consequência desejada — <b>prorrogar a reunião no calendário a traz de volta</b>, porque
    /// prorrogar é fato novo, exatamente como o §7 já define para o reconhecimento.
    /// </summary>
    public static string OccurrenceFor(AgendaItem item) => Signal.OccurrenceFor(Name, item);

    /// <summary>
    /// A agenda como o <b>domínio</b> deve enxergá-la: sem as reuniões que o usuário encerrou.
    /// <para>
    /// <b>Remove, não trunca.</b> Encurtar o fim para "agora" faria nascer um <c>MeetingEnded</c> —
    /// âmbar por três minutos. Responder a um "já terminei" explícito com um alerta é a resposta
    /// errada; a pergunta que o produto faz é "algo precisa de mim agora?", e depois deste gesto a
    /// resposta verdadeira é silêncio.
    /// </para>
    /// <para>
    /// Só o humor, o evento ativo e os sinais usam esta lista. O <b>painel do dia continua com a
    /// agenda inteira</b>: a reunião aconteceu, e o gesto não reescreve o dia — ele só para de
    /// cobrar atenção.
    /// </para>
    /// </summary>
    public static IReadOnlyList<AgendaItem> Apply(
        IReadOnlyList<AgendaItem> agenda, IReadOnlySet<string>? left)
    {
        if (left is null || left.Count == 0) return agenda;

        return [.. agenda.Where(item => !left.Contains(OccurrenceFor(item)))];
    }

    /// <summary>
    /// A reunião encerrada que, pelo calendário, <b>ainda estaria em curso</b> — o que o menu
    /// precisa para oferecer o desfazer.
    /// <para>
    /// Depois do fim marcado o gesto deixa de ter efeito observável, então oferecer "reabrir" ali
    /// seria oferecer uma ação que não faz nada. A entrada some sozinha do menu quando a hora
    /// passa, sem ninguém precisar limpá-la.
    /// </para>
    /// </summary>
    public static AgendaItem? Reopenable(
        IReadOnlyList<AgendaItem> agenda, IReadOnlySet<string>? left, DateTimeOffset now)
    {
        if (left is null || left.Count == 0) return null;

        return agenda.FirstOrDefault(
            item => item.IsRunningAt(now) && left.Contains(OccurrenceFor(item)));
    }

    /// <summary>
    /// A reunião que o gesto encerraria agora: <b>a que o humor está nomeando</b>, e só se ela
    /// estiver de fato em curso.
    /// <para>
    /// Amarrar ao humor, em vez de pegar a primeira reunião em curso, resolve dois casos de uma
    /// vez. Em <c>Estourou</c> o humor nomeia a que <i>passou</i> do horário, que já não está
    /// correndo — devolver "a que está em curso" ali entregaria a reunião <b>seguinte</b>, aquela
    /// em que o usuário acabou de entrar, e o gesto encerraria a call errada. E em sobreposição
    /// (§8) o humor já reflete a escolha do usuário sobre qual reunião é a dele.
    /// </para>
    /// <para>
    /// Em <c>Estourou</c> o item some do menu e sobra o "reconhecer alerta", que é o gesto certo
    /// para aquele estado: lá o problema não é a contagem, é o alarme.
    /// </para>
    /// </summary>
    public static AgendaItem? Leavable(
        IReadOnlyList<AgendaItem> agenda, TimeStatus time, DateTimeOffset now)
    {
        if (time.EventId is not { } id) return null;

        return agenda.FirstOrDefault(
            item => item.Id == id && !item.IsAllDay && item.IsRunningAt(now));
    }
}
