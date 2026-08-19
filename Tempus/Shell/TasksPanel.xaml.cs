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

    /// <summary>Usuário criou uma tarefa. Carrega o título.</summary>
    public event EventHandler<string>? TaskCreated;

    /// <summary>Usuário confirmou a exclusão. Carrega o id. Não tem volta (D-024).</summary>
    public event EventHandler<string>? TaskDeleted;

    /// <summary>"Tentar de novo" numa escrita que falhou. Carrega o id da <b>intenção</b>.</summary>
    public event EventHandler<string>? WriteRetried;

    /// <summary>"Deixa pra lá": abandona a intenção. Carrega o id da <b>intenção</b>.</summary>
    public event EventHandler<string>? WriteDiscarded;

    /// <summary>Perdeu o foco ou levou <c>Esc</c>: a superfície deve fechar este painel.</summary>
    public event EventHandler? Dismissed;

    public void ShowAt(NativeMethods.RECT anchor) => _host.ShowAt(anchor);

    public void Render(IReadOnlyList<TaskRow> tasks)
    {
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
            return;
        }

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

        RepositionAfterLayout();
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

        var title = new TextBlock
        {
            Text = task.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(_palette.BarForeground),
        };
        Grid.SetColumn(title, 1);

        // No lugar do vencimento, quando algo não subiu: naquele instante o que importa é o que
        // falhou, não para quando a tarefa era.
        var status = entry.HasFailed
            ? BuildRetryAction(entry)
            : new TextBlock
            {
                Text = DueLabel(task, bucket),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Foreground = new SolidColorBrush(
                    bucket == TaskBucket.Overdue ? _palette.CriticalBackground : _palette.Muted),
            };
        Grid.SetColumn(status, 2);

        var delete = BuildDeleteAction(entry, row);
        Grid.SetColumn(delete, 3);

        grid.Children.Add(box);
        grid.Children.Add(title);
        grid.Children.Add(status);
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
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = Brushes.Transparent;

            // Tag marcada = confirmação aberta; some só depois de resolvida.
            if (delete.Tag is null) delete.Visibility = Visibility.Collapsed;
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
            Visibility = Visibility.Collapsed,
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
