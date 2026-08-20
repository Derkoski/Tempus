using System.Windows.Media;
using Tempus.Domain;
using Tempus.Shell;
using Xunit;

namespace Tempus.Tests;

/// <summary>
/// A tinta do chip em modo contorno (I9) precisa ser legível sobre a barra <b>nos dois temas</b>.
/// <para>
/// É o caminho fácil de errar: no tema escuro a cor certa é a saturada, no claro ela é um pastel
/// feito para servir de fundo e sumiria. Ninguém exercita o tema claro no dia a dia, então quem
/// tem de pegar isso é o teste.
/// </para>
/// </summary>
public class PaletteOutlineTests
{
    private static readonly Severity[] Colored =
        [Severity.Info, Severity.Attention, Severity.Critical];

    /// <summary>Luminância relativa da WCAG, com a correção de gama por canal.</summary>
    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static double Contrast(Color a, Color b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        var (lighter, darker) = x > y ? (x, y) : (y, x);

        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// 3:1 é o mínimo da WCAG para elemento de interface — borda e texto curto de alarme. Abaixo
    /// disso o contorno vira um fantasma sobre a barra.
    /// </summary>
    [Fact]
    public void A_tinta_do_contorno_tem_contraste_nos_dois_temas()
    {
        foreach (var (nome, palette) in new[] { ("escuro", Palette.Dark), ("claro", Palette.Light) })
        {
            foreach (var severity in Colored)
            {
                var contrast = Contrast(palette.OutlineFor(severity), palette.BarBackground);

                Assert.True(
                    contrast >= 3.0,
                    $"tema {nome}, {severity}: contraste {contrast:F2}:1 contra a barra");
            }
        }
    }

    /// <summary>
    /// O caso que derrubou a primeira versão, que decidia a tinta por tema em vez de por
    /// contraste: no tema <b>escuro</b> o azul de <c>Info</c> é <c>#1E4B73</c> sobre uma barra
    /// <c>#1F1F1F</c> — 1,81:1, um contorno fantasma. Adivinhar o tema não bastava.
    /// </summary>
    [Fact]
    public void O_azul_escuro_de_info_nao_e_usado_como_tinta_no_tema_escuro()
    {
        Assert.True(Contrast(Palette.Dark.InfoBackground, Palette.Dark.BarBackground) < 3.0);
        Assert.NotEqual(Palette.Dark.InfoBackground, Palette.Dark.OutlineFor(Severity.Info));
    }

    /// <summary>
    /// O mesmo erro do outro lado: no tema claro o âmbar de preenchimento é um pastel
    /// (<c>#FDE2B8</c>) que sumiria sobre a barra clara.
    /// </summary>
    [Fact]
    public void O_pastel_do_tema_claro_nao_e_usado_como_tinta()
    {
        Assert.True(Contrast(Palette.Light.AttentionBackground, Palette.Light.BarBackground) < 3.0);
        Assert.NotEqual(Palette.Light.AttentionBackground, Palette.Light.OutlineFor(Severity.Attention));
    }

    /// <summary>Mistura uma cor com alfa sobre um fundo opaco — o chip neutro é translúcido.</summary>
    private static Color Over(Color top, Color under)
    {
        var a = top.A / 255.0;

        return Color.FromRgb(
            (byte)Math.Round((top.R * a) + (under.R * (1 - a))),
            (byte)Math.Round((top.G * a) + (under.G * (1 - a))),
            (byte)Math.Round((top.B * a) + (under.B * (1 - a))));
    }

    /// <summary>
    /// O texto de cada humor precisa se ler sobre o fundo em que de fato assenta (D-037).
    /// <para>
    /// O defeito que este teste tranca: <c>Approaching</c> e <c>OffHours</c> pintavam o texto com
    /// a cor de <b>preenchimento</b> (<c>#B4530A</c>), que sobre a pílula escura vira um bordô
    /// apagado. O usuário descreveu como "um tom escuro quase apagado que não inspira urgência" —
    /// e a próxima call é a coisa mais importante do dia dele.
    /// </para>
    /// </summary>
    [Fact]
    public void O_texto_de_cada_humor_se_le_sobre_o_proprio_fundo()
    {
        foreach (var (nome, palette) in new[] { ("escuro", Palette.Dark), ("claro", Palette.Light) })
        {
            var neutro = Over(palette.ChipBackground, palette.BarBackground);

            foreach (var mood in Enum.GetValues<TimeMood>())
            {
                // `Unknown` é o Offline, e o cinza apagado dele é **decisão**, não defeito: o §0
                // quer que ele leia como "o Tempus está fora do ar", e um cinza com contraste de
                // alarme diria a coisa errada. É a única isenção, e é nomeada.
                if (mood == TimeMood.Unknown) continue;

                var (fundo, texto) = palette.For(new TimeStatus { Mood = mood, Label = "x" });
                var sobre = fundo == Colors.Transparent ? neutro : fundo;
                var contrast = Contrast(texto, sobre);

                // 4,5:1 é o mínimo da WCAG para texto — mais exigente que os 3:1 de contorno,
                // porque aqui a informação está nas letras.
                Assert.True(contrast >= 4.5, $"tema {nome}, {mood}: {contrast:F2}:1");
            }
        }
    }

    /// <summary>
    /// A tinta não pode ser nenhuma das outras duas cores da severidade: a de preenchimento não
    /// se lê sobre a barra escura, e a de texto sobre ela é pálida demais para ainda parecer
    /// alarme. Contorno é contexto próprio.
    /// </summary>
    [Fact]
    public void A_tinta_e_uma_cor_propria()
    {
        foreach (var severity in Colored)
        {
            var ink = Palette.Dark.OutlineFor(severity);

            Assert.NotEqual(Palette.Dark.For(new ShellState { Severity = severity }).Background, ink);
            Assert.NotEqual(Palette.Dark.For(new ShellState { Severity = severity }).Foreground, ink);
        }
    }

    /// <summary>
    /// A tinta continua sendo do matiz certo: um vermelho de alarme não pode virar rosa. O canal
    /// dominante é o teste mais simples que pega isso.
    /// </summary>
    [Fact]
    public void A_tinta_mantem_o_matiz_da_severidade()
    {
        var vermelho = Palette.Dark.OutlineFor(Severity.Critical);
        Assert.True(vermelho.R > vermelho.G + 60 && vermelho.R > vermelho.B + 60);

        var azul = Palette.Dark.OutlineFor(Severity.Info);
        Assert.True(azul.B > azul.R + 40);

        var ambar = Palette.Dark.OutlineFor(Severity.Attention);
        Assert.True(ambar.R > ambar.B + 60 && ambar.G > ambar.B);
    }
}
