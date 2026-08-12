namespace Tempus.Domain;

/// <summary>Um compromisso do dia, como a UI precisa dele.</summary>
internal sealed record AgendaItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }

    /// <summary>Link do Meet, quando existe. Ausente em reunião presencial ou por telefone.</summary>
    public string? MeetUrl { get; init; }

    public bool IsAllDay { get; init; }

    public bool IsRunningAt(DateTimeOffset now) => now >= Start && now < End;
    public bool HasEndedBy(DateTimeOffset now) => now >= End;
}

/// <summary>
/// O espaço entre dois compromissos consecutivos. Existe como conceito próprio porque é
/// exatamente a informação que o produto vende: saber que <b>não</b> há intervalo é o que
/// permite não estourar a reunião.
/// </summary>
internal readonly record struct AgendaGap(TimeSpan Duration)
{
    public bool IsOverlap => Duration < TimeSpan.Zero;
    public bool IsBackToBack => Duration == TimeSpan.Zero;

    public string Label => this switch
    {
        { IsOverlap: true } => $"sobreposição de {Humanize(-Duration)}",
        { IsBackToBack: true } => "sem intervalo",
        _ => $"{Humanize(Duration)} livre",
    };

    private static string Humanize(TimeSpan span)
    {
        var total = (int)Math.Round(span.TotalMinutes);
        if (total < 60) return $"{total} min";

        var hours = total / 60;
        var minutes = total % 60;
        return minutes == 0 ? $"{hours}h" : $"{hours}h{minutes:00}";
    }
}
