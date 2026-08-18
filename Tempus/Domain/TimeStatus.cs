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
    public static readonly TimeStatus Unknown = new() { Mood = TimeMood.Unknown, Text = "—" };

    public TimeMood Mood { get; init; } = TimeMood.Unknown;

    /// <summary>O que aparece no slot esquerdo da barra.</summary>
    public required string Text { get; init; }

    public string? Detail { get; init; }

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

    public static readonly TimeThresholds Default = new();
}

internal static class TimeStatusResolver
{
    /// <summary>
    /// Função pura de (agenda, agora) → o que mostrar e com que humor. Testável sem UI e sem rede
    /// (regra 8 do CLAUDE.md).
    /// </summary>
    public static TimeStatus Resolve(
        IReadOnlyList<AgendaItem> agenda,
        DateTimeOffset now,
        TimeThresholds thresholds,
        WorkDayOptions workDay)
    {
        var relevant = agenda.Where(e => !e.IsAllDay).OrderBy(e => e.Start).ToList();

        var current = relevant.FirstOrDefault(e => e.IsRunningAt(now));
        var next = relevant.FirstOrDefault(e => e.Start > now);

        if (current is not null) return InMeeting(current, next, now, thresholds);

        // Reunião que acabou de passar do horário marcado. Vem antes de "próxima" porque estourar
        // é o problema mais caro que a barra conhece.
        var overrunWindow = TimeSpan.FromMinutes(thresholds.OverrunMinutes);
        var justEnded = relevant
            .Where(e => e.End <= now && now - e.End <= overrunWindow)
            .OrderByDescending(e => e.End)
            .FirstOrDefault();

        if (justEnded is not null) return Overrun(justEnded, next, now);

        // Compromisso marcado ganha do relógio: uma reunião às 19h existe, e o expediente ter
        // acabado não a torna menos real. Fora-de-expediente só fala quando não há nada agendado.
        if (next is null && WorkDayResolver.OffHoursLabel(now, workDay) is { } label)
        {
            return new TimeStatus
            {
                Mood = TimeMood.OffHours,
                Text = label,
                Detail = "Fora do horário de trabalho",
            };
        }

        return Between(next, now, thresholds);
    }

    private static TimeStatus InMeeting(
        AgendaItem current, AgendaItem? next, DateTimeOffset now, TimeThresholds thresholds)
    {
        var remaining = current.End - now;

        // Vira âmbar perto do fim SEMPRE, inclusive sem nada depois. O custo de estourar não é
        // seu — é do tempo das outras pessoas, que ficou comprometido pela duração marcada. Ter a
        // tarde livre não devolve os 22 minutos a quem estava na reunião.
        var endingSoon = remaining <= TimeSpan.FromMinutes(thresholds.EndingSoonMinutes);

        // Estando numa reunião, o que importa é quando ela acaba — e, logo depois, o que vem.
        var text = $"{(endingSoon ? "Encerrando" : "Ocupado")} · {current.Title}, "
            + $"faltam {Humanize(remaining)}";
        if (next is not null) text += $" → {next.Title}";

        return new TimeStatus
        {
            Mood = endingSoon ? TimeMood.EndingSoon : TimeMood.InMeeting,
            Text = text,
            NamesAnEvent = true,
            CallUrl = current.Conference?.Url,
            Detail = next is null
                ? $"Termina às {current.End.ToLocalTime():HH:mm}"
                : $"Termina às {current.End.ToLocalTime():HH:mm} · {next.Title} às {next.Start.ToLocalTime():HH:mm}",
        };
    }

    private static TimeStatus Overrun(AgendaItem ended, AgendaItem? next, DateTimeOffset now)
    {
        var over = now - ended.End;
        var invading = next is not null && next.Start <= now;

        var text = $"Estourou · {ended.Title}, passou {Humanize(over)}";
        if (invading) text += $" · {next!.Title} já começou";

        return new TimeStatus
        {
            Mood = TimeMood.Overrun,
            Text = text,
            NamesAnEvent = true,
            // A que invadiu, quando existe: às 12:22 o que importa é entrar na que já começou,
            // não voltar para a que devia ter acabado.
            CallUrl = (invading ? next!.Conference ?? ended.Conference : ended.Conference)?.Url,
            Detail = $"Estava marcada até {ended.End.ToLocalTime():HH:mm}",
        };
    }

    private static TimeStatus Between(AgendaItem? next, DateTimeOffset now, TimeThresholds thresholds)
    {
        if (next is null)
        {
            return new TimeStatus
            {
                Mood = TimeMood.Free,
                Text = "Livre",
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

        // O rótulo de estado vem sempre primeiro, em todos os humores (D-015): é a resposta que se
        // lê de relance, e ela tem que estar sempre no mesmo lugar para o olho não precisar
        // interpretar a frase antes de saber se está livre ou ocupado.
        var label = mood switch
        {
            TimeMood.Imminent => "Começando",
            TimeMood.Approaching => "Em breve",
            _ => "Livre",
        };

        var text = $"{label} · {next.Title} em {Humanize(until)}";

        return new TimeStatus
        {
            Mood = mood,
            Text = text,
            NamesAnEvent = true,

            // Só a partir de "Começando" o clique entra na call. Antes disso ele abre a agenda:
            // clicar em "Livre" e cair dentro de uma reunião que só começa daqui a quatro horas é
            // um estrago silencioso — você entra numa sala vazia sem perceber que entrou.
            CallUrl = mood == TimeMood.Imminent ? next.Conference?.Url : null,

            Detail = $"{next.Title} às {next.Start.ToLocalTime():HH:mm}",
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
