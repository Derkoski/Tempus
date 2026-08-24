using Tempus.Domain;

namespace Tempus.Shell;

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
    /// O que vem depois de hoje, para o dia que já acabou não deixar a barra vazia.
    /// Cede lugar assim que houver um alarme — informação de conforto nunca disputa com alerta.
    /// </summary>
    public string? Lookahead { get; init; }

    /// <summary>
    /// As pausas de descanso ainda de pé hoje. Vazio quando a funcionalidade está desligada — que
    /// é o padrão de instalação — ou quando a folga do dia foi dispensada.
    /// </summary>
    public IReadOnlyList<BreakSlot> Breaks { get; init; } = [];

    /// <summary>
    /// A funcionalidade está ligada mas a folga de hoje foi dispensada. Distinto de
    /// <see cref="Breaks"/> vazio por falta de espaço na agenda: só este habilita "Restaurar
    /// pausa de hoje" no menu.
    /// </summary>
    public bool BreaksDismissed { get; init; }

    /// <summary>
    /// Identidade da ocorrência do sinal que venceu a arbitragem (§4), para o clique poder
    /// reconhecê-lo. <c>null</c> quando o chip está vazio.
    /// </summary>
    public string? SignalOccurrence { get; init; }

    /// <summary>
    /// O chip está repetindo o que o slot de tempo já conta, e por isso não é desenhado (D-033).
    /// <para>
    /// <b>Só afeta o desenho.</b> A severidade, o reconhecimento e o toast continuam valendo — é
    /// por isso que a decisão chega como uma flag de apresentação em vez de o sinal ser removido
    /// da arbitragem.
    /// </para>
    /// </summary>
    public bool ChipRepeatsTime { get; init; }

    /// <summary>
    /// As reuniões concorrentes quando há sobreposição sem escolha feita (§8). Vazio no caso
    /// normal. Com duas ou mais, o clique no chip abre o seletor em vez de reconhecer — é uma
    /// pergunta, e responder vale mais que silenciar.
    /// </summary>
    public IReadOnlyList<AgendaItem> ActiveEventChoices { get; init; } = [];

    /// <summary>
    /// A funcionalidade de pausas está ligada. Distinto de ter pausa hoje: governa se os gestos
    /// aparecem no menu, porque "tirar agora" faz sentido mesmo sem nenhuma pausa planejada de pé.
    /// </summary>
    public bool BreaksEnabled { get; init; }

    /// <summary>Períodos que o usuário marcou como já tirados, clicando no slot.</summary>
    public IReadOnlyList<BreakPeriod> BreaksTaken { get; init; } = [];

    public bool IsBreakTaken(BreakPeriod period) => BreaksTaken.Contains(period);

    /// <summary>
    /// A pausa que o slot da barra deve mostrar: a que está em curso, ou a próxima ainda por vir.
    /// <para>
    /// Uma pausa já passada devolve <c>null</c> em vez da anterior — não há o que oferecer sobre
    /// ela, e insistir viraria cobrança sobre algo que o app não tem como saber se aconteceu.
    /// </para>
    /// </summary>
    public BreakSlot? NextBreak(DateTimeOffset now) =>
        Breaks.FirstOrDefault(b => b.IsRunningAt(now))
        ?? Breaks.Where(b => b.Start > now).OrderBy(b => b.Start).FirstOrDefault();

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

    /// <summary>
    /// A ocorrência da reunião em curso que o gesto "encerrei esta reunião" marcaria (D-041), ou
    /// <c>null</c> quando não há reunião correndo. É o irmão do <see cref="CanAcknowledge"/>: um
    /// diz "eu vi", o outro diz "já saí", e são coisas diferentes.
    /// </summary>
    public string? LeavableOccurrence { get; init; }

    /// <summary>
    /// O título da reunião encerrada que ainda estaria em curso, para o menu oferecer o desfazer.
    /// <c>null</c> quando não há o que reabrir — inclusive depois que o horário dela passa, quando
    /// reabrir já não teria efeito nenhum.
    /// </summary>
    public string? ReopenableTitle { get; init; }

    /// <summary>A ocorrência que o desfazer removeria. Anda junto do <see cref="ReopenableTitle"/>.</summary>
    public string? ReopenableOccurrence { get; init; }

    /// <summary><c>null</c> renderiza <c>—</c>. Nunca cair para o último valor conhecido: mostrar
    /// contador velho como se fosse atual é o pior modo de falha do produto (§0).</summary>
    public int? OpenTasks { get; init; }

    /// <inheritdoc cref="OpenTasks"/>
    public int? UnreadMail { get; init; }

    public DateTimeOffset? LastSyncAt { get; init; }

    /// <summary>A cor efetiva ignora a escala quando estamos offline.</summary>
    public Severity EffectiveSeverity => IsOffline ? Severity.Calm : Severity;
}
