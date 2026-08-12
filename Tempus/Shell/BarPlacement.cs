using System.Text.Json;
using System.Text.Json.Serialization;
using Tempus.Interop;
using static Tempus.Interop.NativeMethods;

namespace Tempus.Shell;

internal enum BarAnchor
{
    /// <summary>Imediatamente à esquerda da bandeja. Espaço tipicamente vazio nos dois modos
    /// de alinhamento de ícones do Windows 11.</summary>
    LeftOfTray,

    /// <summary>Canto esquerdo da taskbar. Vazio quando os ícones estão centralizados.</summary>
    TaskbarStart,
}

internal sealed record BarOptions
{
    /// <summary>
    /// Largura pedida, em slots de ícone da taskbar.
    /// <para>
    /// O pedido original era 3 a 5 slots, mas na prática 5 slots (200px) deixavam ~64px para o
    /// texto do motivo depois de relógio e contadores — pequeno demais para caber uma frase como
    /// "Daily acabou — Review já começou". A área de motivo é a informação mais importante da
    /// barra, então ela dita a largura.
    /// </para>
    /// </summary>
    public int Slots { get; init; } = 12;

    /// <summary>Largura de um slot de ícone do Windows 11, em DIP.</summary>
    public double SlotWidthDip { get; init; } = 40;

    /// <summary>Folga entre a barra e a âncora, em DIP.</summary>
    public double GapDip { get; init; } = 8;

    /// <summary>Respiro vertical dentro da taskbar, em DIP.</summary>
    public double VerticalMarginDip { get; init; } = 4;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BarAnchor Anchor { get; init; } = BarAnchor.LeftOfTray;

    public static BarOptions Load(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return new BarOptions();

            using var document = JsonDocument.Parse(System.IO.File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("Bar", out var bar)) return new BarOptions();

            return bar.Deserialize<BarOptions>(new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new BarOptions();
        }
        catch (Exception)
        {
            // Config inválida não deve impedir a barra de aparecer.
            return new BarOptions();
        }
    }
}

internal static class BarPlacement
{
    /// <summary>
    /// Retângulo da barra em <b>pixels físicos</b>, ou <c>null</c> se ela não deve aparecer agora.
    /// </summary>
    /// <remarks>
    /// Trabalhamos em pixels físicos porque a posição vem do retângulo da taskbar, que o Win32
    /// reporta em físico. Converter para DIP no meio do caminho só adiciona erro de arredondamento.
    /// </remarks>
    public static RECT? Compute(TaskbarInfo taskbar, BarOptions options, uint dpi)
    {
        // Windows 11 não suporta taskbar vertical. Se algum dia suportar, o fallback de tray
        // (D-002) é a resposta certa — não vale distorcer o layout da barra para caber.
        if (!taskbar.IsHorizontal) return null;

        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;
        var width = (int)Math.Round(options.Slots * options.SlotWidthDip * scale);
        var gap = (int)Math.Round(options.GapDip * scale);
        var margin = (int)Math.Round(options.VerticalMarginDip * scale);

        var height = taskbar.Bounds.Height - (margin * 2);
        if (width <= 0 || height <= 0) return null;

        var top = taskbar.Bounds.Top + margin;

        // Limite direito: o que vier primeiro entre a borda da taskbar e o início da bandeja.
        // No Windows 11 o TrayNotifyWnd legado nem sempre reflete a bandeja XAML visível, e ele
        // se move quando um ícone aparece. Tratar isto como um teto rígido — em vez de confiar na
        // subtração da âncora — torna a sobreposição impossível em vez de improvável.
        var rightLimit = Math.Min(taskbar.Bounds.Right, taskbar.TrayBounds.Left) - gap;
        var leftLimit = taskbar.Bounds.Left + gap;

        var left = options.Anchor switch
        {
            BarAnchor.TaskbarStart => leftLimit,
            _ => rightLimit - width,
        };

        if (left + width > rightLimit) left = rightLimit - width;
        if (left < leftLimit) left = leftLimit;

        // Não couber é resultado válido: melhor não aparecer do que aparecer por cima da bandeja.
        if (left + width > rightLimit) return null;

        return new RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }
}
