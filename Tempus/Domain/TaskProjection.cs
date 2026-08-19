namespace Tempus.Domain;

/// <summary>Uma linha da lista: a tarefa e, se houver, a intenção de escrita que pesa sobre ela.</summary>
internal sealed record TaskRow
{
    public required TaskItem Item { get; init; }

    public PendingWrite? Write { get; init; }

    /// <summary>Ainda subindo. A tela já mostra o resultado, mas ele não está confirmado.</summary>
    public bool IsPending => Write is { State: WriteState.Pending };

    /// <summary>Desistiu. A tela voltou à verdade do servidor e cobra uma decisão do usuário.</summary>
    public bool HasFailed => Write is { State: WriteState.Failed };
}

/// <summary>
/// O que a tela mostra: o retrato do servidor com as intenções pendentes aplicadas por cima.
/// Função pura (regra 8) — sem rede, sem relógio, sem estado.
/// <para>
/// <b>A regra que resume tudo:</b> o efeito otimista vale <i>enquanto</i> a escrita está pendente.
/// Assim que ela falha, a linha volta à verdade do servidor e carrega o aviso. É a reversão, e ela
/// não precisa de código próprio — cai da mesma função.
/// </para>
/// <para>
/// Isto <b>não</b> fere a regra 10 do projeto ("nunca mostrar dado velho como se fosse atual").
/// Pendente não é dado velho: é o dado mais novo que existe, produzido pelo próprio usuário há um
/// instante. O que a regra proíbe é apresentar o passado como presente, e é justamente o que a
/// reversão evita.
/// </para>
/// </summary>
internal static class TaskProjection
{
    public static IReadOnlyList<TaskRow> Apply(
        IReadOnlyList<TaskItem> server, IReadOnlyList<PendingWrite> pending)
    {
        var rows = server.Select(t => new TaskRow { Item = t }).ToList();

        foreach (var write in pending)
        {
            // Falhada não esconde nem transforma nada: só se anuncia sobre a linha que o servidor
            // de fato tem. A exceção é a criação, cuja linha só existe aqui.
            if (write.State == WriteState.Failed)
            {
                MarkFailure(rows, write);
                continue;
            }

            switch (write.Kind)
            {
                case WriteKind.Create:
                    rows.Add(Provisional(write));
                    break;

                case WriteKind.Complete:
                    Replace(rows, write.TaskId, row => row with
                    {
                        Item = row.Item with { IsCompleted = true },
                        Write = write,
                    });
                    break;

                case WriteKind.Delete:
                    rows.RemoveAll(r => r.Item.Id == write.TaskId);
                    break;
            }
        }

        return rows;
    }

    /// <summary>
    /// O servidor já está do jeito que a intenção queria, então ela sai da fila.
    /// <para>
    /// É aqui que "<b>Google ganha empate</b>" acontece: confirmada, a escrita deixa de existir e o
    /// retrato do servidor volta a ser a única verdade. Também é o que impede duplicata depois de
    /// um restart, quando a resposta se perdeu mas a escrita tinha dado certo.
    /// </para>
    /// </summary>
    public static bool IsConfirmed(PendingWrite write, IReadOnlyList<TaskItem> server) => write.Kind switch
    {
        // Sem timestamp de criação no retrato, título aberto e igual é o melhor sinal disponível.
        // Erra para o lado de não duplicar, que é o lado certo: uma criação engolida se refaz com
        // um clique, e uma tarefa duplicada incomoda até alguém apagar na mão.
        WriteKind.Create => server.Any(t => !t.IsCompleted && SameTitle(t.Title, write.Title)),

        // Ausente conta como concluída: a leitura pede ShowCompleted = false, então tarefa
        // concluída simplesmente some do retrato.
        WriteKind.Complete => server.FirstOrDefault(t => t.Id == write.TaskId) is null or { IsCompleted: true },

        WriteKind.Delete => server.All(t => t.Id != write.TaskId),

        _ => false,
    };

    private static TaskRow Provisional(PendingWrite write) => new()
    {
        Item = new TaskItem
        {
            Id = write.ProvisionalId,
            ListId = write.ListId,
            Title = write.Title ?? "",
        },
        Write = write,
    };

    private static void MarkFailure(List<TaskRow> rows, PendingWrite write)
    {
        if (write.Kind == WriteKind.Create)
        {
            // A linha provisória continua na tela mesmo falhada — é o único lugar onde o título
            // digitado existe. Sumir com ela seria perder o dado, que é o defeito que esta fatia
            // inteira existe para consertar.
            rows.Add(Provisional(write));
            return;
        }

        Replace(rows, write.TaskId, row => row with { Write = write });
    }

    private static void Replace(List<TaskRow> rows, string? taskId, Func<TaskRow, TaskRow> change)
    {
        var index = rows.FindIndex(r => r.Item.Id == taskId);
        if (index >= 0) rows[index] = change(rows[index]);
    }

    private static bool SameTitle(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
