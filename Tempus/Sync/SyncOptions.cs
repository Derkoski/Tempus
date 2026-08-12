using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Tempus.Sync;

/// <summary>
/// Ritmo do polling. Rápido enquanto você está no computador, lento quando não está.
/// <para>
/// Sem webhooks (exigem endpoint HTTPS público), a latência de "criei no celular, apareceu na
/// barra" é o intervalo de polling. Um ciclo são ~2 requisições de poucos KB — o custo real é
/// desprezível, e o que de fato incomodaria seria manter esse ritmo com a máquina ociosa.
/// </para>
/// </summary>
internal sealed record SyncOptions
{
    /// <summary>Intervalo com o usuário ativo. Define a latência percebida.</summary>
    public int ActiveSeconds { get; init; } = 15;

    /// <summary>Intervalo com a máquina ociosa ou bloqueada.</summary>
    public int IdleSeconds { get; init; } = 120;

    /// <summary>Quanto tempo sem input do usuário até considerar ocioso.</summary>
    public int IdleAfterSeconds { get; init; } = 180;

    public TimeSpan Active => TimeSpan.FromSeconds(Math.Max(5, ActiveSeconds));
    public TimeSpan Idle => TimeSpan.FromSeconds(Math.Max(ActiveSeconds, IdleSeconds));
    public TimeSpan IdleAfter => TimeSpan.FromSeconds(Math.Max(30, IdleAfterSeconds));

    public static SyncOptions Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new SyncOptions();

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("Sync", out var sync)) return new SyncOptions();

            return sync.Deserialize<SyncOptions>(new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new SyncOptions();
        }
        catch (Exception)
        {
            return new SyncOptions();
        }
    }
}

/// <summary>
/// Há quanto tempo o usuário não toca em teclado ou mouse.
/// <para>
/// É a resposta honesta a "não quero deixar o PC lento": quando você não está na máquina, a
/// latência não importa para ninguém, então não há motivo para gastar rede. Sessão bloqueada
/// também cai aqui, porque o relógio de input para de avançar.
/// </para>
/// </summary>
internal static class UserIdle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static TimeSpan Duration()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;

        // Ambos são contadores de 32 bits que dão a volta a cada ~49 dias; a subtração sem sinal
        // continua correta na virada, o cast para int é que estragaria.
        var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }
}
