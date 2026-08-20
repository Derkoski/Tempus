namespace Tempus.Domain;

/// <summary>
/// A que matiz uma cor pertence, aos olhos de quem olha de relance.
/// <para>
/// Não é a cor: é a <b>família</b>. O azul do humor <c>InMeeting</c> e o azul da severidade
/// <c>Info</c> são hexadecimais diferentes que o olho lê como a mesma coisa, e é isso que importa
/// aqui.
/// </para>
/// </summary>
internal enum ColorFamily
{
    /// <summary>Sem cor própria — herda a da barra.</summary>
    Neutral,

    /// <summary>Cinza dessaturado do estado <c>Offline</c>. Não compete com nada (§0).</summary>
    Gray,

    Green,
    Blue,
    Amber,
    Red,
}

/// <summary>
/// Impede que os dois vocabulários de cor do <c>SEVERITY.md</c> §0.5 se confundam — invariante I9.
/// <para>
/// O §0.5 dizia que a <i>forma</i> já separava os dois: o slot de tempo com texto tingido, o chip
/// sempre preenchido. <b>A premissa envelheceu.</b> Quando <c>EndingSoon</c>, <c>Imminent</c> e
/// <c>Overrun</c> passaram a preencher o slot, os dois viraram pílulas — e como compartilham a
/// paleta, viraram pílulas <i>idênticas</i>. O usuário encontrou isso em uso: "fica tudo na mesma
/// cor, confunde um pouco".
/// </para>
/// <para>
/// A comparação é por família e não por igualdade de <c>Color</c> de propósito: por igualdade,
/// <c>Ocupado</c> em <c>#8FB8DC</c> ao lado de um chip <c>#1E4B73</c> passaria batido, e são os
/// dois azuis que motivaram a queixa.
/// </para>
/// <para>Puro, sem tocar em <c>System.Windows.Media</c> (regra 8) — quem pinta é a paleta.</para>
/// </summary>
internal static class ColorVocabulary
{
    public static ColorFamily Of(TimeMood mood) => mood switch
    {
        TimeMood.Free => ColorFamily.Green,

        // Os três âmbares do humor: fora de expediente, próxima chegando, atual acabando.
        TimeMood.OffHours or TimeMood.Approaching or TimeMood.EndingSoon => ColorFamily.Amber,

        TimeMood.InMeeting => ColorFamily.Blue,
        TimeMood.Imminent or TimeMood.Overrun => ColorFamily.Red,
        _ => ColorFamily.Gray,
    };

    public static ColorFamily Of(Severity severity) => severity switch
    {
        Severity.Info => ColorFamily.Blue,
        Severity.Attention => ColorFamily.Amber,
        Severity.Critical => ColorFamily.Red,
        _ => ColorFamily.Neutral,
    };

    /// <summary>
    /// True quando as duas áreas cairiam na mesma família e precisam ser separadas pela forma.
    /// <para>
    /// <c>Neutral</c> e <c>Gray</c> nunca colidem: o primeiro não pinta nada, e o segundo é o
    /// <c>Offline</c>, que anula a escala inteira antes de qualquer sinal ser avaliado (§0).
    /// </para>
    /// </summary>
    public static bool Collide(TimeMood mood, Severity severity)
    {
        var family = Of(mood);

        return family == Of(severity)
            && family is not (ColorFamily.Neutral or ColorFamily.Gray);
    }
}
