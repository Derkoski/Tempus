using Tempus.Domain;

namespace Tempus.Shell;

/// <summary>
/// Ver <c>docs/SEVERITY.md</c> §1. Existem exatamente quatro níveis —
/// não adicionar um quinto sem revisar aquele documento.
/// </summary>
internal enum Severity
{
    Calm = 0,
    Info = 1,
    Attention = 2,
    Critical = 3,
}

/// <summary>
/// Tudo o que a superfície precisa para se desenhar, e nada além. É o contrato entre o
/// domínio e a UI: quando a Fase 3 chegar, a máquina de estados produz isto e a barra
/// continua igual.
/// </summary>
internal sealed record ShellState
{
    /// <summary>
    /// Estado inicial: ainda não sabemos nada, então não podemos alarmar sobre nada
    /// (<c>SEVERITY.md</c> §0).
    /// </summary>
    public static readonly ShellState Starting = new()
    {
        IsOffline = true,
        Reason = "Iniciando…",
    };

    /// <summary>
    /// Offline precede e <b>anula</b> a escala 0–3: sem login válido ou com sync velha, nenhum
    /// sinal é avaliado e os contadores não exibem número (invariante I7).
    /// </summary>
    public bool IsOffline { get; init; }

    public Severity Severity { get; init; } = Severity.Calm;

    /// <summary>
    /// Nunca vazio quando <see cref="Severity"/> ≥ Info (invariante I2). <b>Vazio</b> quando
    /// <c>Calm</c>: "o que vem a seguir" mora em <see cref="NextUp"/>, e este campo é reservado
    /// para o que exige ação. Barra calma tem esta área em branco, e isso é o objetivo.
    /// </summary>
    public string Reason { get; init; } = "";

    /// <summary>
    /// A situação temporal: livre, em reunião, ou com uma se aproximando. Ocupa o slot esquerdo e
    /// tem <b>vocabulário de cor próprio</b>, separado do alarme (D-012) — verde para livre, azul
    /// em reunião, âmbar se aproximando, vermelho iminente.
    /// </summary>
    public TimeStatus Time { get; init; } = TimeStatus.Unknown;

    /// <summary>
    /// Contagem regressiva para o fim do expediente. <c>null</c> fora da janela de aviso, e o
    /// indicador simplesmente não existe na barra — não é um estado "calmo", é ausência.
    /// </summary>
    public BoundaryStatus? Boundary { get; init; }

    /// <summary>
    /// True quando o nível 3 passou de 5 min sem reconhecimento e deve piscar
    /// âmbar↔vermelho (§1.1). Só faz sentido com <see cref="Severity"/> == Critical.
    /// </summary>
    public bool IsEscalated { get; init; }

    /// <summary>
    /// Se um clique remove este alerta (invariante I3). O clique é o gesto central do produto:
    /// substitui a detecção de presença em call que foi descartada em D-006.
    /// </summary>
    public bool CanAcknowledge { get; init; }

    /// <summary><c>null</c> renderiza <c>—</c>. Nunca cair para o último valor conhecido: mostrar
    /// contador velho como se fosse atual é o pior modo de falha do produto (§0).</summary>
    public int? OpenTasks { get; init; }

    /// <inheritdoc cref="OpenTasks"/>
    public int? UnreadMail { get; init; }

    public DateTimeOffset? LastSyncAt { get; init; }

    /// <summary>A cor efetiva ignora a escala quando estamos offline.</summary>
    public Severity EffectiveSeverity => IsOffline ? Severity.Calm : Severity;
}
