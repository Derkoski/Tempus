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
    Color OfflineBackground,
    Color OfflineForeground,
    Color PanelBackground,
    Color PanelBorder,
    Color RowHover,
    Color FreeForeground,
    Color InMeetingForeground)
{
    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

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
        OfflineBackground: Hex("#FFEDEDED"),
        OfflineForeground: Hex("#FF8A8A8A"),
        PanelBackground: Hex("#FFFBFBFB"),
        PanelBorder: Hex("#FFD8D8D8"),
        RowHover: Hex("#FFEFEFEF"),
        FreeForeground: Hex("#FF2E7D3B"),
        InMeetingForeground: Hex("#FF1F5B87"));

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
}
