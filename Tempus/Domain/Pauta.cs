namespace Tempus.Domain;

/// <summary>
/// A pauta de uma reunião: os assuntos que você quer levantar nela (D-052).
/// <para>
/// <b>Um assunto não é uma tarefa.</b> Ele nasce ligado a uma reunião, vale só até ela acontecer, e
/// "resolver" é dizê-lo em voz alta. Guardá-los na lista de tarefas — que era o jeito antes disto —
/// inflava o contador que responde "quanto trabalho está aberto?" e deixava para trás pendências
/// que nunca foram pendências.
/// </para>
/// <para>
/// Funções puras de (tarefas da lista de pauta, id do evento) → assuntos (regra 8). Quem guarda é o
/// Google Tasks, numa lista dedicada; quem escreve é a <c>WriteQueue</c>, com os mesmos verbos de
/// sempre.
/// </para>
/// </summary>
internal static class Pauta
{
    /// <summary>
    /// Nome da lista no Google Tasks. Aparece assim no celular, então precisa dizer de onde veio
    /// para quem topar com ela sem contexto.
    /// </summary>
    public const string ListTitle = "Tempus · pautas";

    /// <summary>
    /// Prefixo do vínculo dentro das notas. Uma linha só, no começo — o resto das notas fica livre
    /// para quem quiser escrever algo pelo app do Google.
    /// </summary>
    private const string Marker = "evento:";

    /// <summary>
    /// A nota que amarra um assunto à reunião.
    /// <para>
    /// <b>Id do evento, e não a ocorrência do §7.</b> O <c>Signal.OccurrenceFor</c> embute início e
    /// fim de propósito, para que prorrogar uma reunião traga o alarme de volta — mas aqui esse
    /// mesmo comportamento apagaria a pauta se o organizador mexesse quinze minutos no horário. Com
    /// <c>SingleEvents</c>, cada ocorrência de uma recorrente já tem id próprio, então o id sozinho
    /// distingue a Daily de hoje da de amanhã <b>e</b> sobrevive à remarcação.
    /// </para>
    /// </summary>
    public static string NotesFor(string eventId) => $"{Marker}{eventId}";

    /// <summary>
    /// De qual reunião este assunto é, ou <c>null</c> se não dá para saber.
    /// <para>
    /// Nada aqui pode lançar. A lista é editável pelo app do Google e pelo celular, então nota
    /// vazia, truncada ou escrita à mão é situação normal — e um item sem vínculo legível
    /// simplesmente não aparece em pauta nenhuma, em vez de aparecer na errada.
    /// </para>
    /// </summary>
    public static string? EventIdOf(TaskItem task)
    {
        if (task.Notes is not { Length: > 0 } notes) return null;

        foreach (var line in notes.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(Marker, StringComparison.Ordinal)) continue;

            var id = trimmed[Marker.Length..].Trim();
            if (id.Length > 0) return id;
        }

        return null;
    }

    /// <summary>
    /// Os assuntos daquela reunião, não ditos primeiro.
    /// <para>
    /// Riscados no fim, e não escondidos: durante a call eles são a prova visível do que já foi
    /// dito, e some-los faria a lista encolher debaixo do olho de quem está falando.
    /// </para>
    /// </summary>
    public static IReadOnlyList<TaskItem> For(IReadOnlyList<TaskItem> pauta, string? eventId)
    {
        if (eventId is null) return [];

        // OrderBy do LINQ é estável, então a ordem de chegada se mantém dentro de cada metade —
        // ordenar por id jogaria o assunto recém-criado para um lugar arbitrário da lista, e a
        // linha provisória tem id que não se parece com nenhum outro.
        return [.. pauta.Where(t => EventIdOf(t) == eventId).OrderBy(t => t.IsCompleted)];
    }

    /// <summary>
    /// O mesmo recorte sobre linhas já projetadas, para o painel enxergar as escritas que ainda não
    /// subiram (D-029). Um assunto recém-digitado precisa aparecer <b>antes</b> de o Google
    /// confirmá-lo — é a promessa da escrita otimista, e ela não pode valer só para tarefas.
    /// </summary>
    public static IReadOnlyList<TaskRow> RowsFor(IReadOnlyList<TaskRow> rows, string? eventId)
    {
        if (eventId is null) return [];

        return [.. rows.Where(r => EventIdOf(r.Item) == eventId).OrderBy(r => r.Item.IsCompleted)];
    }

    /// <summary>
    /// Quantos assuntos cada reunião tem, por id de evento — o que o painel de agenda precisa para
    /// desenhar o selo em quinze linhas sem varrer a pauta inteira uma vez por linha.
    /// </summary>
    public static IReadOnlyDictionary<string, int> CountsFor(IReadOnlyList<TaskRow> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (EventIdOf(row.Item) is not { } id) continue;

            counts[id] = counts.TryGetValue(id, out var n) ? n + 1 : 1;
        }

        return counts;
    }

    /// <summary>
    /// Quantos assuntos ainda não foram ditos. É o número que a barra mostra.
    /// <para>
    /// Zero quando não há reunião, quando ela não tem pauta, e também quando <b>tudo</b> foi
    /// riscado — os três casos apagam o contador, que é o comportamento certo: terminar a pauta
    /// devolve a barra ao normal.
    /// </para>
    /// </summary>
    public static int Pending(IReadOnlyList<TaskItem> pauta, string? eventId)
    {
        if (eventId is null) return 0;

        return pauta.Count(t => !t.IsCompleted && EventIdOf(t) == eventId);
    }

    /// <summary>
    /// Separa o que veio da lista de pauta do que é tarefa de verdade.
    /// <para>
    /// Um lugar só, e é o que impede a funcionalidade de recriar o problema que ela resolve: sem
    /// este corte os assuntos entrariam no contador da barra, no painel S2 e no sinal
    /// <c>TaskOverdue</c> do §2.3.
    /// </para>
    /// <para>
    /// Sem lista de pauta conhecida, tudo é tarefa — que é o estado de quem nunca usou a
    /// funcionalidade, e nele nada muda.
    /// </para>
    /// </summary>
    public static (IReadOnlyList<TaskItem> Tasks, IReadOnlyList<TaskItem> Pauta) Split(
        IReadOnlyList<TaskItem> all, string? pautaListId)
    {
        if (pautaListId is null) return (all, []);

        var tasks = new List<TaskItem>();
        var pauta = new List<TaskItem>();

        foreach (var item in all)
        {
            if (item.ListId == pautaListId) pauta.Add(item);
            else tasks.Add(item);
        }

        return (tasks, pauta);
    }
}
