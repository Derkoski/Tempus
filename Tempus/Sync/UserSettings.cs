using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tempus.Domain;

namespace Tempus.Sync;

/// <summary>
/// O que o usuário escolheu, sobreposto aos padrões de fábrica do <c>appsettings.json</c>.
/// <para>
/// Cada campo é anulável de propósito: <c>null</c> significa "não escolhi, use o padrão". Sem
/// isso, um campo ausente viraria o default do tipo — <c>StartHour = 0</c> em vez de 8 — e o
/// arquivo do usuário sobrescreveria silenciosamente configuração que ele nunca tocou.
/// </para>
/// </summary>
internal sealed record UserSettings
{
    public string? LoginHint { get; init; }
    public WorkDaySettings? WorkDay { get; init; }
    public BreakSettings? Breaks { get; init; }

    /// <summary>
    /// <c>JsonIgnore</c> porque é derivada: sem ele o serializador grava um <c>"HasLoginHint"</c>
    /// no arquivo, que ninguém lê e que passa a mentir assim que o e-mail muda por fora.
    /// </summary>
    [JsonIgnore]
    public bool HasLoginHint => !string.IsNullOrWhiteSpace(LoginHint);
}

internal sealed record WorkDaySettings
{
    public int? StartHour { get; init; }
    public int? StartMinute { get; init; }
    public int? MiddayHour { get; init; }
    public int? MiddayMinute { get; init; }
    public int? LunchEndHour { get; init; }
    public int? LunchEndMinute { get; init; }
    public int? EndHour { get; init; }
    public int? EndMinute { get; init; }

    public WorkDayOptions ApplyTo(WorkDayOptions defaults) => defaults with
    {
        StartHour = StartHour ?? defaults.StartHour,
        StartMinute = StartMinute ?? defaults.StartMinute,
        MiddayHour = MiddayHour ?? defaults.MiddayHour,
        MiddayMinute = MiddayMinute ?? defaults.MiddayMinute,
        LunchEndHour = LunchEndHour ?? defaults.LunchEndHour,
        LunchEndMinute = LunchEndMinute ?? defaults.LunchEndMinute,
        EndHour = EndHour ?? defaults.EndHour,
        EndMinute = EndMinute ?? defaults.EndMinute,
    };

    public static WorkDaySettings From(WorkDayOptions o) => new()
    {
        StartHour = o.StartHour,
        StartMinute = o.StartMinute,
        MiddayHour = o.MiddayHour,
        MiddayMinute = o.MiddayMinute,
        LunchEndHour = o.LunchEndHour,
        LunchEndMinute = o.LunchEndMinute,
        EndHour = o.EndHour,
        EndMinute = o.EndMinute,
    };
}

internal sealed record BreakSettings
{
    public bool? Enabled { get; init; }
    public int? DurationMinutes { get; init; }

    public BreakOptions ApplyTo(BreakOptions defaults) => defaults with
    {
        Enabled = Enabled ?? defaults.Enabled,
        DurationMinutes = DurationMinutes ?? defaults.DurationMinutes,
    };

    public static BreakSettings From(BreakOptions o) => new()
    {
        Enabled = o.Enabled,
        DurationMinutes = o.DurationMinutes,
    };
}

/// <summary>
/// Lê e grava as escolhas do usuário em <c>%APPDATA%\Tempus\settings.json</c>.
/// <para>
/// Fora do diretório de instalação por três motivos: fica ao lado do <c>client_secret.json</c> e
/// dos tokens, num lugar só para "seus dados"; não exige permissão de escrita em Program Files; e
/// não corre risco de ser commitado, porque está fora do repositório por construção (regra 5).
/// </para>
/// <para>
/// O <c>appsettings.json</c> do repo passa a ser só padrão de fábrica, e não pode conter dado
/// pessoal — foi de lá que o e-mail do usuário saiu.
/// </para>
/// </summary>
internal sealed class UserSettingsStore
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public UserSettingsStore(string dataDirectory) =>
        Path = System.IO.Path.Combine(dataDirectory, "settings.json");

    public string Path { get; }

    public UserSettings Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Path), Format) ?? new()
                : new UserSettings();
        }
        catch (Exception)
        {
            // Arquivo corrompido volta ao padrão de fábrica em vez de impedir a barra de subir.
            return new UserSettings();
        }
    }

    public bool Save(UserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(settings, Format));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
