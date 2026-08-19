namespace Tempus.Domain;

/// <summary>Qual das duas interrupções do nível 3 esta é (<c>SEVERITY.md</c> §5).</summary>
internal enum ToastKind
{
    /// <summary>Ao entrar em vermelho.</summary>
    Entry,

    /// <summary>Ao escalar para piscante, sem reconhecimento, alguns minutos depois.</summary>
    Escalation,
}

/// <summary>Um toast pronto para sair — texto já resolvido, sem nada a decidir na camada de UI.</summary>
internal sealed record ToastRequest
{
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required ToastKind Kind { get; init; }

    /// <summary>
    /// Identidade na Central de Ações. Igual para a entrada e para a escalada da mesma ocorrência,
    /// porque as duas falam da <b>mesma</b> situação — assim a segunda substitui a primeira em vez
    /// de empilhar dois avisos sobre a mesma reunião.
    /// </summary>
    public required string Tag { get; init; }

    /// <summary>
    /// As chaves que passam a contar como enviadas depois desta emissão. Normalmente uma; duas
    /// quando a escalada engole a entrada que nunca saiu.
    /// </summary>
    public required IReadOnlyList<string> Consumes { get; init; }
}

internal sealed record ToastOptions
{
    /// <summary>
    /// Nasce ligado, ao contrário das pausas: toast de nível 3 não é funcionalidade opcional, é a
    /// contraparte da regra 2 — todo nível 3 é reconhecível, e ele só é reconhecível se você ficar
    /// sabendo. O interruptor existe para desligar depois de conviver, não para ligar depois.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Minutos até a segunda interrupção. Acompanha a escalada visual do §1.1.</summary>
    public int EscalationMinutes { get; init; } = 5;

    public static readonly ToastOptions Default = new();
}

/// <summary>
/// Quando interromper, e com que texto (<c>SEVERITY.md</c> §5).
/// <para>
/// <b>Só nível 3 nesta rodada.</b> O §5 prevê toast em âmbar para três transições escolhidas e um
/// aviso ao entrar em <c>Offline</c>; ficaram de fora de propósito. Cor é ambiente e toast é
/// interrupção — o orçamento de interrupção é menor que o de cor, e começar pelos dois sinais que
/// nunca se limpam sozinhos dá uma ou duas interrupções por dia em vez de uma dúzia. Subir o
/// escopo depois é acrescentar casos aqui; descer, depois de acostumar o usuário a ser
/// interrompido, é mais caro.
/// </para>
/// <para>
/// Função pura como todo o resto do domínio (regra 8): a supressão de apresentação chega como
/// booleano e o conjunto do que já saiu chega como parâmetro. Quem fala com o Windows é o
/// <c>ToastNotifier</c>.
/// </para>
/// </summary>
internal static class ToastPolicy
{
    /// <param name="winner">O sinal que a barra está exibindo — já passado pela arbitragem e pela
    /// histerese. Usar o vencedor exibido, e não a lista bruta, garante que o toast nunca fale de
    /// algo que a barra não está mostrando.</param>
    /// <param name="suppressed">Apresentação, tela cheia ou D3D exclusivo (§5).</param>
    /// <param name="sent">Chaves já emitidas, para a deduplicação por <c>(sinal, ocorrência)</c>.</param>
    public static ToastRequest? Decide(
        Signal? winner,
        DateTimeOffset now,
        bool suppressed,
        IReadOnlySet<string> sent,
        ToastOptions? options = null)
    {
        var limits = options ?? ToastOptions.Default;

        if (!limits.Enabled) return null;

        // Nível 3 e mais nada. SelfClearing junto porque interromper por algo que vai sumir em três
        // minutos é o pior negócio possível — hoje nenhum crítico se limpa sozinho, e esta cláusula
        // é o que garante que um crítico passageiro criado no futuro não vire toast por acidente.
        if (winner is not { Severity: Severity.Critical, SelfClearing: false }) return null;

        // Retido, não descartado (§5). Nada sai e nada é marcado como enviado; terminada a
        // apresentação, se o vermelho ainda estiver de pé — quer dizer, se ainda não foi
        // reconhecido — a interrupção acontece na volta. Descartar aqui perderia justamente o
        // aviso que mais importa, porque apresentação é quando é mais fácil estourar o horário.
        if (suppressed) return null;

        var entry = KeyFor(winner, ToastKind.Entry);
        var escalation = KeyFor(winner, ToastKind.Escalation);
        var after = TimeSpan.FromMinutes(Math.Max(1, limits.EscalationMinutes));

        if (winner.IsEscalatedAt(now, after) && !sent.Contains(escalation))
        {
            // Duas interrupções em sequência não são duas interrupções: são um susto. Quando a
            // entrada nunca chegou a sair — app aberto tarde, apresentação longa — ela é consumida
            // em silêncio e só a escalada, que é a informação mais atual, chega ao usuário.
            return Compose(winner, ToastKind.Escalation, now, [entry, escalation]);
        }

        return sent.Contains(entry) ? null : Compose(winner, ToastKind.Entry, now, [entry]);
    }

    /// <summary>
    /// Chave de deduplicação: <c>(ocorrência, tipo)</c>. A ocorrência já carrega o evento e seus
    /// horários (§7), então remarcar a reunião produz chave nova — e remarcar é, de fato, uma
    /// situação nova.
    /// </summary>
    public static string KeyFor(Signal signal, ToastKind kind) =>
        $"{signal.Occurrence}|{(kind == ToastKind.Entry ? "entry" : "escalated")}";

    /// <summary>
    /// Etiqueta curta e estável para a Central de Ações, que limita a 64 caracteres e não aceita a
    /// ocorrência crua. FNV-1a e não <c>string.GetHashCode</c>: o hash de string do .NET é
    /// aleatorizado por processo, e a etiqueta precisa sobreviver a um restart para não duplicar
    /// o mesmo aviso na Central.
    /// </summary>
    public static string TagFor(string occurrence)
    {
        ulong hash = 14695981039346656037;

        foreach (var c in occurrence)
        {
            hash ^= c;
            hash *= 1099511628211;
        }

        return hash.ToString("x16");
    }

    private static ToastRequest Compose(
        Signal signal, ToastKind kind, DateTimeOffset now, IReadOnlyList<string> consumes)
    {
        var title = signal.Name switch
        {
            SignalNames.MeetingRanIntoNext => "A reunião passou do horário",

            // Não repete "jornada encerrada", que já vem no motivo: o título nomeia a consequência,
            // o corpo dá o número.
            SignalNames.DayEnded => "O dia não fechou",
            _ => "Precisa de você",
        };

        if (kind == ToastKind.Escalation)
        {
            var minutes = Math.Max(1, (int)Math.Floor((now - signal.Since).TotalMinutes));
            title = $"{title} — há {minutes} min";
        }

        return new ToastRequest
        {
            Title = title,
            Body = $"{signal.Reason}. Clique na barra para dispensar.",
            Kind = kind,
            Tag = TagFor(signal.Occurrence),
            Consumes = consumes,
        };
    }
}
