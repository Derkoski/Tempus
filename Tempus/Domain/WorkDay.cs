namespace Tempus.Domain;

internal sealed record WorkDayOptions
{
    public int StartHour { get; init; } = 8;
    public int StartMinute { get; init; }
    public int MiddayHour { get; init; } = 12;
    public int MiddayMinute { get; init; }

    /// <summary>Volta do almoço. A janela entre o meio-dia e este horário conta como fora do expediente.</summary>
    public int LunchEndHour { get; init; } = 13;
    public int LunchEndMinute { get; init; }

    public int EndHour { get; init; } = 17;
    public int EndMinute { get; init; }

    /// <summary>Quantos minutos antes da fronteira o contador aparece.</summary>
    public int WindowMinutes { get; init; } = 60;

    /// <summary>Emendas e pontos facultativos da TOTVS, em <c>yyyy-MM-dd</c> (D-008).</summary>
    public string[] ExtraHolidays { get; init; } = [];

    public static readonly WorkDayOptions Default = new();
}

/// <summary>
/// Quanto falta para a próxima fronteira do dia. Ocupa um indicador pequeno e independente, à
/// esquerda de tudo.
/// <para>
/// Existe por causa de um incidente concreto: uma week review segurada até 12:22 quando o horário
/// de todos terminava 12:00. A reunião em si é tratada pelo humor temporal; isto cobre o outro
/// caso, o de estar trabalhando sem agenda e perder a hora.
/// </para>
/// </summary>
internal sealed record BoundaryStatus
{
    public required TimeSpan Remaining { get; init; }

    /// <summary>Rótulo da fronteira: <c>12:00</c> ou <c>17:00</c>.</summary>
    public required string At { get; init; }

    /// <summary>1 = início da janela, 0 = na fronteira. Governa o gradiente verde→âmbar→vermelho.</summary>
    public required double Progress { get; init; }

    public int Minutes => Math.Max(0, (int)Math.Ceiling(Remaining.TotalMinutes));
}

internal static class WorkDayResolver
{
    /// <summary>
    /// A fronteira mais próxima ainda por vir hoje, ou <c>null</c> se está longe demais, se o dia
    /// já acabou, ou se não é dia útil. Função pura (regra 8).
    /// </summary>
    public static BoundaryStatus? Resolve(DateTimeOffset now, WorkDayOptions options)
    {
        var today = DateOnly.FromDateTime(now.Date);
        if (!IsWorkingDay(today, options)) return null;

        var midday = At(now, options.MiddayHour, options.MiddayMinute);
        var end = At(now, options.EndHour, options.EndMinute);

        var (boundary, label) = now < midday
            ? (midday, $"{options.MiddayHour:00}:{options.MiddayMinute:00}")
            : now < end
                ? (end, $"{options.EndHour:00}:{options.EndMinute:00}")
                : (default(DateTimeOffset), string.Empty);

        if (label.Length == 0) return null; // expediente encerrado

        var remaining = boundary - now;
        var window = TimeSpan.FromMinutes(Math.Max(1, options.WindowMinutes));
        if (remaining > window) return null;

        return new BoundaryStatus
        {
            Remaining = remaining,
            At = label,
            Progress = Math.Clamp(remaining.TotalMinutes / window.TotalMinutes, 0, 1),
        };
    }

    private static DateTimeOffset At(DateTimeOffset now, int hour, int minute) =>
        new(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);

    /// <summary>
    /// Fora do expediente: fim de semana, feriado, ou fora da janela de trabalho do dia.
    /// <para>
    /// Sem isto, às 19:48 de uma terça a barra diz "Livre" em verde — tecnicamente verdade e
    /// completamente enganoso. Você não está livre, o dia acabou.
    /// </para>
    /// </summary>
    public static bool IsOffHours(DateTimeOffset now, WorkDayOptions options) =>
        OffHoursLabel(now, options) is not null;

    /// <summary>
    /// O rótulo de fora-de-expediente, ou <c>null</c> se está em horário de trabalho.
    /// <para>
    /// Cada situação ganha a palavra certa em vez de um texto único: no almoço "Dia Encerrado"
    /// seria falso, e num sábado "Fora de expediente" é mais longo e diz menos que "Folga".
    /// Todas compartilham o mesmo tratamento visual — laranja e negrito.
    /// </para>
    /// <para>
    /// Capitalizadas como rótulo, não como frase. São uma família de estados curtos, e a
    /// capitalização as distingue das mensagens da barra, que seguem capitalização de sentença
    /// ("Login do Google expirou — clique para entrar").
    /// </para>
    /// </summary>
    public static string? OffHoursLabel(DateTimeOffset now, WorkDayOptions options)
    {
        if (!IsWorkingDay(DateOnly.FromDateTime(now.Date), options)) return "Folga";

        if (now >= At(now, options.EndHour, options.EndMinute)) return "Dia Encerrado";
        if (now < At(now, options.StartHour, options.StartMinute)) return "Dia Encerrado";

        var lunchStart = At(now, options.MiddayHour, options.MiddayMinute);
        var lunchEnd = At(now, options.LunchEndHour, options.LunchEndMinute);

        return now >= lunchStart && now < lunchEnd ? "Almoço" : null;
    }

    public static bool IsWorkingDay(DateOnly date, WorkDayOptions options)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        if (BrazilianHolidays.IsHoliday(date)) return false;

        foreach (var extra in options.ExtraHolidays)
            if (DateOnly.TryParse(extra, out var parsed) && parsed == date) return false;

        return true;
    }
}

/// <summary>
/// Feriados calculados localmente, sem rede e sem dependência (D-008): nacionais, do Paraná e de
/// Pato Branco. Os móveis derivam da Páscoa pelo algoritmo de Meeus/Jones/Butcher.
/// </summary>
internal static class BrazilianHolidays
{
    public static bool IsHoliday(DateOnly date)
    {
        // Nacionais de data fixa. 20/11 é nacional desde 2024 (Lei 14.759/2023).
        var fixedDays = new (int Month, int Day)[]
        {
            (1, 1), (4, 21), (5, 1), (9, 7), (10, 12), (11, 2), (11, 15), (11, 20), (12, 25),
            (12, 19), // Emancipação Política do Paraná
            (6, 29),  // São Pedro Apóstolo, padroeiro de Pato Branco (Lei Municipal 40/1970)
            (12, 14), // Emancipação Política de Pato Branco
        };

        foreach (var (month, day) in fixedDays)
            if (date.Month == month && date.Day == day) return true;

        var easter = Easter(date.Year);

        // Carnaval e Corpus Christi são ponto facultativo e não feriado legal, mas na prática se
        // comportam como feriado — e o custo de errar aqui é zero, porque o usuário não abre o
        // computador nesses dias.
        return date == easter.AddDays(-48)  // segunda de Carnaval
            || date == easter.AddDays(-47)  // terça de Carnaval
            || date == easter.AddDays(-2)   // Sexta-feira Santa
            || date == easter.AddDays(60);  // Corpus Christi
    }

    public static DateOnly Easter(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = ((19 * a) + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        var m = (a + (11 * h) + (22 * l)) / 451;

        var month = (h + l - (7 * m) + 114) / 31;
        var day = ((h + l - (7 * m) + 114) % 31) + 1;

        return new DateOnly(year, month, day);
    }
}
