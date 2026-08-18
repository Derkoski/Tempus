namespace Tempus.Domain;

/// <summary>
/// Configuração de instalação das pausas. <b>Nasce desabilitada</b>: quem quiser a folga liga, e
/// quem nunca ouviu falar dela não é surpreendido por um bloco novo no painel.
/// </summary>
internal sealed record BreakOptions
{
    public bool Enabled { get; init; }

    public int DurationMinutes { get; init; } = 15;

    /// <summary>
    /// Granularidade da busca. Cinco minutos é fino o bastante para achar folga em agenda cheia e
    /// grosso o bastante para a pausa cair num horário redondo, que é como as pessoas leem hora.
    /// </summary>
    public int StepMinutes { get; init; } = 5;

    public static readonly BreakOptions Default = new();
}

internal enum BreakPeriod
{
    Morning,
    Afternoon,
}

/// <summary>Uma janela de descanso encontrada na agenda.</summary>
internal sealed record BreakSlot
{
    public required BreakPeriod Period { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }

    public string Label => Period == BreakPeriod.Morning ? "Pausa da manhã" : "Pausa da tarde";

    public bool IsRunningAt(DateTimeOffset now) => now >= Start && now < End;
    public bool HasEndedBy(DateTimeOffset now) => now >= End;
}

/// <summary>
/// Acha 15 minutos de descanso na manhã e na tarde, o mais perto possível do meio de cada período,
/// numa janela sem compromisso marcado.
/// <para>
/// Função pura de (agenda, expediente, hora) → janelas (regra 8). Nenhum I/O, nenhuma persistência:
/// quem sabe se a folga de hoje foi dispensada é a camada de cima.
/// </para>
/// <para>
/// <b>A pausa se move.</b> É recalculada a cada rodada de sync, então uma reunião marcada em cima
/// empurra o descanso para a próxima janela livre em vez de deixá-lo num horário que já não existe.
/// Foi o motivo de ela viver só no Tempus e não virar evento no Calendar: evento real ficaria
/// parado (ver ROADMAP, "Pausas de descanso").
/// </para>
/// </summary>
internal static class BreakPlanner
{
    /// <summary>
    /// As pausas ainda de pé hoje. Vazio se a funcionalidade está desligada, se não é dia útil, ou
    /// se nenhum dos dois períodos tem espaço.
    /// </summary>
    public static IReadOnlyList<BreakSlot> Plan(
        IReadOnlyList<AgendaItem> agenda,
        DateTimeOffset now,
        WorkDayOptions work,
        BreakOptions options)
    {
        if (!options.Enabled) return [];
        if (!WorkDayResolver.IsWorkingDay(DateOnly.FromDateTime(now.Date), work)) return [];

        var duration = TimeSpan.FromMinutes(Math.Max(1, options.DurationMinutes));
        var busy = BusyIntervals(agenda);
        var slots = new List<BreakSlot>(2);

        var morning = Find(
            BreakPeriod.Morning,
            At(now, work.StartHour, work.StartMinute),
            At(now, work.MiddayHour, work.MiddayMinute),
            busy, duration, options.StepMinutes);
        if (morning is not null) slots.Add(morning);

        var afternoon = Find(
            BreakPeriod.Afternoon,
            At(now, work.LunchEndHour, work.LunchEndMinute),
            At(now, work.EndHour, work.EndMinute),
            busy, duration, options.StepMinutes);
        if (afternoon is not null) slots.Add(afternoon);

        return slots;
    }

    /// <summary>
    /// A janela livre mais próxima do meio do período. <b>Não olha o relógio.</b>
    /// <para>
    /// A primeira versão descartava candidatas já terminadas, e o efeito só apareceu em uso: quando
    /// a pausa ideal passava sem ser tirada, o planejador escolhia a próxima janela livre, e depois
    /// a próxima. A folga perseguia o usuário o dia inteiro — <i>"sempre tô com pausa pra fazer"</i>
    /// — e nunca terminava de acontecer.
    /// </para>
    /// <para>
    /// Agora a pausa é do <b>período</b>, não do instante: fica no mesmo horário o dia todo, e só
    /// muda se marcarem reunião em cima. Perdeu, perdeu — não vira cobrança. Quem decide se ela
    /// ainda é relevante é a UI, comparando com a hora na hora de desenhar.
    /// </para>
    /// </summary>
    private static BreakSlot? Find(
        BreakPeriod period,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> busy,
        TimeSpan duration,
        int stepMinutes)
    {
        var latestStart = periodEnd - duration;
        if (latestStart < periodStart) return null; // período menor que a própria pausa

        // O início ideal centraliza a pausa no meio do período, não a começa nele.
        var ideal = periodStart + ((periodEnd - periodStart) - duration) / 2;
        var step = TimeSpan.FromMinutes(Math.Max(1, stepMinutes));

        BreakSlot? best = null;
        var bestDistance = TimeSpan.MaxValue;

        for (var start = periodStart; start <= latestStart; start += step)
        {
            var end = start + duration;
            if (Overlaps(busy, start, end)) continue;

            var distance = Abs(start - ideal);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = new BreakSlot { Period = period, Start = start, End = end };
        }

        return best;
    }

    /// <summary>
    /// Eventos de dia inteiro ficam de fora: bloqueiam as 8 horas e não impedem ninguém de
    /// levantar da cadeira. Tratá-los como ocupado apagaria as duas pausas de qualquer dia com
    /// uma férias ou um aniversário no calendário.
    /// </summary>
    private static List<(DateTimeOffset Start, DateTimeOffset End)> BusyIntervals(
        IReadOnlyList<AgendaItem> agenda)
    {
        var busy = new List<(DateTimeOffset, DateTimeOffset)>(agenda.Count);

        foreach (var item in agenda)
            if (!item.IsAllDay && item.End > item.Start)
                busy.Add((item.Start, item.End));

        return busy;
    }

    private static bool Overlaps(
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> busy,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        foreach (var (busyStart, busyEnd) in busy)
            if (start < busyEnd && end > busyStart) return true;

        return false;
    }

    private static TimeSpan Abs(TimeSpan span) => span < TimeSpan.Zero ? -span : span;

    private static DateTimeOffset At(DateTimeOffset now, int hour, int minute) =>
        new(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);
}
