using System.Net;
using System.Text.RegularExpressions;

namespace Tempus.Domain;

internal enum ConferenceProvider
{
    /// <summary>Link de conferência de um serviço que o Tempus não reconhece pelo host.</summary>
    Other,
    Meet,
    Zoom,
    Teams,
}

/// <summary>
/// A call de um compromisso: para onde ir e em que serviço.
/// <para>
/// O modelo antes era um <c>string MeetUrl</c>, e o nome carregava a suposição de que toda call é
/// Meet. Reunião de Zoom com o link colado no convite não tinha como ser representada — e é o
/// caso que originou este tipo (D-017).
/// </para>
/// </summary>
internal sealed record Conference
{
    public required string Url { get; init; }
    public required ConferenceProvider Provider { get; init; }

    /// <summary>Nome do serviço para texto de UI. Genérico quando não reconhecemos o host.</summary>
    public string ProviderName => Provider switch
    {
        ConferenceProvider.Meet => "Meet",
        ConferenceProvider.Zoom => "Zoom",
        ConferenceProvider.Teams => "Teams",
        _ => "call",
    };

    /// <summary>
    /// URLs em texto livre. Deliberadamente permissiva à direita: <b>não</b> corta em <c>?</c> nem
    /// <c>&amp;</c>, porque o link do Zoom leva a senha em <c>?pwd=</c> e sem ela a call pede
    /// senha na entrada. Para no primeiro caractere que não pode fazer parte de uma URL.
    /// </summary>
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s""'<>\\]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Classifica pelo <b>host</b>, nunca por substring solta. Um link cujo caminho contenha
    /// "zoom" não é uma call de Zoom, e casar por substring transformaria qualquer link de
    /// marketing num convite de reunião.
    /// </summary>
    public static ConferenceProvider Classify(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return ConferenceProvider.Other;

        var host = uri.Host.ToLowerInvariant();

        if (host is "meet.google.com") return ConferenceProvider.Meet;

        // Cada conta de Zoom tem seu próprio subdomínio (us02web, empresa.zoom.us, …).
        if (host is "zoom.us" || host.EndsWith(".zoom.us", StringComparison.Ordinal))
            return ConferenceProvider.Zoom;

        if (host is "teams.microsoft.com" or "teams.live.com") return ConferenceProvider.Teams;

        return ConferenceProvider.Other;
    }

    /// <summary>Constrói a partir de uma URL já conhecida como sendo a call (campo estruturado).</summary>
    public static Conference? FromUrl(string? url) =>
        string.IsNullOrWhiteSpace(url)
            ? null
            : new Conference { Url = url.Trim(), Provider = Classify(url.Trim()) };

    /// <summary>
    /// Procura uma call em texto livre — <c>location</c> ou <c>description</c> do evento.
    /// <para>
    /// Só devolve provedor <b>conhecido</b>. Um convite tem links de tudo: rastreador de e-mail,
    /// documento anexo, cancelamento de inscrição. Abrir o primeiro que aparecer seria um clique
    /// no escuro, então de texto livre só sai o que dá para reconhecer pelo host.
    /// </para>
    /// </summary>
    public static Conference? FindIn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // A descrição do Google Calendar costuma vir em HTML (<a href="…">), onde o & do
        // query string aparece como &amp;. Decodificar antes, ou a URL sai quebrada.
        var decoded = WebUtility.HtmlDecode(text);

        foreach (Match match in UrlPattern.Matches(decoded))
        {
            var url = Trim(match.Value);
            var provider = Classify(url);

            if (provider != ConferenceProvider.Other)
                return new Conference { Url = url, Provider = provider };
        }

        return null;
    }

    /// <summary>Tira pontuação de frase que ficou grudada no fim da URL.</summary>
    private static string Trim(string url) => url.TrimEnd('.', ',', ';', ':', ')', ']', '}', '>', '"');
}
