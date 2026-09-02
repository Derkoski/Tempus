using Tempus.Domain;
using Xunit;

namespace Tempus.Tests;

/// <summary>
/// A pauta de reunião (D-052).
/// <para>
/// O risco que estes testes cercam não é a lista aparecer errada — é ela aparecer <b>na reunião
/// errada</b>, com cara de certa. O vínculo mora nas notas de uma tarefa que o usuário pode editar
/// pelo celular, então nota torta é situação normal, não excepcional.
/// </para>
/// </summary>
public class PautaTests
{
    private static TaskItem Assunto(
        string id, string? eventId, string titulo = "assunto", bool dito = false) => new()
    {
        Id = id,
        ListId = "lista-pauta",
        Title = titulo,
        Notes = eventId is null ? null : Pauta.NotesFor(eventId),
        IsCompleted = dito,
    };

    // ---------------------------------------------------------------- o vínculo

    [Fact]
    public void Le_de_volta_o_evento_que_escreveu()
    {
        var item = Assunto("a", "evt-123");

        Assert.Equal("evt-123", Pauta.EventIdOf(item));
    }

    /// <summary>
    /// A lista é editável pelo app do Google e pelo celular. Nota apagada, escrita à mão ou de
    /// outra origem não pode derrubar a leitura nem, pior, casar com alguma reunião por acidente.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("lembrar de levar o notebook")]
    [InlineData("evento:")]
    [InlineData("evento:   ")]
    public void Nota_sem_vinculo_legivel_nao_pertence_a_reuniao_nenhuma(string? notas)
    {
        var item = new TaskItem { Id = "a", Title = "x", Notes = notas };

        Assert.Null(Pauta.EventIdOf(item));
    }

    /// <summary>
    /// O usuário pode escrever o que quiser junto. O vínculo é uma linha, e as outras são dele.
    /// </summary>
    [Fact]
    public void Vinculo_convive_com_texto_livre_do_usuario()
    {
        var item = new TaskItem
        {
            Id = "a",
            Title = "x",
            Notes = "anotei no caderno também\nevento:evt-9\nver com o Rafael antes",
        };

        Assert.Equal("evt-9", Pauta.EventIdOf(item));
    }

    // ---------------------------------------------------------------- o recorte

    [Fact]
    public void Assunto_de_outra_reuniao_nao_vaza()
    {
        IReadOnlyList<TaskItem> pauta =
        [
            Assunto("a", "evt-1", "meu"),
            Assunto("b", "evt-2", "de outra"),
            Assunto("c", null, "solto"),
        ];

        var minha = Pauta.For(pauta, "evt-1");

        Assert.Single(minha);
        Assert.Equal("meu", minha[0].Title);
    }

    /// <summary>
    /// Sem reunião nomeada não há pauta — e é o caso comum: a maior parte do dia não tem reunião
    /// em curso, e o contador da barra não pode inventar uma.
    /// </summary>
    [Fact]
    public void Sem_reuniao_nao_ha_pauta()
    {
        IReadOnlyList<TaskItem> pauta = [Assunto("a", "evt-1")];

        Assert.Empty(Pauta.For(pauta, null));
        Assert.Equal(0, Pauta.Pending(pauta, null));
    }

    /// <summary>Riscado fica na lista, mas no fim: ele é a prova do que já foi dito.</summary>
    [Fact]
    public void Dito_vai_para_o_fim_sem_sumir()
    {
        IReadOnlyList<TaskItem> pauta =
        [
            Assunto("a", "e", "primeiro", dito: true),
            Assunto("b", "e", "segundo"),
            Assunto("c", "e", "terceiro"),
        ];

        var ordem = Pauta.For(pauta, "e").Select(t => t.Title).ToList();

        Assert.Equal(["segundo", "terceiro", "primeiro"], ordem);
    }

    // ---------------------------------------------------------------- a contagem da barra

    [Fact]
    public void Pending_conta_so_o_que_falta_dizer()
    {
        IReadOnlyList<TaskItem> pauta =
        [
            Assunto("a", "e", dito: true),
            Assunto("b", "e"),
            Assunto("c", "e"),
            Assunto("d", "outra"),
        ];

        Assert.Equal(2, Pauta.Pending(pauta, "e"));
    }

    /// <summary>
    /// Tudo riscado zera o contador — é o que apaga o âmbar da barra e devolve o normal. Se
    /// contasse o total, a cor ficaria acesa a reunião inteira e deixaria de querer dizer algo.
    /// </summary>
    [Fact]
    public void Tudo_dito_zera_a_contagem()
    {
        IReadOnlyList<TaskItem> pauta = [Assunto("a", "e", dito: true), Assunto("b", "e", dito: true)];

        Assert.Equal(0, Pauta.Pending(pauta, "e"));
        Assert.Equal(2, Pauta.For(pauta, "e").Count);
    }

    [Fact]
    public void CountsFor_agrupa_por_reuniao_e_ignora_os_soltos()
    {
        IReadOnlyList<TaskRow> rows =
        [
            new() { Item = Assunto("a", "e1") },
            new() { Item = Assunto("b", "e1", dito: true) },
            new() { Item = Assunto("c", "e2") },
            new() { Item = Assunto("d", null) },
        ];

        var counts = Pauta.CountsFor(rows);

        Assert.Equal(2, counts["e1"]);
        Assert.Equal(1, counts["e2"]);
        Assert.Equal(2, counts.Count);
    }

    // ---------------------------------------------------------------- o corte que protege o resto

    /// <summary>
    /// <b>O teste que justifica a funcionalidade.</b> Assunto na contagem de tarefas é exatamente o
    /// problema que ela existe para resolver — inflaria o chip da barra, o painel S2 e o sinal
    /// <c>TaskOverdue</c> do §2.3.
    /// </summary>
    [Fact]
    public void Assunto_nunca_conta_como_tarefa()
    {
        IReadOnlyList<TaskItem> tudo =
        [
            new() { Id = "t1", ListId = "minhas", Title = "tarefa de verdade" },
            Assunto("p1", "e1"),
            Assunto("p2", "e1"),
        ];

        var (tasks, pauta) = Pauta.Split(tudo, "lista-pauta");

        Assert.Single(tasks);
        Assert.Equal("tarefa de verdade", tasks[0].Title);
        Assert.Equal(2, pauta.Count);
    }

    /// <summary>
    /// Quem nunca adicionou um assunto não tem lista de pauta, e nesse estado nada muda: toda
    /// tarefa continua sendo tarefa.
    /// </summary>
    [Fact]
    public void Sem_lista_de_pauta_tudo_e_tarefa()
    {
        IReadOnlyList<TaskItem> tudo = [new() { Id = "t1", Title = "tarefa" }];

        var (tasks, pauta) = Pauta.Split(tudo, null);

        Assert.Single(tasks);
        Assert.Empty(pauta);
    }

    // ---------------------------------------------------------------- escrita otimista

    /// <summary>
    /// A linha provisória de uma criação precisa carregar as notas, senão um assunto recém-digitado
    /// sumiria do painel até o Google confirmar — quebrando a promessa do D-029 justamente no
    /// momento em que o usuário está no meio de uma reunião.
    /// </summary>
    [Fact]
    public void Assunto_recem_criado_ja_aparece_na_pauta()
    {
        var write = PendingWrite.For(WriteKind.Create, DateTimeOffset.Now) with
        {
            Title = "falar do orçamento",
            Notes = Pauta.NotesFor("e1"),
            IsPauta = true,
        };

        var rows = TaskProjection.Apply([], [write]);
        var minha = Pauta.RowsFor(rows, "e1");

        Assert.Single(minha);
        Assert.Equal("falar do orçamento", minha[0].Item.Title);
        Assert.True(minha[0].IsPending);
    }

    /// <summary>
    /// O vínculo é o <b>id do evento</b>, não a ocorrência do §7 — que embute início e fim. A
    /// diferença aparece aqui: remarcar a reunião não pode apagar a pauta preparada para ela.
    /// </summary>
    [Fact]
    public void Remarcar_a_reuniao_preserva_o_vinculo()
    {
        var antes = new AgendaItem
        {
            Id = "evt-1",
            Title = "Weekly",
            Start = DateTimeOffset.Parse("2026-09-02T14:00:00-03:00"),
            End = DateTimeOffset.Parse("2026-09-02T15:00:00-03:00"),
        };

        var depois = antes with
        {
            Start = antes.Start.AddMinutes(15),
            End = antes.End.AddMinutes(15),
        };

        IReadOnlyList<TaskItem> pauta = [Assunto("a", antes.Id)];

        Assert.Single(Pauta.For(pauta, depois.Id));

        // E a prova de que a alternativa teria falhado: a ocorrência do §7 muda com o horário.
        Assert.NotEqual(Signal.OccurrenceFor("X", antes), Signal.OccurrenceFor("X", depois));
    }
}
