namespace Tempus.Domain;

/// <summary>Qual interrupção esta é (<c>SEVERITY.md</c> §5).</summary>
internal enum ToastKind
{
    /// <summary>Ao entrar em vermelho.</summary>
    Entry,

    /// <summary>Ao escalar para piscante, sem reconhecimento, alguns minutos depois.</summary>
    Escalation,

    /// <summary>Reunião chegando: o aviso passageiro, com folga para você se organizar.</summary>
    MeetingHeadsUp,

    /// <summary>Reunião em cima da hora: fica na tela até o clique (D-039).</summary>
    MeetingStanding,
}

/// <summary>
/// O que um botão do toast faz. A diferença entre eles não é cosmética — é <b>de quem depende</b>:
/// <see cref="Join"/> e <see cref="Dismiss"/> são resolvidos inteiramente pelo Windows, e
/// <see cref="Acknowledge"/> precisa voltar ao processo. Ver D-039.
/// </summary>
internal enum ToastActionKind
{
    /// <summary>Abre a URL da call pelo shell. Não depende de nada do Tempus estar funcionando.</summary>
    Join,

    /// <summary>"Eu vi": volta ao processo e reconhece a ocorrência, igual ao clique na barra.</summary>
    Acknowledge,

    /// <summary>Fecha o aviso. Inteiramente tratado pelo Windows.</summary>
    Dismiss,
}

internal sealed record ToastAction(string Label, ToastActionKind Kind, string Argument = "");

/// <summary>Um toast pronto para sair — texto já resolvido, sem nada a decidir na camada de UI.</summary>
internal sealed record ToastRequest
{
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required ToastKind Kind { get; init; }

    /// <summary>
    /// Identidade na Central de Ações. Igual para as duas interrupções do mesmo assunto — entrada e
    /// escalada de um alarme, aviso e fixo de uma reunião — porque as duas falam da <b>mesma</b>
    /// situação. Assim a segunda substitui a primeira em vez de empilhar dois avisos.
    /// </summary>
    public required string Tag { get; init; }

    /// <summary>
    /// As chaves que passam a contar como enviadas depois desta emissão. Normalmente uma; duas
    /// quando a interrupção mais recente engole a anterior, que nunca chegou a sair.
    /// </summary>
    public required IReadOnlyList<string> Consumes { get; init; }

    /// <summary>
    /// Os botões, em ordem de importância. <b>Nunca vazio quando <see cref="StaysOnScreen"/></b> —
    /// ver a documentação da propriedade.
    /// </summary>
    public required IReadOnlyList<ToastAction> Actions { get; init; }

    /// <summary>
    /// Fica na tela até o usuário agir, em vez de sumir sozinho em alguns segundos
    /// (<c>scenario="reminder"</c>).
    /// <para>
    /// <b>Um toast fixo sem botão não é fixo.</b> O Windows exige pelo menos uma ação para honrar o
    /// cenário: sem ela ele degrada em silêncio para um toast comum, que é o modo de falhar mais
    /// caro daqui — pareceria que a decisão foi tomada e ela teria sido ignorada. A invariante está
    /// trancada em <c>ToastPolicyTests</c>.
    /// </para>
    /// </summary>
    public bool StaysOnScreen { get; init; }

    /// <summary>
    /// Quando o aviso deixa de fazer sentido na Central de Ações; <c>null</c> usa o padrão do canal.
    /// Serve ao aviso de reunião, que vira mentira no instante em que ela começa (regra 10).
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// A ocorrência de que este toast fala. É o que o "eu vi" reconhece e o que a retirada
    /// automática compara para saber se o assunto ainda está de pé.
    /// </summary>
    public required string Occurrence { get; init; }
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

    /// <summary>
    /// Desliga só os avisos de reunião, preservando o nível 3. Existe porque são coisas de peso
    /// diferente: desistir do aviso de call é preferência, desistir do vermelho é abrir mão da
    /// regra 2.
    /// </summary>
    public bool Meetings { get; init; } = true;

    /// <summary>Aviso passageiro: quantos minutos antes da reunião ele sai.</summary>
    public int MeetingHeadsUpMinutes { get; init; } = 10;

    /// <summary>Aviso fixo, que fica na tela até o clique. Sempre menor que o passageiro.</summary>
    public int MeetingStandingMinutes { get; init; } = 2;

    public static readonly ToastOptions Default = new();
}

/// <summary>
/// Quando interromper, e com que texto (<c>SEVERITY.md</c> §5).
/// <para>
/// Dois caminhos, com prioridades diferentes. O <b>nível 3</b> lê o sinal vencedor: é um alarme, e
/// alarme é o que a arbitragem escolheu mostrar. A <b>reunião</b> lê o humor temporal, e não o
/// vencedor — <c>MeetingUpcoming</c> é nível 1 e qualquer tarefa vencida o derrota na arbitragem,
/// o que faria o aviso da call sumir por causa de algo que não tem nada com ela. O humor não sofre
/// disso: o slot de tempo mostra a próxima reunião independentemente do que mais esteja alarmando.
/// </para>
/// <para>
/// A propriedade que isso preserva é a mesma que o D-028 protegia — o toast nunca fala de algo que
/// a barra não está mostrando —, só que agora por outra superfície da barra.
/// </para>
/// <para>
/// Função pura como todo o resto do domínio (regra 8): a supressão de apresentação chega como
/// booleano e o conjunto do que já saiu chega como parâmetro. Quem fala com o Windows é o
/// <c>ToastChannel</c>.
/// </para>
/// </summary>
internal static class ToastPolicy
{
    /// <summary>
    /// Identidade do assunto "avisar sobre esta reunião", compartilhada pelas duas interrupções
    /// dela. Reusa o formato de ocorrência do §7, então remarcar a reunião produz assunto novo.
    /// <para>
    /// É pública porque quem <b>retira</b> o aviso precisa da mesma chave que quem o emitiu — e a
    /// alternativa, cada lado montando a string por conta, quebraria em silêncio no dia em que o
    /// formato mudasse.
    /// </para>
    /// </summary>
    public static string SubjectOf(AgendaItem meeting) =>
        Signal.OccurrenceFor("MeetingToast", meeting);

    /// <param name="winner">O sinal que a barra está exibindo — já passado pela arbitragem e pela
    /// histerese. Usar o vencedor exibido, e não a lista bruta, garante que o alarme nunca fale de
    /// algo que a barra não está mostrando.</param>
    /// <param name="time">O humor temporal, que é a outra coisa que a barra mostra (§0.5).</param>
    /// <param name="meeting">O evento de que o humor fala, quando há um. É dele que sai o horário
    /// exato e o link — o humor sozinho não basta.</param>
    /// <param name="suppressed">Apresentação, tela cheia ou D3D exclusivo (§5).</param>
    /// <param name="sent">Chaves já emitidas, para a deduplicação por assunto.</param>
    public static ToastRequest? Decide(
        Signal? winner,
        TimeStatus time,
        AgendaItem? meeting,
        DateTimeOffset now,
        bool suppressed,
        IReadOnlySet<string> sent,
        ToastOptions? options = null)
    {
        var limits = options ?? ToastOptions.Default;

        if (!limits.Enabled) return null;

        // Retido, não descartado (§5). Nada sai e nada é marcado como enviado; terminada a
        // apresentação, se a situação ainda estiver de pé, a interrupção acontece na volta.
        // Descartar aqui perderia justamente o aviso que mais importa, porque apresentação é
        // quando é mais fácil estourar o horário — e também quando é mais fácil perder a hora.
        if (suppressed) return null;

        // Ordem deliberada: quando as duas coisas cabem no mesmo instante, quem interrompe é a
        // pior. Estar invadindo a próxima call é notícia mais urgente que a seguinte estar por vir.
        return Critical(winner, time, now, sent, limits)
            ?? Meeting(time, meeting, now, sent, limits);
    }

    // ---------------------------------------------------------------- nível 3

    private static ToastRequest? Critical(
        Signal? winner,
        TimeStatus time,
        DateTimeOffset now,
        IReadOnlySet<string> sent,
        ToastOptions limits)
    {
        // Nível 3 e mais nada. SelfClearing junto porque interromper por algo que vai sumir em três
        // minutos é o pior negócio possível — hoje nenhum crítico se limpa sozinho, e esta cláusula
        // é o que garante que um crítico passageiro criado no futuro não vire toast por acidente.
        if (winner is not { Severity: Severity.Critical, SelfClearing: false }) return null;

        var entry = KeyFor(winner.Occurrence, ToastKind.Entry);
        var escalation = KeyFor(winner.Occurrence, ToastKind.Escalation);
        var after = TimeSpan.FromMinutes(Math.Max(1, limits.EscalationMinutes));

        if (winner.IsEscalatedAt(now, after) && !sent.Contains(escalation))
        {
            // Duas interrupções em sequência não são duas interrupções: são um susto. Quando a
            // entrada nunca chegou a sair — app aberto tarde, apresentação longa — ela é consumida
            // em silêncio e só a escalada, que é a informação mais atual, chega ao usuário.
            return ComposeCritical(winner, time, ToastKind.Escalation, now, [entry, escalation]);
        }

        return sent.Contains(entry)
            ? null
            : ComposeCritical(winner, time, ToastKind.Entry, now, [entry]);
    }

    private static ToastRequest ComposeCritical(
        Signal signal,
        TimeStatus time,
        ToastKind kind,
        DateTimeOffset now,
        IReadOnlyList<string> consumes)
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

        // "Eu vi" primeiro: é o gesto que a regra 2 exige que exista sempre, e o único que resolve
        // o alarme. Entrar na call vem depois porque é opcional — e só aparece quando o clique na
        // barra faria a mesma coisa, para as duas superfícies nunca discordarem sobre o destino.
        List<ToastAction> actions = [new("Eu vi", ToastActionKind.Acknowledge, signal.Occurrence)];

        if (time.CallUrl is { Length: > 0 } url)
            actions.Add(new ToastAction("Entrar na call", ToastActionKind.Join, url));

        return new ToastRequest
        {
            Title = title,
            Body = signal.Reason,
            Kind = kind,
            Tag = TagFor(signal.Occurrence),
            Occurrence = signal.Occurrence,
            Consumes = consumes,
            Actions = actions,

            // Fica na tela até o clique. Não é conforto: a regra 2 diz que o nível 3 escala e não
            // decai, e um vermelho que interrompe por cinco segundos e some decai.
            StaysOnScreen = true,
        };
    }

    // ---------------------------------------------------------------- reunião (D-039)

    private static ToastRequest? Meeting(
        TimeStatus time,
        AgendaItem? meeting,
        DateTimeOffset now,
        IReadOnlySet<string> sent,
        ToastOptions limits)
    {
        if (!limits.Meetings || meeting is null) return null;

        // Só os humores que falam de reunião que ainda não começou. InMeeting e Overrun são
        // reuniões em curso: lá o aviso já saiu, e se ninguém tocou nele ele continua na tela.
        if (time.Mood is not (TimeMood.Approaching or TimeMood.Imminent)) return null;

        var until = meeting.Start - now;
        if (until <= TimeSpan.Zero) return null;

        var subject = SubjectOf(meeting);
        var headsUp = KeyFor(subject, ToastKind.MeetingHeadsUp);
        var standing = KeyFor(subject, ToastKind.MeetingStanding);

        // O fixo engole o passageiro pelo mesmo motivo que a escalada engole a entrada: abrir o app
        // a um minuto da reunião deve produzir um aviso, não dois em sequência.
        if (until <= TimeSpan.FromMinutes(Math.Max(0, limits.MeetingStandingMinutes)))
        {
            return sent.Contains(standing)
                ? null
                : ComposeMeeting(meeting, ToastKind.MeetingStanding, until, [headsUp, standing]);
        }

        if (until > TimeSpan.FromMinutes(Math.Max(0, limits.MeetingHeadsUpMinutes))) return null;

        return sent.Contains(headsUp)
            ? null
            : ComposeMeeting(meeting, ToastKind.MeetingHeadsUp, until, [headsUp]);
    }

    private static ToastRequest ComposeMeeting(
        AgendaItem meeting, ToastKind kind, TimeSpan until, IReadOnlyList<string> consumes)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(until.TotalMinutes));
        var fixo = kind == ToastKind.MeetingStanding;
        var subject = SubjectOf(meeting);

        List<ToastAction> actions = [];

        // O link vem do evento, e não de TimeStatus.CallUrl, que é null em Approaching de propósito
        // (D-016): clicar na barra escrita "Em breve" e cair numa sala vazia é um estrago
        // silencioso. Aqui não há essa ambiguidade — o toast nomeia a reunião e o botão nomeia a
        // ação, então a mesma URL que seria perigosa na barra é exatamente o que se quer no botão.
        if (meeting.Conference?.Url is { Length: > 0 } url)
            actions.Add(new ToastAction("Entrar na call", ToastActionKind.Join, url));

        // Sempre, e não só quando falta o link: é ele que sustenta o cenário fixo. Uma reunião
        // presencial ainda precisa de saída, e sem nenhum botão o Windows ignoraria o "fica na
        // tela" sem avisar ninguém.
        actions.Add(new ToastAction("Dispensar", ToastActionKind.Dismiss));

        return new ToastRequest
        {
            Title = meeting.Title,
            Body = $"Começa em {minutes} min, às {meeting.Start.ToLocalTime():HH:mm}.",
            Kind = kind,
            Tag = TagFor(subject),
            Occurrence = subject,
            Consumes = consumes,
            Actions = actions,
            StaysOnScreen = fixo,

            // O passageiro morre na Central quando a reunião começa: depois disso ele só informaria
            // um horário que já passou, que é a regra 10 aplicada à notificação. O fixo não expira
            // por tempo — ele sai por gesto, ou pela retirada automática quando a reunião acaba.
            ExpiresAt = fixo ? null : meeting.Start,
        };
    }

    // ---------------------------------------------------------------- identidade

    /// <summary>
    /// Chave de deduplicação: <c>(assunto, tipo)</c>. O assunto já carrega o evento e seus horários
    /// (§7), então remarcar a reunião produz chave nova — e remarcar é, de fato, uma situação nova.
    /// </summary>
    public static string KeyFor(string occurrence, ToastKind kind) => $"{occurrence}|{Slug(kind)}";

    private static string Slug(ToastKind kind) => kind switch
    {
        ToastKind.Entry => "entry",
        ToastKind.Escalation => "escalated",
        ToastKind.MeetingHeadsUp => "headsup",
        _ => "standing",
    };

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
}
