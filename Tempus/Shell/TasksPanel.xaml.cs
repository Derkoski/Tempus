using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tempus.Domain;
using Tempus.Interop;

namespace Tempus.Shell;

/// <summary>
/// Superfície S2 do SPEC: as tarefas abertas, agrupadas por vencimento.
/// <para>
/// Desenha <see cref="TaskRow"/>, não <c>TaskItem</c>: cada linha pode carregar uma intenção de
/// escrita que ainda não subiu. Pendente aparece esmaecida — já vale, mas não está confirmada;
/// falhada volta ao que o servidor tem e cobra uma decisão (D-029).
/// </para>
/// <para>
/// Editar título e vencimento é a próxima fatia da Fase 4.
/// </para>
/// </summary>
internal partial class TasksPanel : Window
{
    private readonly PanelHost _host;
    private readonly Palette _palette;

    /// <summary>
    /// Estado da seção de concluídas. Mora no painel, e não em disco: ele responde "o que está
    /// aberto", e cada abertura deve voltar a responder isso. Mas sobrevive aos redesenhos, senão
    /// um sync recolheria a seção embaixo do clique do usuário.
    /// </summary>
    private bool _showCompleted;

    private IReadOnlyList<TaskRow> _last = [];

    public TasksPanel(Palette palette, NativeMethods.RECT anchor)
    {
        _palette = palette;
        InitializeComponent();

        _host = new PanelHost(this) { Anchor = anchor };

        // Quem decide o ciclo de vida é a superfície, não o painel — ela precisa saber que
        // fechou para que o próximo clique no contador abra em vez de alternar.
        _host.CloseRequested += (_, _) => Dismissed?.Invoke(this, EventArgs.Empty);

        ApplyPalette();

        NewTaskBox.TextChanged += (_, _) =>
            Placeholder.Visibility = string.IsNullOrEmpty(NewTaskBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

        NewTaskBox.KeyDown += OnNewTaskKeyDown;
    }

    /// <summary>Usuário marcou/desmarcou uma tarefa. Carrega o id.</summary>
    public event EventHandler<string>? TaskToggled;

    /// <summary>
    /// Novo vencimento de uma tarefa (D-042). Data nula quer dizer <b>apagar</b> — quem recebe
    /// precisa dessa distinção, porque no caminho da API apagar e "não mexer" se parecem.
    /// </summary>
    public event EventHandler<(string Id, DateOnly? Due)>? TaskRescheduled;

    /// <summary>Novo título de uma tarefa (D-045). Nunca vazio — vazio equivale a desistir.</summary>
    public event EventHandler<(string Id, string Title)>? TaskRenamed;

    /// <summary>Usuário criou uma tarefa. Carrega o título.</summary>
    public event EventHandler<string>? TaskCreated;

    /// <summary>Usuário confirmou a exclusão. Carrega o id. Não tem volta (D-024).</summary>
    public event EventHandler<string>? TaskDeleted;

    /// <summary>"Tentar de novo" numa escrita que falhou. Carrega o id da <b>intenção</b>.</summary>
    public event EventHandler<string>? WriteRetried;

    /// <summary>"Deixa pra lá": abandona a intenção. Carrega o id da <b>intenção</b>.</summary>
    public event EventHandler<string>? WriteDiscarded;

    /// <summary>Desmarcar uma concluída: ela volta para a lista de abertas. Carrega o id.</summary>
    public event EventHandler<string>? TaskReopened;

    /// <summary>Perdeu o foco ou levou <c>Esc</c>: a superfície deve fechar este painel.</summary>
    public event EventHandler? Dismissed;

    public void ShowAt(NativeMethods.RECT anchor) => _host.ShowAt(anchor);

    /// <summary>
    /// A tarefa cujo título está sendo editado, ou <c>null</c> (D-045).
    /// <para>
    /// Enquanto vale, atualização <b>vinda de fora</b> é represada. Sem isso o editor seria
    /// destruído no meio da digitação: as linhas são reconstruídas a cada desenho, e o painel
    /// redesenha a cada rodada de sync — a cada 15 s o usuário perderia o que escreveu. Ficar 15 s
    /// com a lista velha durante uma renomeação não custa nada; perder o texto custa a confiança
    /// no gesto.
    /// </para>
    /// </summary>
    private string? _editing;

    /// <summary>
    /// Dado novo chegou de fora — sync, escrita confirmada, painel reaberto.
    /// <para>
    /// <b>Separado do <see cref="Draw"/> de propósito.</b> A primeira versão pôs a guarda de
    /// edição aqui e chamava este mesmo método para <i>abrir</i> o editor: ele desistia na
    /// guarda, o editor nunca aparecia, e <c>_editing</c> ficava preso — a partir dali todo
    /// redesenho era engolido, inclusive o de concluir tarefa, e o painel inteiro parecia morto.
    /// Quem represa e quem desenha não podem ser a mesma porta.
    /// </para>
    /// </summary>
    public void Render(IReadOnlyList<TaskRow> tasks)
    {
        _last = tasks;

        if (_editing is not null) return;

        Draw();
    }

    /// <summary>Redesenha a partir do último retrato conhecido, sempre. Uso interno.</summary>
    private void Draw()
    {
        var tasks = _last;
        var open = tasks.Where(t => !t.Item.IsCompleted).ToList();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var failed = open.Count(r => r.HasFailed);

        HeaderCount.Text = (open.Count, failed) switch
        {
            (0, _) => "nada aberto",

            // O que não subiu vem primeiro no cabeçalho: é a informação que muda o que fazer.
            (_, > 0) => $"{open.Count} · {failed} não salvou",
            (1, _) => "1 tarefa",
            _ => $"{open.Count} tarefas",
        };

        Groups.Children.Clear();

        if (open.Count == 0)
        {
            Groups.Children.Add(new TextBlock
            {
                Text = "Dia limpo. Nada aberto.",
                Margin = new Thickness(6, 14, 6, 18),
                Foreground = new SolidColorBrush(_palette.Muted),
            });
        }
        else
        {
            // A ordem dos buckets é a ordem do enum: vencidas primeiro.
            var buckets = open
                .GroupBy(r => r.Item.Bucket(today))
                .OrderBy(g => g.Key);

            foreach (var bucket in buckets)
            {
                Groups.Children.Add(BuildGroupHeader(bucket.Key, bucket.Count()));

                var ordered = bucket
                    .OrderBy(r => r.Item.Due ?? DateOnly.MaxValue)
                    .ThenBy(r => r.Item.Title);

                foreach (var row in ordered) Groups.Children.Add(BuildRow(row, bucket.Key));
            }
        }

        RenderCompleted([.. tasks.Where(t => t.Item.IsCompleted)]);
        RepositionAfterLayout();
    }

    /// <summary>
    /// A seção de concluídas, recolhida por padrão.
    /// <para>
    /// O painel responde "o que está aberto", e concluída não é resposta para isso — por isso ela
    /// entra atrás de um clique, e não na lista. Mas precisa existir: sem volta, concluir seria um
    /// gesto irreversível de um clique só, ao lado de outro clicável. O ✕ ganhou dois cliques pelo
    /// mesmo motivo (D-024); aqui a saída foi dar a volta em vez de encarecer o gesto (D-030).
    /// </para>
    /// <para>
    /// Ordenadas da mais recente para a mais antiga: a marcada sem querer é a última, e fica no
    /// topo, ao alcance do clique que a desfaz.
    /// </para>
    /// </summary>
    private void RenderCompleted(IReadOnlyList<TaskRow> done)
    {
        if (done.Count == 0) return;

        Groups.Children.Add(BuildCompletedHeader(done.Count));

        if (!_showCompleted) return;

        var ordered = done
            .OrderByDescending(r => r.Item.CompletedAt ?? DateTimeOffset.MinValue)
            .ThenBy(r => r.Item.Title);

        foreach (var row in ordered) Groups.Children.Add(BuildCompletedRow(row));
    }

    private UIElement BuildCompletedHeader(int count)
    {
        var header = new Border
        {
            Padding = new Thickness(6, 10, 6, 4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = _showCompleted ? "Ocultar concluídas" : "Mostrar concluídas para desmarcar",
            Child = new TextBlock
            {
                Text = $"{(_showCompleted ? "▾" : "▸")}  Concluídas  ·  {count}",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(_palette.Muted),
            },
        };

        header.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _showCompleted = !_showCompleted;
            Draw();
        };

        return header;
    }

    /// <summary>
    /// Uma concluída. Clicar desmarca — simétrico à linha aberta, onde clicar marca. O mesmo gesto
    /// nos dois sentidos é o que torna a volta óbvia sem precisar de botão nomeado.
    /// </summary>
    private UIElement BuildCompletedRow(TaskRow entry)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 7, 8, 7),
            Background = Brushes.Transparent,
            Cursor = entry.IsPending ? Cursors.Arrow : Cursors.Hand,
            Opacity = entry.IsPending ? 0.55 : 1.0,
            ToolTip = "Clique para desmarcar e devolver à lista",
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Caixa marcada, desenhada — mesma decisão da aberta: nada de glifo de fonte, para não
        // depender de qual versão do Segoe está instalada.
        var box = new Border
        {
            Width = 15,
            Height = 15,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(_palette.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
            Child = new TextBlock
            {
                Text = "✓",
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(_palette.PanelBackground),
            },
        };
        Grid.SetColumn(box, 0);

        // Mesmo tratamento da linha aberta: reabrir a tarefa certa exige reconhecer qual é.
        var title = new TextBlock
        {
            Text = entry.Item.Title,
            VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = TextDecorations.Strikethrough,
            Foreground = new SolidColorBrush(_palette.Muted),
        }.WrappingToTwoLines();
        Grid.SetColumn(title, 1);

        var status = entry.HasFailed
            ? BuildRetryAction(entry)
            : new TextBlock
            {
                Text = CompletedLabel(entry.Item.CompletedAt),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Foreground = new SolidColorBrush(_palette.Muted),
            };
        Grid.SetColumn(status, 2);

        grid.Children.Add(box);
        grid.Children.Add(title);
        grid.Children.Add(status);
        row.Child = grid;

        if (entry.IsPending) return row;

        var hover = new SolidColorBrush(_palette.RowHover);
        row.MouseEnter += (_, _) => row.Background = hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;

            // Falha pendente de decisão trava o gesto, como na linha aberta: duas intenções
            // conflitantes sobre a mesma tarefa na fila não teriam resposta certa.
            if (entry.HasFailed) return;

            TaskReopened?.Invoke(this, entry.Item.Id);
        };

        return row;
    }

    private static string CompletedLabel(DateTimeOffset? at)
    {
        if (at is not { } when) return "";

        var days = (DateTime.Today - when.LocalDateTime.Date).Days;

        return days switch
        {
            <= 0 => when.ToLocalTime().ToString("HH:mm"),
            1 => "ontem",
            _ => $"há {days} dias",
        };
    }

    /// <summary>
    /// O painel é ancorado pela borda de baixo (fica acima da barra), então mudar de altura move
    /// o topo. Reposicionar precisa esperar o <c>SizeToContent</c> concluir o layout — daí a
    /// prioridade <c>Loaded</c> em vez de chamar direto.
    /// </summary>
    private void RepositionAfterLayout() => Dispatcher.BeginInvoke(
        new Action(() => _host.Reposition()), System.Windows.Threading.DispatcherPriority.Loaded);

    private UIElement BuildGroupHeader(TaskBucket bucket, int count)
    {
        var isOverdue = bucket == TaskBucket.Overdue;

        return new TextBlock
        {
            Text = $"{bucket.Label()}  ·  {count}",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 10, 6, 4),
            Foreground = new SolidColorBrush(isOverdue ? _palette.CriticalBackground : _palette.Muted),
        };
    }

    private UIElement BuildRow(TaskRow entry, TaskBucket bucket)
    {
        var task = entry.Item;

        var row = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 7, 8, 7),
            Background = Brushes.Transparent,

            // Pendente não aceita clique: ela já está a caminho, e um segundo clique enfileiraria
            // a mesma coisa outra vez. A mão vira seta para o cursor dizer isso antes do clique.
            Cursor = entry.IsPending ? Cursors.Arrow : Cursors.Hand,

            // Esmaecida enquanto sobe. Sem cor nova — só menos presença, que é o que "ainda não
            // é definitivo" quer dizer.
            Opacity = entry.IsPending ? 0.55 : 1.0,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // renomear
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // excluir

        // Caixa desenhada em vez de glifo de fonte: nenhuma dependência de qual versão do
        // Segoe MDL2/Fluent está instalada.
        var box = new Border
        {
            Width = 15,
            Height = 15,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1.2),
            BorderBrush = new SolidColorBrush(_palette.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
        };
        Grid.SetColumn(box, 0);

        FrameworkElement title = _editing == task.Id
            ? BuildTitleEditor(task)
            : new TextBlock
            {
                Text = task.Title,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(_palette.BarForeground),
            }.WrappingToTwoLines();
        Grid.SetColumn(title, 1);

        // No lugar do vencimento, quando algo não subiu: naquele instante o que importa é o que
        // falhou, não para quando a tarefa era.
        var status = entry.HasFailed ? BuildRetryAction(entry) : BuildDueAction(entry, bucket);
        Grid.SetColumn(status, 2);

        var rename = BuildRenameAction(entry);
        Grid.SetColumn(rename, 3);

        var delete = BuildDeleteAction(entry, row);
        Grid.SetColumn(delete, 4);

        grid.Children.Add(box);
        grid.Children.Add(title);
        grid.Children.Add(status);
        grid.Children.Add(rename);
        grid.Children.Add(delete);
        row.Child = grid;

        // Linha a caminho não oferece gesto nenhum: nem concluir, nem excluir, nem realce.
        if (entry.IsPending) return row;

        var hover = new SolidColorBrush(_palette.RowHover);
        row.MouseEnter += (_, _) =>
        {
            row.Background = hover;

            // O ✕ só existe sob o ponteiro. Treze tarefas com um ✕ permanente cada viram uma
            // coluna de ruído ao lado do que importa, e a ação principal aqui é concluir.
            if (delete.Tag is null) delete.Visibility = Visibility.Visible;

            // Mesmo motivo para o "+ data" de quem não tem vencimento: convite quando a mão já
            // está ali, e nada no caminho do olho quando não está.
            if (status.Tag is true) status.Visibility = Visibility.Visible;

            // Só o lápis que a linha de fato oferece. Sem a guarda, passar o ponteiro numa linha
            // concluída ou com escrita pendurada acendia um gesto que ela não aceita.
            if (rename.Tag is true) rename.Visibility = Visibility.Visible;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = Brushes.Transparent;

            // Hidden, e não Collapsed, nas três: a coluna já reservou o espaço dela, e recolher
            // faria título, ✎ e ✕ pularem de lugar a cada passagem do ponteiro.
            // Tag marcada no ✕ = confirmação aberta; some só depois de resolvida.
            if (delete.Tag is null) delete.Visibility = Visibility.Hidden;
            if (status.Tag is true) status.Visibility = Visibility.Hidden;
            if (rename.Tag is true) rename.Visibility = Visibility.Hidden;
        };
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;

            // Enquanto há falha pendente de decisão, o clique na linha não vale: concluir uma
            // tarefa cuja exclusão não subiu deixaria duas intenções conflitantes na fila.
            if (entry.HasFailed) return;

            TaskToggled?.Invoke(this, task.Id);
        };

        return row;
    }

    /// <summary>
    /// A pílula de "não salvou". Clicar repete a escrita.
    /// <para>
    /// Usa a cor crítica da paleta, a mesma das vencidas, porque quer dizer a mesma coisa: isto
    /// precisa de você. O motivo exato fica no tooltip — a linha tem largura para um verbo, não
    /// para uma explicação.
    /// </para>
    /// </summary>
    private FrameworkElement BuildRetryAction(TaskRow entry)
    {
        var write = entry.Write!;

        var pill = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(_palette.CriticalBackground),
            Cursor = Cursors.Hand,
            ToolTip = $"{write.Failure} — clique para tentar de novo",
            Child = new TextBlock
            {
                Text = "não salvou",
                FontSize = 11,
                Foreground = new SolidColorBrush(_palette.CriticalForeground),
            },
        };

        pill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true; // sem isto o clique borbulha e conclui a tarefa
            WriteRetried?.Invoke(this, write.Id);
        };

        return pill;
    }

    /// <summary>
    /// O ✕ de excluir, com confirmação na própria linha.
    /// <para>
    /// Excluir <b>não tem volta</b>: a API do Google Tasks não expõe lixeira. Um clique só,
    /// irreversível, num alvo de 16px ao lado de outro clicável é armadilha — o primeiro clique
    /// troca o ✕ por "Excluir?" e só o segundo apaga. Sair da linha com o ponteiro cancela.
    /// </para>
    /// <para>
    /// Numa linha que falhou, o mesmo ✕ significa <b>descartar a alteração</b>, não apagar a
    /// tarefa. A palavra da confirmação muda junto — "Descartar?" em vez de "Excluir?" — porque um
    /// glifo com dois significados só é honesto se disser qual está em jogo na hora de decidir.
    /// </para>
    /// </summary>
    private FrameworkElement BuildDeleteAction(TaskRow entry, Border row)
    {
        var task = entry.Item;
        var discarding = entry.HasFailed;
        var confirmWord = discarding ? "Descartar?" : "Excluir?";
        var idle = discarding ? "Descartar a alteração que não subiu" : "Excluir tarefa";

        // Alvo generoso de propósito. A primeira versão tinha ~25px de largura, e errá-lo não era
        // inofensivo: o clique caía na linha, que conclui a tarefa. Quinze pixels de imprecisão
        // mudavam o resultado. Quando vizinhos têm efeitos diferentes e irreversíveis, o alvo
        // menor precisa ser grande o bastante para não se errar.
        var host = new Border
        {
            MinWidth = 44,
            MinHeight = 26,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(10, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,

            // Hidden, e não Collapsed: a coluna reserva o espaço dela o tempo todo. Recolher fazia
            // o título crescer quando o ponteiro saía — invisível enquanto era uma linha com
            // reticências, mas com quebra em duas o texto se rearranja debaixo do ponteiro.
            Visibility = Visibility.Hidden,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = idle,
        };

        var glyph = new TextBlock
        {
            Text = "✕",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(_palette.Muted),
        };

        host.Child = glyph;

        host.MouseEnter += (_, _) =>
        {
            if (host.Tag is null) glyph.Foreground = new SolidColorBrush(_palette.CriticalBackground);
        };
        host.MouseLeave += (_, _) =>
        {
            if (host.Tag is null) glyph.Foreground = new SolidColorBrush(_palette.Muted);
        };

        host.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true; // sem isto o clique borbulha para a linha e conclui a tarefa

            if (host.Tag is null)
            {
                host.Tag = "confirmando";
                host.Background = new SolidColorBrush(_palette.CriticalBackground);
                host.ToolTip = "Clique de novo para confirmar — sair da linha cancela";
                glyph.Text = confirmWord;
                glyph.Foreground = new SolidColorBrush(_palette.CriticalForeground);
                return;
            }

            if (discarding) WriteDiscarded?.Invoke(this, entry.Write!.Id);
            else TaskDeleted?.Invoke(this, task.Id);
        };

        // Tirar o ponteiro da linha desarma a confirmação: quem se afastou não quis.
        row.MouseLeave += (_, _) =>
        {
            if (host.Tag is null) return;

            host.Tag = null;
            host.Background = Brushes.Transparent;
            host.ToolTip = idle;
            glyph.Text = "✕";
            glyph.Foreground = new SolidColorBrush(_palette.Muted);
        };

        return host;
    }

    /// <summary>
    /// O lápis de renomear (D-045).
    /// <para>
    /// Alvo próprio, e não clique no título, porque o clique na linha já concluí a tarefa — e
    /// duplo clique não serve: o primeiro clique dele já teria enfileirado uma conclusão. É a
    /// mesma lição do D-038, um gesto por significado.
    /// </para>
    /// <para>
    /// Só sob o ponteiro, como o ✕: renomear é raro, e um lápis permanente em cada linha viraria
    /// uma coluna de ruído ao lado do que importa.
    /// </para>
    /// </summary>
    private FrameworkElement BuildRenameAction(TaskRow entry)
    {
        var alvo = new Border
        {
            Padding = new Thickness(7, 2, 7, 2),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Visibility = Visibility.Hidden, // reserva a coluna; ver o motivo em BuildDeleteAction
            ToolTip = "Renomear tarefa",
            Child = new TextBlock
            {
                Text = "✎",
                FontSize = 12,
                Foreground = new SolidColorBrush(_palette.Muted),
            },
        };

        // Concluída não se renomeia: o gesto dela é desfazer a conclusão (D-030), e o que já
        // acabou não merece cobrar edição. Linha com falha pendente também não — renomear enquanto
        // uma escrita não subiu deixaria duas intenções conflitantes na fila, que é a mesma guarda
        // que concluir e excluir já aplicam.
        if (entry.IsPending || entry.HasFailed || entry.Item.IsCompleted)
        {
            alvo.Visibility = Visibility.Collapsed;
            return alvo;
        }

        // Marca que este lápis é oferecível: sem isto o hover da linha revelava até o que os
        // testes acima acabaram de recolher.
        alvo.Tag = true;

        var hover = new SolidColorBrush(_palette.PanelBorder);
        alvo.MouseEnter += (_, _) => alvo.Background = hover;
        alvo.MouseLeave += (_, _) => alvo.Background = Brushes.Transparent;

        var id = entry.Item.Id;
        alvo.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _editing = id;
            Draw();
        };

        return alvo;
    }

    /// <summary>
    /// O editor no lugar do título. <c>Enter</c> grava, <c>Esc</c> desiste, e sair do campo grava
    /// — a convenção do Explorer do Windows, que é onde o usuário aprendeu a renomear.
    /// <para>
    /// Título vazio nunca é gravado: o Google aceitaria e a tarefa viraria uma linha em branco,
    /// impossível de encontrar depois. Vazio equivale a desistir.
    /// </para>
    /// </summary>
    private FrameworkElement BuildTitleEditor(TaskItem task)
    {
        var editor = new TextBox
        {
            Text = task.Title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(4, 2, 4, 2),
            Background = new SolidColorBrush(_palette.PanelBackground),
            Foreground = new SolidColorBrush(_palette.BarForeground),
            BorderBrush = new SolidColorBrush(_palette.OutlineFor(Severity.Info)),
            BorderThickness = new Thickness(1),
            CaretBrush = new SolidColorBrush(_palette.BarForeground),
        };

        var resolvido = false;

        void Finish(bool commit)
        {
            if (resolvido) return;
            resolvido = true;

            var titulo = editor.Text.Trim();
            var mudou = commit && titulo.Length > 0 && titulo != task.Title;

            _editing = null;

            if (mudou) TaskRenamed?.Invoke(this, (task.Id, titulo));

            // Sempre redesenha, mesmo desistindo: o painel ficou congelado durante a edição, então
            // pode haver rodada de sync represada esperando para aparecer.
            Draw();
        }

        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(commit: true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Finish(commit: false); }
        };

        editor.LostKeyboardFocus += (_, _) => Finish(commit: true);

        // Depois do layout, senão o foco vai para um elemento que ainda não está na árvore visual.
        editor.Loaded += (_, _) =>
        {
            editor.Focus();
            editor.SelectAll();
        };

        return editor;
    }

    /// <summary>
    /// O vencimento, que agora também é o <b>controle</b> dele (D-042).
    /// <para>
    /// O rótulo já morava nesta coluna, então torná-lo clicável não custa layout nenhum — o
    /// controle nasce onde a informação estava. A alternativa, uma coluna nova, gastaria largura
    /// permanente num painel estreito para uma ação ocasional.
    /// </para>
    /// <para>
    /// Tarefa <b>sem</b> data tem rótulo vazio, e alvo de clique invisível não existe: ela recebe
    /// um <c>+ data</c> apagado que aparece no hover, o mesmo padrão do ✕ de excluir.
    /// </para>
    /// </summary>
    private FrameworkElement BuildDueAction(TaskRow entry, TaskBucket bucket)
    {
        var task = entry.Item;
        var vazio = bucket == TaskBucket.NoDate;

        var text = new TextBlock
        {
            Text = vazio ? "+ data" : DueLabel(task, bucket),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(
                bucket == TaskBucket.Overdue ? _palette.CriticalBackground : _palette.Muted),
        };

        var host = new Border
        {
            Child = text,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = entry.IsPending ? Cursors.Arrow : Cursors.Hand,

            // O "+ data" só sob o ponteiro; a data de verdade fica sempre à vista, porque ela é
            // informação antes de ser botão.
            Visibility = vazio ? Visibility.Hidden : Visibility.Visible,
        };

        // Concluída não se reagenda: a data dela já não governa nada, e o gesto da linha é
        // desfazer a conclusão (D-030).
        if (entry.IsPending || task.IsCompleted) return host;

        // Marca para a linha saber que este alvo aparece no hover. Só depois da saída acima:
        // numa linha que não aceita o gesto, revelar o "+ data" prometeria o que não cumpre.
        host.Tag = vazio;

        host.MouseEnter += (_, _) => host.Background = new SolidColorBrush(_palette.PanelBorder);
        host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;

        // Handled, senão o clique sobe para a linha e conclui a tarefa em vez de datá-la.
        host.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowDueMenu(host, task);
        };

        return host;
    }

    /// <summary>
    /// Hoje e amanhã em cima, porque são o caso comum; qualquer outro dia no seletor de mês.
    /// <para>
    /// A primeira versão tinha <b>só</b> hoje e amanhã, que era o pedido literal — e a primeira
    /// pergunta em uso foi "como coloco pra sexta?". Atalho resolve o frequente; calendário resolve
    /// o resto, e os dois juntos não deixam nenhum dia inalcançável.
    /// </para>
    /// <para>
    /// Sem hora, e isso não é omissão: a API descarta a hora e grava só o dia (D-031). Oferecer
    /// relógio seria prometer o que o outro lado não guarda.
    /// </para>
    /// </summary>
    private void ShowDueMenu(FrameworkElement anchor, TaskItem task)
    {
        var hoje = DateOnly.FromDateTime(DateTime.Today);
        var menu = new ContextMenu { PlacementTarget = anchor };

        MenuItem Build(string header, DateOnly? due)
        {
            var item = new MenuItem
            {
                Header = header,

                // Negrito no que já está valendo: o menu mostra onde a tarefa está antes de
                // perguntar para onde vai.
                FontWeight = task.Due == due ? FontWeights.SemiBold : FontWeights.Normal,
            };

            item.Click += (_, _) => TaskRescheduled?.Invoke(this, (task.Id, due));
            return item;
        }

        menu.Items.Add(Build("Hoje", hoje));
        menu.Items.Add(Build("Amanhã", hoje.AddDays(1)));

        // Reticências porque abre outra coisa, em vez de agir na hora — a mesma convenção do
        // "Configurações…" no menu da barra.
        var outro = new MenuItem { Header = "Outro dia…" };
        outro.Click += (_, _) => ShowMonthPicker(anchor, task, hoje);
        menu.Items.Add(outro);

        // Só quando há o que apagar: oferecer "sem data" para quem já está sem data seria um gesto
        // que não faz nada — e ainda custaria uma ida extra à API (get + update).
        if (task.Due is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Build("Sem data", null));
        }

        menu.IsOpen = true;
    }

    /// <summary>Rótulos em português, como o resto da barra — sem depender do locale da máquina.</summary>
    private static readonly System.Globalization.CultureInfo Brasil = new("pt-BR");

    /// <summary>
    /// O seletor de mês, desenhado <b>por nós</b>, numa superfície <b>nossa</b>.
    /// <para>
    /// <b>Duas tentativas foram descartadas antes desta, e as duas pelo mesmo motivo de fundo:
    /// dentro de um <c>ContextMenu</c> não mandamos nem na cor nem no clique.</b>
    /// </para>
    /// <para>
    /// Primeiro o <c>Calendar</c> do WPF: o template padrão assume fundo claro e é feito de peças
    /// com cor própria — pintar <c>CalendarDayButton</c> e <c>CalendarButton</c> não alcançava o
    /// cabeçalho, e o nome do mês só aparecia sob o ponteiro. Depois a grade desenhada, mas ainda
    /// hospedada no menu: o menu do WPF <b>não segue o tema do sistema</b> e é sempre claro, então
    /// o texto quase branco da paleta escura sumia no branco da chrome; e a captura de mouse do
    /// menu comia os cliques nas setas de mês.
    /// </para>
    /// <para>
    /// Num <c>Popup</c> próprio os dois problemas somem juntos: o fundo é o da paleta, como o resto
    /// do painel, e o clique é clique. É a mesma escolha da caixa de seleção da linha, desenhada em
    /// vez de vir de fonte de ícones — não depender de como um componente alheio decidiu se pintar.
    /// </para>
    /// </summary>
    private void ShowMonthPicker(FrameworkElement anchor, TaskItem task, DateOnly hoje)
    {
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        // Abre no mês da data atual da tarefa, e não sempre em hoje: reagendar costuma ser um
        // ajuste perto de onde ela já estava.
        var alvo = task.Due ?? hoje;
        var mes = new DateOnly(alvo.Year, alvo.Month, 1);

        var titulo = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(_palette.BarForeground),
        };

        var dias = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7, Rows = 6 };

        void Render()
        {
            titulo.Text = Brasil.TextInfo.ToTitleCase(mes.ToString("MMMM yyyy", Brasil));
            dias.Children.Clear();

            // A grade começa no domingo da semana em que o mês cai, para as colunas baterem com
            // os nomes dos dias da semana.
            var primeiro = mes.AddDays(-(int)mes.DayOfWeek);

            for (var i = 0; i < 42; i++)
            {
                var dia = primeiro.AddDays(i);
                dias.Children.Add(BuildDayCell(popup, task, dia, mes, hoje));
            }
        }

        var voltar = BuildMonthStep("‹", () => { mes = mes.AddMonths(-1); Render(); });
        var avancar = BuildMonthStep("›", () => { mes = mes.AddMonths(1); Render(); });

        var cabecalho = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(voltar, Dock.Left);
        DockPanel.SetDock(avancar, Dock.Right);
        cabecalho.Children.Add(voltar);
        cabecalho.Children.Add(avancar);
        cabecalho.Children.Add(titulo);

        var semana = new System.Windows.Controls.Primitives.UniformGrid
        {
            Columns = 7,
            Margin = new Thickness(0, 0, 0, 2),
        };

        foreach (var nome in Brasil.DateTimeFormat.ShortestDayNames)
        {
            semana.Children.Add(new TextBlock
            {
                Text = nome.ToUpper(Brasil),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(_palette.Muted),
            });
        }

        var corpo = new StackPanel { Width = 224 };
        corpo.Children.Add(cabecalho);
        corpo.Children.Add(semana);
        corpo.Children.Add(dias);

        Render();

        // O fundo é nosso, e é aqui que a legibilidade se resolve: sobre PanelBackground, o texto
        // da paleta lê nos dois temas. Era exatamente isto que faltava enquanto a grade morava
        // dentro do menu, que é sempre claro.
        popup.Child = new Border
        {
            Child = corpo,
            Padding = new Thickness(8, 6, 8, 8),
            Background = new SolidColorBrush(_palette.PanelBackground),
            BorderBrush = new SolidColorBrush(_palette.PanelBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
        };

        popup.IsOpen = true;

        // Sem foco o StaysOpen=false não fecha ao clicar fora: o popup precisa perder algo para
        // saber que saiu de cena.
        corpo.Focusable = true;
        corpo.Focus();
    }

    /// <summary>Um dia da grade. Fora do mês exibido, fica apagado mas continua clicável.</summary>
    private UIElement BuildDayCell(
        System.Windows.Controls.Primitives.Popup popup,
        TaskItem task,
        DateOnly dia,
        DateOnly mes,
        DateOnly hoje)
    {
        var doMes = dia.Month == mes.Month && dia.Year == mes.Year;
        var selecionado = task.Due == dia;

        var cell = new Border
        {
            Height = 26,
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Cursor = Cursors.Hand,
            Background = selecionado
                ? new SolidColorBrush(_palette.InfoBackground)
                : Brushes.Transparent,

            // Hoje ganha contorno em vez de preenchimento: preenchimento é do dia escolhido, e as
            // duas coisas precisam coexistir sem se confundir.
            BorderThickness = new Thickness(dia == hoje ? 1 : 0),
            BorderBrush = new SolidColorBrush(_palette.OutlineFor(Severity.Info)),
            Child = new TextBlock
            {
                Text = dia.Day.ToString(Brasil),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(
                    doMes ? _palette.BarForeground : _palette.Muted),
            },
        };

        var hover = new SolidColorBrush(_palette.RowHover);
        cell.MouseEnter += (_, _) => { if (!selecionado) cell.Background = hover; };
        cell.MouseLeave += (_, _) => { if (!selecionado) cell.Background = Brushes.Transparent; };

        cell.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            popup.IsOpen = false;
            TaskRescheduled?.Invoke(this, (task.Id, dia));
        };

        return cell;
    }

    /// <summary>As setas de mês.</summary>
    private UIElement BuildMonthStep(string glifo, Action step)
    {
        var alvo = new Border
        {
            Width = 24,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = glifo,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(_palette.BarForeground),
            },
        };

        var hover = new SolidColorBrush(_palette.RowHover);
        alvo.MouseEnter += (_, _) => alvo.Background = hover;
        alvo.MouseLeave += (_, _) => alvo.Background = Brushes.Transparent;

        alvo.MouseLeftButtonUp += (_, e) => { e.Handled = true; step(); };

        return alvo;
    }

    private static string DueLabel(TaskItem task, TaskBucket bucket) => bucket switch
    {
        TaskBucket.Today => "hoje",
        TaskBucket.NoDate => "",
        _ => task.Due?.ToString("dd/MM") ?? "",
    };

    private void OnNewTaskKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        var title = NewTaskBox.Text.Trim();
        if (title.Length == 0) return;

        e.Handled = true;
        NewTaskBox.Clear();
        TaskCreated?.Invoke(this, title);
    }

    private void ApplyPalette()
    {
        Background = Brushes.Transparent;
        Shell.Background = new SolidColorBrush(_palette.PanelBackground);
        Shell.BorderBrush = new SolidColorBrush(_palette.PanelBorder);

        HeaderText.Foreground = new SolidColorBrush(_palette.BarForeground);
        HeaderCount.Foreground = new SolidColorBrush(_palette.Muted);

        ComposerBorder.BorderBrush = new SolidColorBrush(_palette.PanelBorder);
        Placeholder.Foreground = new SolidColorBrush(_palette.Muted);
        ComposerHint.Foreground = new SolidColorBrush(_palette.Muted);
        NewTaskBox.Foreground = new SolidColorBrush(_palette.BarForeground);
        NewTaskBox.CaretBrush = new SolidColorBrush(_palette.BarForeground);
    }
}

internal static class TitleText
{
    /// <summary>Altura de uma linha, fixada para o corte não depender da métrica da fonte.</summary>
    private const double LineDip = 18;

    /// <summary>
    /// Título que quebra em <b>duas</b> linhas e só então usa reticências.
    /// <para>
    /// Uma linha só escondia metade do que vem das automações do Chat. Ilimitado seria pior: um
    /// título patológico — uma URL colada, uma frase inteira — empurraria a lista para fora da
    /// tela, e o painel existe para ser varrido com o olho, não lido parágrafo a parágrafo.
    /// </para>
    /// <para>
    /// WPF não tem <c>MaxLines</c> (é de UWP): o corte é por altura, e por isso a linha precisa ter
    /// altura fixa em vez da natural da fonte. <c>BlockLineHeight</c> é o que garante isso — sem
    /// ele, um acento ou um glifo mais alto no meio do texto empurraria a segunda linha para fora
    /// do limite e o título viraria uma linha de novo, sem explicação visível.
    /// </para>
    /// </summary>
    public static TextBlock WrappingToTwoLines(this TextBlock title)
    {
        title.TextWrapping = TextWrapping.Wrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        title.LineHeight = LineDip;
        title.MaxHeight = LineDip * 2;

        return title;
    }
}
