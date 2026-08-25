namespace Tempus.Domain;

/// <summary>Um intervalo livre dentro do expediente.</summary>
internal readonly record struct FreeWindow(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Duration => End - Start;

    /// <summary><c>09:00–11:30</c>. O traço é meia-risca, não hífen: é intervalo, não separador.</summary>
    public string Label => $"{Start.ToLocalTime():HH:mm}–{End.ToLocalTime():HH:mm}";
}

/// <summary>O que responder sobre um dia sem abrir o Google Agenda.</summary>
internal sealed record DaySummary
{
    public required DateOnly Day { get; init; }

    /// <summary>Compromissos que ocupam horário. Dia inteiro não entra (§2.1).</summary>
    public required int Meetings { get; init; }

    public required IReadOnlyList<FreeWindow> Free { get; init; }

    /// <summary>Seg–sex menos feriados (D-008). Falso zera as janelas livres, de propósito.</summary>
    public required bool IsWorkingDay { get; init; }

    /// <summary>
    /// Por que o dia não tem janelas, quando não tem. <c>"Fim de semana"</c>, <c>"Feriado"</c>,
    /// <c>"Agenda cheia"</c> ou <c>"Expediente encerrado"</c> — a lista precisa dizer a diferença,
    /// senão quatro situações distintas viram a mesma linha vazia.
    /// </summary>
    public string? Note { get; init; }

    public bool HasFree => Free.Count > 0;
}

/// <summary>
/// "Você tem horário livre quinta?" — a pergunta que vem de outra pessoa e que obrigava a abrir o
/// Google Agenda (D-043).
/// <para>
/// Função pura de (agenda, dia, expediente, agora) → resumo (regra 8). O painel só desenha o que
/// sai daqui.
/// </para>
/// </summary>
internal static class DayAvailability
{
    /// <summary>
    /// Abaixo disto não é horário disponível, é vão. Listar dez minutos entre duas calls como
    /// "livre" transformaria a resposta em ruído — e ninguém marca reunião ali.
    /// </summary>
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromMinutes(30);

    public static IReadOnlyList<DaySummary> Summarize(
        IReadOnlyList<AgendaItem> agenda,
        DateOnly from,
        int days,
        WorkDayOptions work,
        DateTimeOffset now)
    {
        var result = new List<DaySummary>(days);

        for (var i = 0; i < days; i++)
            result.Add(Describe(agenda, from.AddDays(i), work, now));

        return result;
    }

    public static DaySummary Describe(
        IReadOnlyList<AgendaItem> agenda, DateOnly day, WorkDayOptions work, DateTimeOffset now)
    {
        // Dia inteiro é marcador, não compromisso (§2.1): não conta e não ocupa horário.
        var events = agenda
            .Where(e => !e.IsAllDay && DateOnly.FromDateTime(e.Start.ToLocalTime().Date) == day)
            .OrderBy(e => e.Start)
            .ToList();

        if (!WorkDayResolver.IsWorkingDay(day, work))
        {
            return new DaySummary
            {
                Day = day,
                Meetings = events.Count,
                Free = [],
                IsWorkingDay = false,

                // Sem janelas, e a nota diz por quê. "Livre o dia todo" num sábado seria uma
                // resposta errada com cara de certa — e é justamente a que faria o usuário
                // prometer um horário que ele não quer dar.
                //
                // Pelo dia da semana, e não por BrazilianHolidays: assim os feriados extras do
                // appsettings (emendas da TOTVS, D-008) também caem em "Feriado" em vez de ficarem
                // sem explicação.
                Note = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                    ? "Fim de semana"
                    : "Feriado",
            };
        }

        // Dois blocos, porque o almoço parte o expediente em dois. Tratá-lo como "ocupado" daria o
        // mesmo resultado, mas dizer que o almoço é uma reunião seria mentira no dia em que
        // alguém contasse os compromissos.
        var manha = Window(day, work.StartHour, work.StartMinute, work.MiddayHour, work.MiddayMinute, now);
        var tarde = Window(day, work.LunchEndHour, work.LunchEndMinute, work.EndHour, work.EndMinute, now);

        var busy = Merge(events);

        var free = new List<FreeWindow>();
        free.AddRange(Subtract(manha, busy));
        free.AddRange(Subtract(tarde, busy));

        return new DaySummary
        {
            Day = day,
            Meetings = events.Count,
            Free = free,
            IsWorkingDay = true,
            Note = free.Count > 0 ? null
                : manha is null && tarde is null ? "Expediente encerrado"
                : "Agenda cheia",
        };
    }

    /// <summary>
    /// Um pedaço do expediente, já recortado pelo presente.
    /// <para>
    /// Hoje começa em <b>agora</b>, e não às 08:00: hora que passou não é oferecível, e um resumo
    /// que às 16h diga "livre 09:00–12:00" está respondendo sobre um dia que não existe mais.
    /// </para>
    /// </summary>
    private static FreeWindow? Window(
        DateOnly day, int fromHour, int fromMinute, int toHour, int toMinute, DateTimeOffset now)
    {
        var start = At(day, fromHour, fromMinute, now.Offset);
        var end = At(day, toHour, toMinute, now.Offset);

        if (start < now) start = now;
        if (end <= start) return null;

        return new FreeWindow(start, end);
    }

    /// <summary>
    /// Funde compromissos que se sobrepõem ou se encostam. Sem isto, duas calls simultâneas
    /// abririam entre si um buraco que não existe — e sobreposição é rotina nesta agenda (§8).
    /// </summary>
    private static List<FreeWindow> Merge(IReadOnlyList<AgendaItem> events)
    {
        var merged = new List<FreeWindow>();

        foreach (var e in events.OrderBy(e => e.Start))
        {
            if (merged.Count > 0 && e.Start <= merged[^1].End)
            {
                var last = merged[^1];
                if (e.End > last.End) merged[^1] = last with { End = e.End };
                continue;
            }

            merged.Add(new FreeWindow(e.Start, e.End));
        }

        return merged;
    }

    /// <summary>O que sobra de <paramref name="window"/> depois de tirar os blocos ocupados.</summary>
    private static IEnumerable<FreeWindow> Subtract(FreeWindow? window, List<FreeWindow> busy)
    {
        if (window is not { } livre) yield break;

        var cursor = livre.Start;

        foreach (var block in busy)
        {
            if (block.End <= cursor) continue;
            if (block.Start >= livre.End) break;

            if (block.Start > cursor && block.Start - cursor >= MinimumWindow)
                yield return new FreeWindow(cursor, block.Start);

            if (block.End > cursor) cursor = block.End;
        }

        if (livre.End > cursor && livre.End - cursor >= MinimumWindow)
            yield return new FreeWindow(cursor, livre.End);
    }

    private static DateTimeOffset At(DateOnly day, int hour, int minute, TimeSpan offset) =>
        new(day.Year, day.Month, day.Day, hour, minute, 0, offset);
}
