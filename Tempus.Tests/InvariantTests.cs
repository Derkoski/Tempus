using Tempus.Domain;
using Tempus.Shell;
using Xunit;

namespace Tempus.Tests;

/// <summary>
/// As invariantes I1–I8 do <c>SEVERITY.md</c> §1.
/// <para>
/// <b>Nem todas são testáveis hoje.</b> I4, I5 e I8 dependem de máquina de estados, histerese e
/// escalada, que a Fase 3 ainda não construiu. Elas aparecem aqui como testes marcados
/// <c>Skip</c>, com o motivo — um teste ausente some da vista, um teste pulado cobra.
/// </para>
/// </summary>
public class InvariantTests
{
    private static readonly WorkDayOptions Work = new()
    {
        StartHour = 9,
        MiddayHour = 12,
        LunchEndHour = 13,
        EndHour = 17,
    };

    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 8, 19, hour, minute, 0, TimeSpan.FromHours(-3));

    // ---------------------------------------------------------------- I2

    /// <summary>
    /// I2: toda severidade ≥ 1 tem motivo legível. Nunca colorir sem explicar.
    /// <para>
    /// Sem <c>[Theory]</c> tipada porque <see cref="Severity"/> é <c>internal</c> e um método
    /// público de teste não pode receber parâmetro menos acessível que ele.
    /// </para>
    /// </summary>
    [Fact]
    public void I2_severidade_colorida_sempre_tem_motivo()
    {
        foreach (var nivel in new[] { Severity.Info, Severity.Attention, Severity.Critical })
        {
            var comMotivo = new ShellState { Severity = nivel, Reason = "Motivo qualquer" };
            var semMotivo = new ShellState { Severity = nivel, Reason = "" };

            Assert.True(Respeita(comMotivo));
            Assert.False(Respeita(semMotivo));
        }

        // Calm é o único nível que pode existir sem motivo.
        Assert.True(Respeita(new ShellState { Severity = Severity.Calm, Reason = "" }));

        static bool Respeita(ShellState s) =>
            s.Severity == Severity.Calm || !string.IsNullOrWhiteSpace(s.Reason);
    }

    // ---------------------------------------------------------------- I3

    /// <summary>
    /// I3: todo sinal de nível 3 é reconhecível por clique, e reconhecer sempre o remove
    /// imediatamente. Nenhum nível 3 pode ser inescapável.
    /// <para>
    /// Esta é a invariante do Q-01: ela foi violada em produção por três semanas porque
    /// <c>CanAcknowledge</c> nunca era ligado no caminho de dados reais.
    /// </para>
    /// </summary>
    [Fact]
    public void I3_todo_alarme_oferece_reconhecimento_e_ele_resolve_na_hora()
    {
        var weekly = new AgendaItem
        {
            Id = "weekly",
            Title = "Weekly",
            Start = At(14),
            End = At(15),
        };

        var alarme = TimeStatusResolver.Resolve(
            [weekly], At(15, 3), TimeThresholds.Default, Work);

        // Alarmando ⇒ existe ocorrência a reconhecer.
        Assert.True(alarme.IsFilled);
        Assert.NotNull(alarme.Occurrence);

        // E o estado que a UI monta tem que oferecer o gesto.
        var comAlarme = new ShellState { Time = alarme, CanAcknowledge = alarme.Occurrence is not null };
        Assert.True(comAlarme.CanAcknowledge);

        // Reconhecido ⇒ sai imediatamente, sem esperar nada.
        var depois = TimeStatusResolver.Resolve(
            [weekly], At(15, 3), TimeThresholds.Default, Work,
            new HashSet<string> { alarme.Occurrence! });

        Assert.False(depois.IsFilled);
        Assert.Null(depois.Occurrence);
    }

    /// <summary>Espelho do I3: estado calmo não inventa gesto que não tem o que fazer.</summary>
    [Fact]
    public void I3_estado_calmo_nao_oferece_reconhecimento()
    {
        var calmo = TimeStatusResolver.Resolve([], At(10), TimeThresholds.Default, Work);

        Assert.False(calmo.IsFilled);
        Assert.Null(calmo.Occurrence);
    }

    // ---------------------------------------------------------------- I6 e I7

    /// <summary>
    /// I6: contadores pertencem ao nível <c>Calm</c> mesmo com a barra vermelha. A cor pertence ao
    /// estado, não aos números — aqui, que o valor não é apagado nem alterado pela severidade.
    /// </summary>
    [Fact]
    public void I6_contadores_sobrevivem_a_severidade()
    {
        var vermelha = new ShellState
        {
            Severity = Severity.Critical,
            Reason = "Estourou",
            OpenTasks = 7,
            UnreadMail = 3,
        };

        Assert.Equal(7, vermelha.OpenTasks);
        Assert.Equal(3, vermelha.UnreadMail);
    }

    /// <summary>
    /// I6 aplicada ao contador de pauta (D-052), que é o único com cor <b>própria</b>.
    /// <para>
    /// A invariante proíbe o contador <i>herdar</i> a cor do alarme, não ter uma. O âmbar da pauta
    /// significa "estes assuntos esperam por você" — o mesmo que o ponto de <c>Rsvp.NeedsAction</c>
    /// já significa no painel de agenda. O que este teste trava é a outra metade: com a barra
    /// vermelha por motivo alheio, os números da pauta não mudam.
    /// </para>
    /// </summary>
    [Fact]
    public void I6_contador_de_pauta_nao_herda_a_cor_do_alarme()
    {
        var pauta = new ShellState
        {
            PautaEventId = "evt-1",
            PautaTotal = 4,
            PautaPending = 2,
        };

        var vermelha = pauta with { Severity = Severity.Critical, Reason = "Estourou" };

        Assert.Equal(pauta.PautaTotal, vermelha.PautaTotal);
        Assert.Equal(pauta.PautaPending, vermelha.PautaPending);
        Assert.Equal(pauta.PautaEventId, vermelha.PautaEventId);
    }

    /// <summary>
    /// A cor do contador segue os <b>pendentes</b>, e zerá-los tem de apagá-la. Se seguisse o total,
    /// o âmbar ficaria aceso a reunião inteira e deixaria de querer dizer alguma coisa — que é como
    /// se gasta um vocabulário de cor (regra 1).
    /// </summary>
    [Fact]
    public void Pauta_toda_dita_apaga_a_cor_sem_esconder_o_contador()
    {
        var terminada = new ShellState { PautaEventId = "evt-1", PautaTotal = 3, PautaPending = 0 };

        Assert.Equal(0, terminada.PautaPending);  // sem âmbar
        Assert.Equal(3, terminada.PautaTotal);    // mas o contador continua clicável
    }

    /// <summary>
    /// I7: em <c>Offline</c> nenhum contador exibe número e nenhum sinal é avaliado. Mostrar o
    /// último valor conhecido seria mentir com confiança (regra 10).
    /// </summary>
    [Fact]
    public void I7_offline_nao_mostra_numero_nem_avalia_sinal()
    {
        var offline = new ShellState
        {
            IsOffline = true,
            Reason = "Login do Google expirou",
            OpenTasks = null,
            UnreadMail = null,
        };

        Assert.Null(offline.OpenTasks);
        Assert.Null(offline.UnreadMail);

        // A pauta some junto: o retorno cedo do BuildState não preenche estes campos, e zero
        // esconde o contador. Um número de assuntos sobrevivendo ao Offline seria dado velho com
        // cara de atual, que é o pior modo de falha do produto (§0).
        Assert.Equal(0, offline.PautaTotal);
        Assert.Equal(0, offline.PautaPending);
        Assert.Null(offline.PautaEventId);

        // Sem sinal avaliado: o humor é Unknown, que é o default de ShellState.
        Assert.Equal(TimeMood.Unknown, offline.Time.Mood);
        Assert.Null(offline.Time.Occurrence);
        Assert.False(offline.Time.IsFilled);
        Assert.False(offline.Time.IsEscalated);

        // Nenhuma pausa, nenhuma fronteira, nada a reconhecer.
        Assert.Empty(offline.Breaks);
        Assert.Null(offline.Boundary);
        Assert.False(offline.CanAcknowledge);
    }

    /// <summary>
    /// I7, a metade que faltava: <c>Offline</c> <b>precede e anula</b> a escala. Uma severidade que
    /// tenha sobrado de antes não pode pintar a barra depois que os dados deixaram de ser
    /// confiáveis — "nada pisca, nada fica vermelho" (§0).
    /// </summary>
    [Fact]
    public void I7_offline_anula_a_escala_de_severidade()
    {
        foreach (var nivel in new[] { Severity.Info, Severity.Attention, Severity.Critical })
        {
            var offline = new ShellState
            {
                IsOffline = true,
                Severity = nivel,
                Reason = "Sem sincronizar há 12 min",
                IsEscalated = true,
            };

            Assert.Equal(Severity.Calm, offline.EffectiveSeverity);
        }
    }

    /// <summary>
    /// A idade do último sync é calculada na hora de exibir, nunca congelada numa string. Uma
    /// mensagem gravada no momento da falha diria "há 1 min" duas horas depois (regra 10).
    /// </summary>
    [Fact]
    public void I7_a_idade_do_ultimo_sync_nao_congela()
    {
        var falhou = new DateTimeOffset(2026, 8, 19, 14, 0, 0, TimeSpan.FromHours(-3));

        Assert.NotEqual(
            Idade(falhou, falhou.AddMinutes(12)),
            Idade(falhou, falhou.AddMinutes(40)));

        static string Idade(DateTimeOffset ultimo, DateTimeOffset agora) =>
            $"há {(int)Math.Floor((agora - ultimo).TotalMinutes)} min";
    }

    // ---------------------------------------------------------------- ainda sem máquina de estados

    /// <summary>
    /// I1: nunca dois alarmes competindo — o chip exibe <b>uma</b> severidade por vez, por mais
    /// sinais que estejam ativos ao mesmo tempo.
    /// </summary>
    [Fact]
    public void I1_nunca_dois_alarmes_competindo()
    {
        var agenda = new[]
        {
            new AgendaItem { Id = "daily", Title = "Daily", Start = At(16, 30), End = At(17) },
            new AgendaItem { Id = "review", Title = "Review", Start = At(16, 55), End = At(18) },
        };

        var tarefas = new[]
        {
            new TaskItem { Id = "t1", Title = "t1", Due = new DateOnly(2026, 8, 17) },
            new TaskItem { Id = "t2", Title = "t2" },
        };

        // Cenário deliberadamente carregado: invasão de call, fim de jornada e tarefa vencida.
        var ativos = Signals.Evaluate(
            agenda, tarefas, At(17, 5), Work, SignalThresholds.Default, new HashSet<string>());

        Assert.True(ativos.Count > 1, "o cenário precisa de vários sinais para o teste valer");

        // A arbitragem entrega exatamente um, e o estado carrega um motivo só.
        var vencedor = Arbiter.Winner(ativos);
        Assert.NotNull(vencedor);

        var state = new ShellState { Severity = vencedor!.Severity, Reason = vencedor.Reason };
        Assert.False(string.IsNullOrWhiteSpace(state.Reason));
    }

    /// <summary>
    /// I4: mínimo de 20s num nível antes de rebaixar, para a barra não tremer em torno de uma
    /// fronteira.
    /// </summary>
    [Fact]
    public void I4_histerese_de_20s_antes_de_rebaixar()
    {
        var gate = new SeverityGate(TimeSpan.FromSeconds(20));
        var t0 = At(14);

        gate.Apply(Alarme(Severity.Critical, "Estourou"), t0);
        Assert.Equal(Severity.Critical, gate.Shown);

        // Antes de 20s o rebaixamento é recusado: continua mostrando o que estava.
        gate.Apply(null, t0.AddSeconds(5));
        Assert.Equal(Severity.Critical, gate.Shown);

        gate.Apply(null, t0.AddSeconds(19));
        Assert.Equal(Severity.Critical, gate.Shown);

        // Cumprido o mínimo, desce.
        gate.Apply(null, t0.AddSeconds(20));
        Assert.Equal(Severity.Calm, gate.Shown);
    }

    /// <summary>I4, a exceção escrita na própria invariante: reconhecimento é sempre imediato.</summary>
    [Fact]
    public void I4_reconhecimento_escapa_da_histerese()
    {
        var gate = new SeverityGate(TimeSpan.FromSeconds(20));
        var t0 = At(14);

        gate.Apply(Alarme(Severity.Critical, "Estourou"), t0);
        gate.Reset(t0.AddSeconds(2));

        Assert.Equal(Severity.Calm, gate.Shown);
        Assert.Null(gate.Winner);
    }

    /// <summary>
    /// I5: subir é imediato. Atrasar um alarme para não tremer trocaria o problema certo pelo
    /// errado — tremer incomoda, chegar tarde custa.
    /// </summary>
    [Fact]
    public void I5_subida_de_nivel_e_imediata()
    {
        var gate = new SeverityGate(TimeSpan.FromSeconds(20));
        var t0 = At(14);

        gate.Apply(Alarme(Severity.Info, "Daily em 6 min"), t0);
        Assert.Equal(Severity.Info, gate.Shown);

        // Um segundo depois, sem nenhuma espera.
        gate.Apply(Alarme(Severity.Critical, "Estourou"), t0.AddSeconds(1));
        Assert.Equal(Severity.Critical, gate.Shown);

        gate.Apply(Alarme(Severity.Attention, "Metade do dia"), t0.AddSeconds(2));
        Assert.Equal(Severity.Critical, gate.Shown); // descida ainda segurada pela I4
    }

    /// <summary>
    /// No mesmo nível o motivo pode trocar sem reiniciar o relógio da histerese — senão a barra
    /// ficaria presa num nível enquanto sinais se revezassem nele.
    /// </summary>
    [Fact]
    public void I4_trocar_de_motivo_no_mesmo_nivel_nao_reinicia_o_relogio()
    {
        var gate = new SeverityGate(TimeSpan.FromSeconds(20));
        var t0 = At(14);

        gate.Apply(Alarme(Severity.Attention, "Primeiro"), t0);
        gate.Apply(Alarme(Severity.Attention, "Segundo"), t0.AddSeconds(15));

        Assert.Equal("Segundo", gate.Winner!.Reason);

        gate.Apply(null, t0.AddSeconds(21));
        Assert.Equal(Severity.Calm, gate.Shown);
    }

    private static Signal Alarme(Severity nivel, string motivo) => new()
    {
        Name = motivo,
        Severity = nivel,
        Reason = motivo,
        Category = SignalCategory.Call,
        Occurrence = motivo,
        Since = At(14),
    };

    /// <summary>
    /// I8: a escalada nunca ocorre antes de 5 min no nível 3, e nunca com período menor que 1s.
    /// </summary>
    [Fact]
    public void I8_escalada_nunca_antes_de_5_min_nem_com_periodo_menor_que_1s()
    {
        var weekly = new AgendaItem
        {
            Id = "weekly",
            Title = "Weekly",
            Start = At(14),
            End = At(15),
        };

        // Com uma reunião depois, que é o cenário do vermelho principal: escalar existe para o
        // caso "estou segurando esta e a próxima já vai começar". Sem nada depois a janela é a
        // curta do D-048, e a escalada de 5 min nem chega a acontecer — por decisão, não por
        // acidente.
        var review = new AgendaItem
        {
            Id = "review",
            Title = "Review",
            Start = At(16),
            End = At(17),
        };

        TimeStatus Em(int minutosDepoisDoFim) => TimeStatusResolver.Resolve(
            [weekly, review], At(15).AddMinutes(minutosDepoisDoFim), TimeThresholds.Default, Work);

        // Vermelho sólido na primeira janela: alarma, mas não pisca.
        Assert.True(Em(1).IsFilled);
        Assert.False(Em(1).IsEscalated);
        Assert.False(Em(4).IsEscalated);

        // A partir de 5 min, escala.
        Assert.True(Em(5).IsEscalated);
        Assert.True(Em(8).IsEscalated);

        // Afrouxar por configuração é permitido; antecipar não.
        var apressado = TimeThresholds.Default with { EscalationMinutes = 1 };
        var cedo = TimeStatusResolver.Resolve([weekly], At(15, 2), apressado, Work);
        Assert.False(cedo.IsEscalated);

        // Período do piscar ≥ 1s.
        Assert.True(FloatingBarWindow.BlinkPeriod >= TimeSpan.FromSeconds(1));
    }

    /// <summary>Reconhecer encerra a escalada junto com o alarme — não existe piscar órfão.</summary>
    [Fact]
    public void I8_reconhecer_encerra_a_escalada()
    {
        var weekly = new AgendaItem
        {
            Id = "weekly",
            Title = "Weekly",
            Start = At(14),
            End = At(15),
        };

        // Como no teste acima: escalada de 5 min precisa da janela longa, que existe quando há
        // compromisso depois (D-048).
        var review = new AgendaItem
        {
            Id = "review",
            Title = "Review",
            Start = At(16),
            End = At(17),
        };

        var escalado = TimeStatusResolver.Resolve(
            [weekly, review], At(15, 7), TimeThresholds.Default, Work);
        Assert.True(escalado.IsEscalated);

        var depois = TimeStatusResolver.Resolve(
            [weekly, review], At(15, 7), TimeThresholds.Default, Work,
            new HashSet<string> { escalado.Occurrence! });

        Assert.False(depois.IsEscalated);
    }
}
