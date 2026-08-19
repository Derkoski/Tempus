using Tempus.Domain;
using Xunit;

namespace Tempus.Tests;

/// <summary>A tela como servidor + intenções pendentes, e a reversão quando a intenção falha.</summary>
public class TaskProjectionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 19, 10, 0, 0, TimeSpan.FromHours(-3));

    private static TaskItem Task(string id, string title = "tarefa", bool done = false) =>
        new() { Id = id, ListId = "L", Title = title, IsCompleted = done };

    private static PendingWrite Write(WriteKind kind, string? taskId = null, string? title = null) =>
        PendingWrite.For(kind, Now) with { TaskId = taskId, ListId = "L", Title = title };

    private static PendingWrite Failed(PendingWrite write) =>
        write with { State = WriteState.Failed, Failure = "Sem conexão com o Google" };

    private static IReadOnlyList<string> OpenTitles(IReadOnlyList<TaskRow> rows) =>
        [.. rows.Where(r => !r.Item.IsCompleted).Select(r => r.Item.Title)];

    // ---------------------------------------------------------------- efeito otimista

    [Fact]
    public void Concluir_pendente_ja_sai_da_lista_aberta()
    {
        var rows = TaskProjection.Apply([Task("t1")], [Write(WriteKind.Complete, "t1")]);

        Assert.Empty(OpenTitles(rows));
        Assert.True(rows.Single().IsPending);
    }

    [Fact]
    public void Excluir_pendente_some_da_lista()
    {
        var rows = TaskProjection.Apply([Task("t1"), Task("t2")], [Write(WriteKind.Delete, "t1")]);

        Assert.Equal("t2", rows.Single().Item.Id);
    }

    [Fact]
    public void Criar_pendente_aparece_na_hora_com_linha_provisoria()
    {
        var criar = Write(WriteKind.Create, title: "Ligar para o cliente");
        var rows = TaskProjection.Apply([], [criar]);

        var linha = rows.Single();
        Assert.Equal("Ligar para o cliente", linha.Item.Title);
        Assert.Equal(criar.ProvisionalId, linha.Item.Id);
        Assert.True(linha.IsPending);
    }

    // ---------------------------------------------------------------- reversão

    /// <summary>
    /// A reversão não tem código próprio: o efeito otimista só vale enquanto pendente, então
    /// falhar já devolve a linha à verdade do servidor.
    /// </summary>
    [Fact]
    public void Concluir_que_falhou_devolve_a_tarefa_para_a_lista()
    {
        var rows = TaskProjection.Apply([Task("t1")], [Failed(Write(WriteKind.Complete, "t1"))]);

        var linha = rows.Single();
        Assert.False(linha.Item.IsCompleted);
        Assert.True(linha.HasFailed);
    }

    [Fact]
    public void Excluir_que_falhou_traz_a_tarefa_de_volta()
    {
        var rows = TaskProjection.Apply([Task("t1")], [Failed(Write(WriteKind.Delete, "t1"))]);

        Assert.Single(rows);
        Assert.True(rows.Single().HasFailed);
    }

    /// <summary>
    /// O defeito que motivou a fatia inteira: o título digitado só existe dentro da intenção. Se a
    /// linha sumisse ao falhar, o dado se perderia — que é exatamente o que acontecia antes.
    /// </summary>
    [Fact]
    public void Criar_que_falhou_mantem_o_titulo_visivel()
    {
        var rows = TaskProjection.Apply([], [Failed(Write(WriteKind.Create, title: "Não me perca"))]);

        var linha = rows.Single();
        Assert.Equal("Não me perca", linha.Item.Title);
        Assert.True(linha.HasFailed);
    }

    // ---------------------------------------------------------------- desfazer conclusão

    [Fact]
    public void Reabrir_pendente_devolve_a_tarefa_a_lista_aberta()
    {
        var rows = TaskProjection.Apply(
            [Task("t1", done: true)], [Write(WriteKind.Reopen, "t1")]);

        var linha = rows.Single();
        Assert.False(linha.Item.IsCompleted);
        Assert.True(linha.IsPending);
    }

    [Fact]
    public void Reabrir_que_falhou_deixa_a_tarefa_concluida_de_novo()
    {
        var rows = TaskProjection.Apply(
            [Task("t1", done: true)], [Failed(Write(WriteKind.Reopen, "t1"))]);

        var linha = rows.Single();
        Assert.True(linha.Item.IsCompleted);
        Assert.True(linha.HasFailed);
    }

    /// <summary>
    /// Assimetria de propósito: para <c>Complete</c>, sumir do retrato confirma; para
    /// <c>Reopen</c>, não. Uma concluída pode ter caído da janela de sete dias em vez de ter
    /// voltado a aberta, e confirmar aí seria dizer "pronto" sobre algo que talvez não aconteceu.
    /// </summary>
    [Fact]
    public void Reabrir_so_confirma_vendo_a_tarefa_aberta_no_retrato()
    {
        var write = Write(WriteKind.Reopen, "t1");

        Assert.False(TaskProjection.IsConfirmed(write, [Task("t1", done: true)]));
        Assert.False(TaskProjection.IsConfirmed(write, []));
        Assert.True(TaskProjection.IsConfirmed(write, [Task("t1")]));
    }

    // ---------------------------------------------------------------- confirmação

    [Fact]
    public void Concluir_confirma_quando_a_tarefa_sai_do_retrato()
    {
        var write = Write(WriteKind.Complete, "t1");

        Assert.False(TaskProjection.IsConfirmed(write, [Task("t1")]));
        Assert.True(TaskProjection.IsConfirmed(write, []));
        Assert.True(TaskProjection.IsConfirmed(write, [Task("t1", done: true)]));
    }

    [Fact]
    public void Excluir_confirma_quando_a_tarefa_some()
    {
        var write = Write(WriteKind.Delete, "t1");

        Assert.False(TaskProjection.IsConfirmed(write, [Task("t1")]));
        Assert.True(TaskProjection.IsConfirmed(write, [Task("t2")]));
    }

    /// <summary>
    /// Sem isto, um restart depois de uma criação cuja resposta se perdeu criaria a tarefa duas
    /// vezes. Duplicata incomoda até alguém apagar na mão; refazer um clique, não.
    /// </summary>
    [Fact]
    public void Criar_confirma_por_titulo_e_nao_duplica_apos_restart()
    {
        var write = Write(WriteKind.Create, title: "Revisar contrato");

        Assert.False(TaskProjection.IsConfirmed(write, [Task("t1", "Outra coisa")]));
        Assert.True(TaskProjection.IsConfirmed(write, [Task("t9", "  revisar contrato ")]));
    }

    // ---------------------------------------------------------------- coerência

    /// <summary>
    /// A barra conta pela mesma projeção que o painel desenha. Sem isto o chip diria "2 tarefas
    /// abertas" enquanto a lista logo acima mostra uma.
    /// </summary>
    [Fact]
    public void A_contagem_acompanha_o_que_a_lista_mostra()
    {
        IReadOnlyList<TaskItem> server = [Task("t1"), Task("t2"), Task("t3")];
        var pendentes = new[] { Write(WriteKind.Complete, "t1"), Write(WriteKind.Delete, "t2") };

        var rows = TaskProjection.Apply(server, pendentes);
        var abertas = rows.Count(r => !r.Item.IsCompleted);

        Assert.Equal(1, abertas);
        Assert.Equal(OpenTitles(rows).Count, abertas);
    }

    [Fact]
    public void Sem_pendencias_a_projecao_e_o_proprio_retrato()
    {
        var rows = TaskProjection.Apply([Task("t1"), Task("t2")], []);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Null(r.Write));
    }

    /// <summary>Intenção órfã — a tarefa sumiu do outro lado — não pode inventar linha.</summary>
    [Fact]
    public void Intencao_sobre_tarefa_inexistente_nao_cria_linha()
    {
        var rows = TaskProjection.Apply([Task("t1")], [Write(WriteKind.Complete, "sumida")]);

        Assert.Single(rows);
        Assert.Null(rows.Single().Write);
    }
}
