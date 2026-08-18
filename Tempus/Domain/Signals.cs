namespace Tempus.Domain;

/// <summary>
/// O que conta como "tarefa aberta" no sinal de fim de jornada (D-007).
/// <para>
/// Existe como opção porque o D-007 previu o arrependimento: se o vermelho das 17:00 incomodar
/// depois de uma semana de convívio, mudar de <c>AllOpen</c> para <c>DueTodayOrOverdue</c> é uma
/// linha de configuração, e não uma discussão sobre o modelo.
/// </para>
/// </summary>
internal enum DayEndedCountMode
{
    /// <summary>Todas as não concluídas, com ou sem vencimento. Padrão do D-007.</summary>
    AllOpen,

    /// <summary>Só as que venciam hoje ou antes — backlog distante não segura o fim do dia.</summary>
    DueTodayOrOverdue,
}

internal sealed record DayEndedOptions
{
    public DayEndedCountMode CountMode { get; init; } = DayEndedCountMode.AllOpen;

    public static readonly DayEndedOptions Default = new();
}

/// <summary>Limiares do ciclo de vida de call (<c>SEVERITY.md</c> §2.1), em minutos.</summary>
internal sealed record SignalThresholds
{
    public int UpcomingMinutes { get; init; } = 10;
    public int ImminentMinutes { get; init; } = 2;

    /// <summary>Por quanto tempo <c>MeetingEnded</c> fica no ar antes de sumir sozinho.</summary>
    public int EndedWindowMinutes { get; init; } = 3;

    /// <summary>Intervalo abaixo do qual duas reuniões contam como coladas.</summary>
    public int BackToBackMinutes { get; init; } = 5;

    /// <summary>Até quando o checkpoint de meio-dia insiste sem reconhecimento.</summary>
    public int MiddayWindowMinutes { get; init; } = 60;

    /// <summary>Janela do meio-dia quando não há nada aberto — informa e passa.</summary>
    public int MiddayCleanMinutes { get; init; } = 15;

    public static readonly SignalThresholds Default = new();
}

/// <summary>
/// Os sinais do <c>SEVERITY.md</c> §2, como funções puras de (dados, hora, supressões).
/// <para>
/// Nenhum I/O, nenhuma UI, nenhum estado (regra 8). O que sai daqui é uma lista de candidatos; a
/// escolha do vencedor é da <see cref="Arbiter"/>, e a suavização do que o usuário vê é do
/// <see cref="SeverityGate"/>. Três responsabilidades, três lugares.
/// </para>
/// </summary>
internal static class Signals
{
    public static IReadOnlyList<Signal> Evaluate(
        IReadOnlyList<AgendaItem> agenda,
        IReadOnlyList<TaskItem> tasks,
        DateTimeOffset now,
        WorkDayOptions work,
        SignalThresholds limits,
        IReadOnlySet<string> acknowledged,
        DayEndedOptions? dayEnded = null,
        string? activeEventId = null)
    {
        var found = new List<Signal>();

        found.AddRange(CallLifecycle(agenda, now, limits, activeEventId));
        found.AddRange(DayBoundaries(tasks, now, work, limits, dayEnded ?? DayEndedOptions.Default));
        found.AddRange(Tasks(tasks, now));

        // Reconhecido some da lista antes de qualquer disputa: sinal suprimido não arbitra (§4).
        return [.. found.Where(s => !acknowledged.Contains(s.Occurrence))];
    }

    // ---------------------------------------------------------------- §2.1 ciclo de call

    /// <summary>
    /// O ciclo de vida de uma call, só pelo cronograma. <b>Nada de detecção de presença</b>
    /// (D-006): o Tempus conhece o calendário e o usuário comunica o resto clicando.
    /// </summary>
    private static IEnumerable<Signal> CallLifecycle(
        IReadOnlyList<AgendaItem> agenda,
        DateTimeOffset now,
        SignalThresholds limits,
        string? activeEventId)
    {
        // Dia inteiro é marcador, não call (§2.1). Recusados e "livre" já não chegam aqui.
        var events = agenda.Where(e => !e.IsAllDay).OrderBy(e => e.Start).ToList();

        // §8: com duas reuniões sobrepostas o app não tem como saber em qual você está, e chutar
        // produz o pior ruído possível — um MeetingRanIntoNext falso no fim da primeira.
        var candidates = ActiveEvent.Candidates(events, now);
        var active = ActiveEvent.Choose(candidates, activeEventId);

        if (candidates.Count > 1 && activeEventId is null)
        {
            yield return new Signal
            {
                Name = SignalNames.MeetingAmbiguous,
                Severity = Severity.Info,
                Reason = $"{candidates.Count} reuniões agora — qual?",
                Category = SignalCategory.Call,
                Occurrence = Signal.OccurrenceFor(SignalNames.MeetingAmbiguous, active!),
                Since = candidates.Max(c => c.Start),
            };
        }

        // Sobrepostas que não são a ativa saem do ciclo até terminarem. É esta linha que faz a
        // cláusula do §8 valer: uma reunião aceita em paralelo nunca conta como "próxima invadida".
        if (active is not null)
            events = [.. events.Where(e => e.Id == active.Id || !ActiveEvent.Overlaps(e, active))];

        var upcoming = TimeSpan.FromMinutes(limits.UpcomingMinutes);
        var imminent = TimeSpan.FromMinutes(limits.ImminentMinutes);
        var endedWindow = TimeSpan.FromMinutes(limits.EndedWindowMinutes);
        var backToBack = TimeSpan.FromMinutes(limits.BackToBackMinutes);

        foreach (var e in events)
        {
            var toStart = e.Start - now;
            var sinceEnd = now - e.End;

            if (toStart > TimeSpan.Zero && toStart <= upcoming)
            {
                var name = toStart <= imminent ? SignalNames.MeetingImminent : SignalNames.MeetingUpcoming;

                yield return new Signal
                {
                    Name = name,
                    Severity = toStart <= imminent ? Severity.Attention : Severity.Info,
                    Reason = toStart <= imminent
                        ? $"{e.Title} começa em {Minutes(toStart)}"
                        : $"{e.Title} em {Minutes(toStart)}",
                    Category = SignalCategory.Call,
                    Occurrence = Signal.OccurrenceFor(name, e),
                    Since = e.Start - upcoming,
                };
            }

            if (e.IsRunningAt(now))
            {
                yield return new Signal
                {
                    Name = SignalNames.MeetingStarted,
                    Severity = Severity.Attention,
                    Reason = $"{e.Title} começou às {e.Start.ToLocalTime():HH:mm}",
                    Category = SignalCategory.Call,
                    Occurrence = Signal.OccurrenceFor(SignalNames.MeetingStarted, e),
                    Since = e.Start,
                };

                var seguinte = events.FirstOrDefault(o => o.Start >= e.End);

                if (seguinte is not null && seguinte.Start - e.End <= backToBack)
                {
                    yield return new Signal
                    {
                        Name = SignalNames.MeetingBackToBack,
                        Severity = Severity.Info,
                        Reason = $"Sem intervalo: {seguinte.Title} às {seguinte.Start.ToLocalTime():HH:mm}",
                        Category = SignalCategory.Call,
                        Occurrence = Signal.OccurrenceFor(SignalNames.MeetingBackToBack, e),
                        Since = e.Start,
                    };
                }
            }

            if (sinceEnd <= TimeSpan.Zero) continue;

            var invasora = events.FirstOrDefault(o => o.Id != e.Id && o.IsRunningAt(now));

            if (invasora is not null)
            {
                // O vermelho principal do produto: você segurou uma call por cima de outra. Não
                // auto-limpa — escala e só sai com clique (§2.1, regra 2).
                yield return new Signal
                {
                    Name = SignalNames.MeetingRanIntoNext,
                    Severity = Severity.Critical,
                    Reason = $"{e.Title} acabou — {invasora.Title} já começou",
                    Category = SignalCategory.Call,
                    Occurrence = Signal.OccurrenceFor(SignalNames.MeetingRanIntoNext, e),
                    Since = e.End,
                };
            }
            else if (sinceEnd <= endedWindow)
            {
                // Reunião isolada que terminou: âmbar breve que passa sozinho. Cobrar clique aqui
                // encareceria o caso comum sem ganhar nada.
                yield return new Signal
                {
                    Name = SignalNames.MeetingEnded,
                    Severity = Severity.Attention,
                    Reason = $"{e.Title} acabou às {e.End.ToLocalTime():HH:mm}",
                    Category = SignalCategory.Call,
                    Occurrence = Signal.OccurrenceFor(SignalNames.MeetingEnded, e),
                    SelfClearing = true,
                    Since = e.End,
                };
            }
        }
    }

    // ---------------------------------------------------------------- §2.2 fronteiras do dia

    private static IEnumerable<Signal> DayBoundaries(
        IReadOnlyList<TaskItem> tasks,
        DateTimeOffset now,
        WorkDayOptions work,
        SignalThresholds limits,
        DayEndedOptions dayEnded)
    {
        var today = DateOnly.FromDateTime(now.Date);

        // Fora de dia útil os dois sinais ficam desligados (§2.2).
        if (!WorkDayResolver.IsWorkingDay(today, work)) yield break;

        // O que conta como aberta é configurável (D-007). O checkpoint do meio-dia segue o mesmo
        // critério do fim de jornada: contar diferente nas duas pontas do dia confundiria.
        var open = tasks.Count(t => !t.IsCompleted && Counts(t, today, dayEnded.CountMode));
        var midday = At(now, work.MiddayHour, work.MiddayMinute);
        var end = At(now, work.EndHour, work.EndMinute);

        if (now >= midday && now < end)
        {
            var since = now - midday;

            if (open > 0 && since <= TimeSpan.FromMinutes(limits.MiddayWindowMinutes))
            {
                // Meio-dia nunca é vermelho: trabalho pendente às 12:00 é o estado esperado.
                yield return new Signal
                {
                    Name = SignalNames.MiddayCheckpoint,
                    Severity = Severity.Attention,
                    Reason = $"Metade do dia: {Plural(open, "tarefa aberta", "tarefas abertas")}",
                    Category = SignalCategory.DayBoundary,
                    Occurrence = Signal.OccurrenceFor(SignalNames.MiddayCheckpoint, today),
                    Since = midday,
                };
            }
            else if (open == 0 && since <= TimeSpan.FromMinutes(limits.MiddayCleanMinutes))
            {
                yield return new Signal
                {
                    Name = SignalNames.MiddayCheckpoint,
                    Severity = Severity.Info,
                    Reason = "Metade do dia — em dia",
                    Category = SignalCategory.DayBoundary,
                    Occurrence = Signal.OccurrenceFor(SignalNames.MiddayCheckpoint, today),
                    SelfClearing = true,
                    Since = midday,
                };
            }
        }

        if (now < end) yield break;

        yield return open > 0
            ? new Signal
            {
                // 17:00 com coisa aberta é o outro vermelho do produto. Persiste até o clique.
                Name = SignalNames.DayEnded,
                Severity = Severity.Critical,
                Reason = $"Jornada encerrada: {Plural(open, "tarefa aberta", "tarefas abertas")}",
                Category = SignalCategory.DayBoundary,
                Occurrence = Signal.OccurrenceFor(SignalNames.DayEnded, today),
                Since = end,
            }
            : new Signal
            {
                Name = SignalNames.DayEnded,
                Severity = Severity.Info,
                Reason = "Dia fechado ✓",
                Category = SignalCategory.DayBoundary,
                Occurrence = Signal.OccurrenceFor(SignalNames.DayEnded, today),
                SelfClearing = true,
                Since = end,
            };
    }

    // ---------------------------------------------------------------- §2.3 tarefas

    private static IEnumerable<Signal> Tasks(IReadOnlyList<TaskItem> tasks, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.Date);

        var overdue = tasks.Count(t => !t.IsCompleted && t.Bucket(today) == TaskBucket.Overdue);
        if (overdue == 0) yield break;

        yield return new Signal
        {
            Name = SignalNames.TaskOverdue,
            Severity = Severity.Attention,
            Reason = Plural(overdue, "tarefa vencida", "tarefas vencidas"),
            Category = SignalCategory.Tasks,
            Occurrence = Signal.OccurrenceFor(SignalNames.TaskOverdue, today),
            Since = now,
        };
    }

    // ---------------------------------------------------------------- auxiliares

    private static bool Counts(TaskItem task, DateOnly today, DayEndedCountMode mode) =>
        mode == DayEndedCountMode.AllOpen
        || task.Bucket(today) is TaskBucket.Overdue or TaskBucket.Today;

    private static string Plural(int n, string um, string varios) =>
        n == 1 ? $"1 {um}" : $"{n} {varios}";

    private static string Minutes(TimeSpan span) =>
        $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))} min";

    private static DateTimeOffset At(DateTimeOffset now, int hour, int minute) =>
        new(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);
}

internal static class SignalNames
{
    public const string MeetingUpcoming = "MeetingUpcoming";
    public const string MeetingImminent = "MeetingImminent";
    public const string MeetingStarted = "MeetingStarted";
    public const string MeetingBackToBack = "MeetingBackToBack";
    public const string MeetingEnded = "MeetingEnded";
    public const string MeetingRanIntoNext = "MeetingRanIntoNext";
    public const string MeetingAmbiguous = "MeetingAmbiguous";
    public const string MiddayCheckpoint = "MiddayCheckpoint";
    public const string DayEnded = "DayEnded";
    public const string TaskOverdue = "TaskOverdue";
}
