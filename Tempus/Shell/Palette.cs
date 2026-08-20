using System.Windows.Media;
using Microsoft.Win32;
using Tempus.Domain;

namespace Tempus.Shell;

/// <summary>
/// Cores da barra. Só a área de motivo colore — o fundo da barra e os contadores permanecem
/// neutros mesmo com alerta vermelho (invariante I6): a cor pertence ao estado, não aos números.
/// </summary>
internal sealed record Palette(
    Color BarBackground,
    Color BarForeground,
    Color Muted,
    Color InfoBackground,
    Color InfoForeground,
    Color AttentionBackground,
    Color AttentionForeground,
    Color CriticalBackground,
    Color CriticalForeground,

    // A terceira cor de cada severidade: a tinta do chip em contorno (I9). Nenhuma das outras duas
    // serve — a de preenchimento é escura demais sobre a barra escura, e a de texto sobre ela é
    // clara demais e perde a identidade do matiz. Ver OutlineFor.
    Color InfoInk,
    Color AttentionInk,
    Color CriticalInk,
    Color OfflineBackground,
    Color OfflineForeground,
    Color PanelBackground,
    Color PanelBorder,
    Color RowHover,
    Color FreeForeground,
    Color InMeetingForeground)
{
    /// <summary>
    /// Fundo do bloco de status nos humores calmos (D-023). Um degrau acima do fundo da barra —
    /// o suficiente para o status ler como bloco próprio, e discreto o bastante para não competir
    /// com o bloco <b>aceso</b> dos humores que escalam.
    /// </summary>
    public Color ChipBackground => Color.FromArgb(
        0x26,
        BarForeground.R,
        BarForeground.G,
        BarForeground.B);

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    /// <summary>
    /// Derivado da luminância do fundo do painel, e não de um campo separado: um booleano poderia
    /// discordar das cores, e a pergunta "esta paleta é escura?" já tem resposta nelas.
    /// </summary>
    public bool IsDark =>
        (0.299 * PanelBackground.R) + (0.587 * PanelBackground.G) + (0.114 * PanelBackground.B) < 128;

    /// <summary>Taskbar escura — o caso padrão no Windows 11.</summary>
    public static readonly Palette Dark = new(
        BarBackground: Hex("#FF1F1F1F"),
        BarForeground: Hex("#FFE8E8E8"),
        Muted: Hex("#FF9A9A9A"),
        InfoBackground: Hex("#FF1E4B73"),
        InfoForeground: Hex("#FFD3E9FF"),
        AttentionBackground: Hex("#FFB4530A"),
        AttentionForeground: Hex("#FFFFEBCF"),
        CriticalBackground: Hex("#FFC02626"),
        CriticalForeground: Hex("#FFFFE3E3"),
        // Tintas de contorno: claras o bastante para ler sobre #1F1F1F (todas acima de 5:1) e
        // ainda inconfundivelmente azul, âmbar e vermelha.
        InfoInk: Hex("#FF7CB3E8"),
        AttentionInk: Hex("#FFE8913C"),
        CriticalInk: Hex("#FFF26B6B"),
        OfflineBackground: Hex("#FF1A1A1A"),
        OfflineForeground: Hex("#FF6E6E6E"),
        PanelBackground: Hex("#FF262626"),
        PanelBorder: Hex("#FF3D3D3D"),
        RowHover: Hex("#FF333333"),
        // Verde dessaturado de propósito: fica visível a maior parte do dia, então precisa ser
        // legível de relance sem competir com um alarme (D-012).
        FreeForeground: Hex("#FF7FC08A"),
        InMeetingForeground: Hex("#FF8FB8DC"));

    public static readonly Palette Light = new(
        BarBackground: Hex("#FFF3F3F3"),
        BarForeground: Hex("#FF1B1B1B"),
        Muted: Hex("#FF6A6A6A"),
        InfoBackground: Hex("#FFCFE6FA"),
        InfoForeground: Hex("#FF0B3B60"),
        AttentionBackground: Hex("#FFFDE2B8"),
        AttentionForeground: Hex("#FF6B3A00"),
        CriticalBackground: Hex("#FFF7C9C9"),
        CriticalForeground: Hex("#FF8A1414"),
        // No tema claro a cor de texto sobre o preenchimento já é escura e serve de tinta: o
        // problema é o oposto do escuro, sobra contraste em vez de faltar.
        InfoInk: Hex("#FF0B3B60"),
        AttentionInk: Hex("#FF6B3A00"),
        CriticalInk: Hex("#FF8A1414"),
        OfflineBackground: Hex("#FFEDEDED"),
        OfflineForeground: Hex("#FF8A8A8A"),
        PanelBackground: Hex("#FFFBFBFB"),
        PanelBorder: Hex("#FFD8D8D8"),
        RowHover: Hex("#FFEFEFEF"),
        FreeForeground: Hex("#FF2E7D3B"),
        InMeetingForeground: Hex("#FF1F5B87"));

    /// <summary>
    /// Cor de marca do serviço de call, para o ponto do painel S3 (D-017).
    /// <para>
    /// Fica fora do record de tema de propósito: identidade de marca não acompanha claro/escuro.
    /// E não pertence a nenhum dos dois vocabulários de cor da barra — só o S3 usa isto, porque
    /// lá não há disputa com a escala de severidade nem com o humor temporal.
    /// </para>
    /// </summary>
    public Color ForProvider(ConferenceProvider provider) => provider switch
    {
        // Verde do Meet e azul do Zoom. Os tons escolhidos são os que a própria marca usa no
        // ícone, e não o do logotipo em texto: legíveis sobre painel claro e escuro.
        ConferenceProvider.Meet => Hex("#FF00AC47"),
        ConferenceProvider.Zoom => Hex("#FF2D8CFF"),

        // Teams e desconhecidos são detectados e clicáveis, mas ficam fora do vocabulário de
        // cor — só o Meet e o Zoom foram pedidos, e cada cor nova custa legibilidade.
        _ => Muted,
    };

    /// <summary>
    /// Segue o tema da <b>taskbar</b>, que é <c>SystemUsesLightTheme</c> — e não
    /// <c>AppsUseLightTheme</c>, que governa as janelas de app. Escolher o errado deixa a barra
    /// clara sobre taskbar escura.
    /// </summary>
    public static Palette FromSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("SystemUsesLightTheme");
            return value is int i && i != 0 ? Light : Dark;
        }
        catch (Exception)
        {
            return Dark;
        }
    }

    /// <summary>
    /// Cores do slot de tempo. <c>Background</c> é <c>Transparent</c> nos humores calmos: eles são
    /// ambiente e ficam só com o texto tingido. Preenchimento é reservado ao que pede antecipação,
    /// para que a escalada aconteça na forma e não apenas na cor (D-012).
    /// </summary>
    public (Color Background, Color Foreground) For(TimeStatus time) => time.Mood switch
    {
        TimeMood.Free => (Colors.Transparent, FreeForeground),
        // Laranja no texto, sem preenchimento: firme o bastante para você notar que o dia acabou,
        // discreto o bastante para não competir com um alarme de verdade.
        TimeMood.OffHours => (Colors.Transparent, AttentionBackground),
        TimeMood.InMeeting => (Colors.Transparent, InMeetingForeground),
        TimeMood.Approaching => (Colors.Transparent, AttentionBackground),
        TimeMood.EndingSoon => (AttentionBackground, AttentionForeground),
        TimeMood.Imminent => (CriticalBackground, CriticalForeground),
        TimeMood.Overrun => (CriticalBackground, CriticalForeground),
        _ => (Colors.Transparent, OfflineForeground),
    };

    /// <summary>
    /// Gradiente contínuo do contador de fim de expediente: verde longe da fronteira, amarelando
    /// no meio, vermelho na hora.
    /// <para>
    /// Cores fixas nos dois temas, ao contrário do resto da paleta: este indicador é um alarme de
    /// contagem regressiva, e o que ele comunica não deveria mudar de intensidade porque o Windows
    /// está em modo claro.
    /// </para>
    /// </summary>
    /// <param name="progress">1 no início da janela, 0 na fronteira.</param>
    public static (Color Background, Color Foreground) Boundary(double progress)
    {
        var far = Hex("#FF2F7D3D");
        var mid = Hex("#FFB4530A");
        var near = Hex("#FFC02626");

        var t = Math.Clamp(progress, 0, 1);
        var background = t >= 0.5
            ? Lerp(mid, far, (t - 0.5) * 2)
            : Lerp(near, mid, t * 2);

        return (background, Hex("#FFF4F4F4"));
    }

    private static Color Lerp(Color from, Color to, double t)
    {
        var amount = Math.Clamp(t, 0, 1);

        return Color.FromArgb(
            255,
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    public (Color Background, Color Foreground) For(ShellState state)
    {
        if (state.IsOffline) return (OfflineBackground, OfflineForeground);

        return state.Severity switch
        {
            Severity.Info => (InfoBackground, InfoForeground),
            Severity.Attention => (AttentionBackground, AttentionForeground),
            Severity.Critical => (CriticalBackground, CriticalForeground),
            _ => (Colors.Transparent, BarForeground),
        };
    }

    /// <summary>
    /// A tinta do chip quando ele abre mão do preenchimento para não se confundir com o slot de
    /// tempo (invariante I9). Vale para borda e texto — os dois na mesma cor, que é o que faz o
    /// contorno ler como uma pílula vazada em vez de duas decisões separadas.
    /// <para>
    /// <b>É uma cor própria, não uma das outras duas.</b> Tentei as duas antes de aceitar isso: a
    /// de preenchimento é escura demais sobre a barra escura — o azul de <c>Info</c>
    /// (<c>#1E4B73</c>) dá 1,81:1 contra <c>#1F1F1F</c>, um contorno fantasma, e foi exatamente o
    /// que o usuário reclamou de não conseguir ler. E a de texto sobre o preenchimento é clara
    /// demais: o vermelho viraria um rosa pálido e perderia a identidade de alarme.
    /// </para>
    /// <para>
    /// Contorno é um <b>terceiro contexto</b>, e contexto novo pede cor nova. O teste de contraste
    /// em <c>PaletteOutlineTests</c> é o que impede a próxima escolha de errar de novo.
    /// </para>
    /// </summary>
    public Color OutlineFor(Severity severity) => severity switch
    {
        Severity.Info => InfoInk,
        Severity.Attention => AttentionInk,
        Severity.Critical => CriticalInk,
        _ => BarForeground,
    };

    private static double Contrast(Color a, Color b)
    {
        var (x, y) = (Relative(a), Relative(b));
        var (lighter, darker) = x > y ? (x, y) : (y, x);

        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// Luminância relativa da WCAG, com correção de gama. Diferente da conta do <see cref="IsDark"/>,
    /// que é a fórmula YIQ e serve para "esta paleta é escura?" — aqui a pergunta é mais fina:
    /// "dá para ler isto sobre aquilo?".
    /// </summary>
    private static double Relative(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }
}
