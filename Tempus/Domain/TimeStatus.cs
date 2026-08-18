namespace Tempus.Domain;

/// <summary>
/// A situação temporal do usuário. É o sinal ambiente que fica visível o dia inteiro, e por isso
/// tem vocabulário próprio de cor, separado do alarme.
/// </summary>
internal enum TimeMood
{
    /// <summary>Sem sincronização — não sabemos nada. Precede tudo (<c>SEVERITY.md</c> §0).</summary>
    Unknown,

    /// <summary>Livre, e a próxima reunião está longe. Verde discreto.</summary>
    Free,

    /// <summary>
    /// Fora do expediente. Laranja, sem preenchimento.
    /// <para>
    /// Não é <see cref="Free"/>: verde diz "aproveite o tempo livre de trabalho", e às 19:48 de
    /// uma terça isso é a mensagem errada. Laranja é o empurrão de que o dia já acabou.
    /// </para>
    /// </summary>
    OffHours,

    /// <summary>Em reunião, dentro do horário. Azul: ocupado, nada errado.</summary>
    InMeeting,

    /// <summary>A próxima reunião se aproxima. Âmbar.</summary>
    Approaching,

    /// <summary>A reunião atual está para acabar. Âmbar preenchido.</summary>
    EndingSoon,

    /// <summary>A próxima reunião começa em minutos. Vermelho preenchido.</summary>
    Imminent,

    /// <summary>
    /// A reunião passou do horário marcado. Vermelho preenchido.
    /// <para>
    /// É o motivo de o Tempus existir: uma week review segurada até 12:22 quando o horário de
    /// todos terminava 12:00. O custo de estourar não é seu — é do tempo das outras pessoas, que
    /// foi comprometido pela duração marcada.
    /// </para>
    /// </summary>
    Overrun,
}

internal sealed record TimeStatus
{
    public static readonly TimeStatus Unknown = new() { Mood = TimeMood.Unknown, Label = "—" };

    public TimeMood Mood { get; init; } = TimeMood.Unknown;

    /// <summary>
    /// O rótulo do estado, sozinho: <c>Livre</c>, <c>Ocupado</c>, <c>Em breve</c>, <c>Encerrando</c>.
    /// <para>
    /// Ocupa um slot próprio com fundo próprio (D-023). É a resposta à pergunta que o produto
    /// existe para responder, e por isso não divide espaço com o nome de reunião nenhuma.
    /// </para>
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// O detalhe do cronograma que acompanha o rótulo — <c>às 14:00 · Treinamento do GWS</c>.
    /// <c>null</c> quando não há nada a dizer, e aí o slot cede lugar ao lookahead.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>Texto da dica. Traz o que não coube, nunca o que já está visível.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Identidade da ocorrência que o clique reconhece, ou <c>null</c> quando não há o que
    /// reconhecer (<c>SEVERITY.md</c> §7).
    /// <para>
    /// Ao <c>(eventId, início, fim)</c> do §7 soma-se o <b>sinal</b>. Sem ele, reconhecer
    /// "Encerrando" às 14:56 calaria o "Estourou" das 15:01 — e estourar é um fato novo e pior,
    /// exatamente o que o produto existe para acusar. Prorrogar a reunião muda o fim, muda a
    /// identidade, e o sinal volta a disparar, como o §7 já previa.
    /// </para>
    /// </summary>
    public string? Occurrence { get; init; }

    /// <summary>Rótulo e detalhe juntos, para quem precisa da frase inteira.</summary>
    public string Text => Summary is null ? Label : $"{Label} · {Summary}";

    /// <summary>
    /// O <see cref="Text"/> já nomeia um compromisso.
    /// <para>
    /// Existe para o lookahead saber calar a boca. Sem isto a barra mostrava dois compromissos lado
    /// a lado — o de hoje no slot de estado e o de amanhã no slot de lookahead — e a leitura de
    /// relance virava "tenho duas reuniões", que é a pergunta errada respondida errado.
    /// </para>
    /// </summary>
    public bool NamesAnEvent { get; init; }

    /// <summary>
    /// Link da call da reunião a que este estado se refere, quando ela tem uma. É o que faz o
    /// clique no slot entrar na call em vez de abrir a agenda (D-016).
    /// <para>
    /// Só a URL, sem o provedor: a barra não sinaliza serviço — essa distinção vive no painel S3
    /// (D-017).
    /// </para>
    /// </summary>
    public string? CallUrl { get; init; }

    /// <summary>
    /// Estados preenchidos são os que pedem antecipação; os demais ficam só com o texto tingido.
    /// A escalada acontece na <b>forma</b>, não só na cor — é o que impede o vermelho de contagem
    /// regressiva de se confundir com o vermelho de "você invadiu a próxima call" (D-012).
    /// </summary>
    public bool IsFilled => Mood is TimeMood.EndingSoon or TimeMood.Imminent or TimeMood.Overrun;

    /// <summary>
    /// Peso da fonte. Fora do expediente entra em negrito sem preenchimento: o recado é firme
    /// ("pare"), mas não é uma urgência que exija ação imediata.
    /// </summary>
    public bool IsBold => IsFilled || Mood is TimeMood.OffHours;

    /// <summary>
    /// O alarme já dura mais que o limiar de escalada e deve <b>piscar</b> âmbar↔vermelho
    /// (<c>SEVERITY.md</c> §1.1, regra 2: nível 3 escala, não decai).
    /// <para>
    /// Na prática só o <see cref="TimeMood.Overrun"/> alcança isto, e não por caso especial: entre
    /// os humores preenchidos, <c>Imminent</c> acaba quando a reunião começa e <c>EndingSoon</c>
    /// quando ela termina — nenhum dos dois sobrevive cinco minutos. O limiar seleciona sozinho.
    /// </para>
    /// <para>
    /// <b>Não guarda estado.</b> O alarme de estouro começa no fim marcado da reunião, então
    /// "há quanto tempo escala" sai de (agenda, agora) — sem timer e sem lembrar quando começou,
    /// o que mantém o resolvedor uma função pura (regra 8).
    /// </para>
    /// </summary>
    public bool IsEscalated { get; init; }
}

internal sealed record TimeThresholds
{
    /// <summary>A partir daqui a próxima reunião começa a chamar atenção.</summary>
    public int ApproachingMinutes { get; init; } = 15;

    /// <summary>A partir daqui é hora de encerrar o que está fazendo.</summary>
    public int ImminentMinutes { get; init; } = 5;

    /// <summary>Aviso de que a reunião atual está para acabar.</summary>
    public int EndingSoonMinutes { get; init; } = 5;

    /// <summary>
    /// Por quanto tempo, depois do fim marcado, a reunião continua acusando estouro. Sem detecção
    /// de presença (D-006), o app não sabe se você saiu — então avisa durante uma janela e para.
    /// </summary>
    public int OverrunMinutes { get; init; } = 10;

    /// <summary>
    /// Quanto tempo um alarme aguenta sem reconhecimento antes de passar a piscar (§1.1, I8).
    /// Nunca menor que isto — a invariante fixa o piso, a configuração só pode afrouxar.
    /// </summary>
    public int EscalationMinutes { get; init; } = 5;

    public static readonly TimeThresholds Default = new();
}

internal static class TimeStatusResolver
{
    /// <summary>
    /// Função pura de (agenda, agora) → o que mostrar e com que humor. Testável sem UI e sem rede
    /// (regra 8 do CLAUDE.md).
    /// </summary>
    /// <summary>Identidade da ocorrência: o <c>(eventId, início, fim)</c> do §7 mais o sinal.</summary>
    public static string OccurrenceOf(TimeMood mood, AgendaItem item) =>
        $"{mood}|{item.Id}|{item.Start:O}|{item.End:O}";

    public static TimeStatus Resolve(
        IReadOnlyList<AgendaItem> agenda,
        DateTimeOffset now,
        TimeThresholds thresholds,
        WorkDayOptions workDay,
        IReadOnlySet<string>? acknowledged = null)
    {
        var seen = acknowledged ?? new HashSet<string>();
        var relevant = agenda.Where(e => !e.IsAllDay).OrderBy(e => e.Start).ToList();

        var current = relevant.FirstOrDefault(e => e.IsRunningAt(now));
        var next = relevant.FirstOrDefault(e => e.Start > now);

        if (current is not null) return InMeeting(current, next, now, thresholds, seen);

        // Reunião que acabou de passar do horário marcado. Vem antes de "próxima" porque estourar
        // é o problema mais caro que a barra conhece.
        var overrunWindow = TimeSpan.FromMinutes(thresholds.OverrunMinutes);
        var justEnded = relevant
            .Where(e => e.End <= now && now - e.End <= overrunWindow)
            .OrderByDescending(e => e.End)
            .FirstOrDefault();

        // Reconhecido some da escala e o resolvedor segue adiante — "volta ao normal
        // imediatamente" (invariante I3). Não vira âmbar nem meio-termo: vira o estado calmo que
        // existiria se o estouro não estivesse lá.
        if (justEnded is not null && seen.Contains(OccurrenceOf(TimeMood.Overrun, justEnded)))
            justEnded = null;

        if (justEnded is not null) return Overrun(justEnded, next, now, thresholds);

        // Compromisso marcado ganha do relógio: uma reunião às 19h existe, e o expediente ter
        // acabado não a torna menos real. Fora-de-expediente só fala quando não há nada agendado.
        if (next is null && WorkDayResolver.OffHoursLabel(now, workDay) is { } label)
        {
            return new TimeStatus
            {
                Mood = TimeMood.OffHours,
                Label = label,
                Detail = "Fora do horário de trabalho",
            };
        }

        return Between(next, now, thresholds, seen);
    }

    private static TimeStatus InMeeting(
        AgendaItem current,
        AgendaItem? next,
        DateTimeOffset now,
        TimeThresholds thresholds,
        IReadOnlySet<string> seen)
    {
        var remaining = current.End - now;

        // Vira âmbar perto do fim SEMPRE, inclusive sem nada depois. O custo de estourar não é
        // seu — é do tempo das outras pessoas, que ficou comprometido pela duração marcada. Ter a
        // tarde livre não devolve os 22 minutos a quem estava na reunião.
        var endingSoon = remaining <= TimeSpan.FromMinutes(thresholds.EndingSoonMinutes)
            && !seen.Contains(OccurrenceOf(TimeMood.EndingSoon, current));

        // Numa reunião o que importa é quando ela acaba, então a contagem vem antes do nome dela.
        var summary = $"faltam {Humanize(remaining)} · {current.Title}";

        // O próximo vem com hora de relógio, e não com contagem: a frase já tem um "faltam X" para
        // a reunião atual, e dois números relativos na mesma linha obrigam a descobrir qual conta
        // para qual reunião.
        if (next is not null) summary += $" → {next.Start.ToLocalTime():HH:mm} {next.Title}";

        return new TimeStatus
        {
            Mood = endingSoon ? TimeMood.EndingSoon : TimeMood.InMeeting,
            Label = endingSoon ? "Encerrando" : "Ocupado",
            Summary = summary,
            NamesAnEvent = true,
            Occurrence = endingSoon ? OccurrenceOf(TimeMood.EndingSoon, current) : null,
            CallUrl = current.Conference?.Url,
            Detail = next is null
                ? $"Termina às {current.End.ToLocalTime():HH:mm}"
                : $"Termina às {current.End.ToLocalTime():HH:mm} · {next.Title} às {next.Start.ToLocalTime():HH:mm}",
        };
    }

    private static TimeStatus Overrun(
        AgendaItem ended, AgendaItem? next, DateTimeOffset now, TimeThresholds thresholds)
    {
        var over = now - ended.End;
        var invading = next is not null && next.Start <= now;

        // I8 fixa o piso em 5 min; a configuração só pode afrouxar, nunca antecipar o piscar.
        var escalation = TimeSpan.FromMinutes(Math.Max(5, thresholds.EscalationMinutes));

        var summary = $"passou {Humanize(over)} · {ended.Title}";
        if (invading) summary += $" · {next!.Title} já começou";

        return new TimeStatus
        {
            Mood = TimeMood.Overrun,
            Label = "Estourou",
            Summary = summary,
            NamesAnEvent = true,
            IsEscalated = over >= escalation,
            Occurrence = OccurrenceOf(TimeMood.Overrun, ended),
            // A que invadiu, quando existe: às 12:22 o que importa é entrar na que já começou,
            // não voltar para a que devia ter acabado.
            CallUrl = (invading ? next!.Conference ?? ended.Conference : ended.Conference)?.Url,
            Detail = $"Estava marcada até {ended.End.ToLocalTime():HH:mm}",
        };
    }

    private static TimeStatus Between(
        AgendaItem? next,
        DateTimeOffset now,
        TimeThresholds thresholds,
        IReadOnlySet<string> seen)
    {
        if (next is null)
        {
            return new TimeStatus
            {
                Mood = TimeMood.Free,
                Label = "Livre",
                Detail = "Nenhum compromisso restante hoje",
            };
        }

        var until = next.Start - now;
        var minutes = until.TotalMinutes;

        var mood = minutes switch
        {
            var m when m <= thresholds.ImminentMinutes => TimeMood.Imminent,
            var m when m <= thresholds.ApproachingMinutes => TimeMood.Approaching,
            _ => TimeMood.Free,
        };

        // Reconhecer "Começando" desce para "Em breve": o bloco apaga, mas a informação continua.
        // Reconhecer não apaga o fato, apaga o alarme.
        if (mood == TimeMood.Imminent && seen.Contains(OccurrenceOf(TimeMood.Imminent, next)))
            mood = TimeMood.Approaching;

        // O rótulo de estado vem sempre primeiro, em todos os humores (D-015): é a resposta que se
        // lê de relance, e ela tem que estar sempre no mesmo lugar para o olho não precisar
        // interpretar a frase antes de saber se está livre ou ocupado.
        var label = mood switch
        {
            TimeMood.Imminent => "Começando",
            TimeMood.Approaching => "Em breve",
            _ => "Livre",
        };

        // Perto usa contagem, longe usa relógio. "em 12 min" se age sem pensar; "em 3h30" obriga a
        // somar para descobrir que a reunião é às 14:00 e onde ela cai no dia. Passada uma hora, a
        // hora de relógio responde melhor a pergunta que o usuário está de fato fazendo.
        var clock = next.Start.ToLocalTime().ToString("HH:mm");
        var when = until < TimeSpan.FromHours(1) ? $"em {Humanize(until)}" : $"às {clock}";

        // Ordem deliberada: quando antes do título. O horário tem tamanho fixo e é o que o usuário
        // pediu para nunca perder; o título é longo e variável. Com o título na frente, era ele que
        // empurrava o horário para fora quando a barra ficava apertada — agora o corte come o fim
        // do título, que é a parte que menos custa.
        return new TimeStatus
        {
            Mood = mood,
            Label = label,
            Summary = $"{when} · {next.Title}",
            NamesAnEvent = true,
            Occurrence = mood == TimeMood.Imminent ? OccurrenceOf(TimeMood.Imminent, next) : null,

            // Só a partir de "Começando" o clique entra na call. Antes disso ele abre a agenda:
            // clicar em "Livre" e cair dentro de uma reunião que só começa daqui a quatro horas é
            // um estrago silencioso — você entra numa sala vazia sem perceber que entrou.
            CallUrl = mood == TimeMood.Imminent ? next.Conference?.Url : null,

            // A dica traz a outra metade: se a barra mostra o relógio, aqui vai a contagem, e
            // vice-versa. Repetir o mesmo número seria desperdiçar o único lugar com espaço.
            Detail = $"{next.Title} às {clock} · em {Humanize(until)}",
        };
    }

    /// <summary>
    /// Duração em linguagem de relance: minutos perto da hora, horas no meio do dia, dias quando
    /// o próximo compromisso é só amanhã.
    /// </summary>
    private static string Humanize(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        var totalMinutes = (int)Math.Ceiling(span.TotalMinutes);
        if (totalMinutes < 1) return "instantes";
        if (totalMinutes < 60) return $"{totalMinutes} min";

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;

        if (hours < 24) return minutes == 0 ? $"{hours}h" : $"{hours}h{minutes:00}";

        var days = hours / 24;
        var remainingHours = hours % 24;
        return remainingHours == 0 ? $"{days}d" : $"{days}d {remainingHours}h";
    }
}
