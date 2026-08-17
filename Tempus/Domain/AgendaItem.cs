namespace Tempus.Domain;

/// <summary>
/// Sua resposta ao convite. <c>Declined</c> não existe aqui: evento recusado é descartado antes
/// de virar <see cref="AgendaItem"/> (<c>SEVERITY.md</c> §2.1).
/// </summary>
internal enum Rsvp
{
    /// <summary>Evento sem convidados — compromisso seu, não há o que responder.</summary>
    None,
    Accepted,
    Tentative,

    /// <summary>Convite parado na sua caixa. É o estado que pede ação.</summary>
    NeedsAction,
}

/// <summary>Um compromisso do dia, como a UI precisa dele.</summary>
internal sealed record AgendaItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }

    /// <summary>A call, quando existe. Ausente em reunião presencial ou por telefone.</summary>
    public Conference? Conference { get; init; }

    /// <summary>Sua resposta ao convite, para o painel S3 marcar o que ainda está pendente (D-018).</summary>
    public Rsvp Rsvp { get; init; } = Rsvp.None;

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
