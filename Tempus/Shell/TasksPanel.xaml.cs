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
/// Fase 2 opera com dados falsos. Concluir e criar já funcionam de ponta a ponta contra a fonte
/// falsa — o objetivo é provar o laço painel→barra (o contador reage). A escrita real no Google
/// Tasks, incluindo edição de título e vencimento, é da Fase 4, que é a fase dona das escritas.
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

    /// <summary>Perdeu o foco ou levou <c>Esc</c>: a superfície deve fechar este painel.</summary>
    public event EventHandler? Dismissed;

    public void ShowAt(NativeMethods.RECT anchor) => _host.ShowAt(anchor);

    public void Render(IReadOnlyList<TaskItem> tasks)
    {
        var open = tasks.Where(t => !t.IsCompleted).ToList();
        var today = DateOnly.FromDateTime(DateTime.Today);

        HeaderCount.Text = open.Count switch
        {
            0 => "nada aberto",
            1 => "1 tarefa",
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
            .GroupBy(t => t.Bucket(today))
            .OrderBy(g => g.Key);

        foreach (var bucket in buckets)
        {
            Groups.Children.Add(BuildGroupHeader(bucket.Key, bucket.Count()));

            foreach (var task in bucket.OrderBy(t => t.Due ?? DateOnly.MaxValue).ThenBy(t => t.Title))
                Groups.Children.Add(BuildRow(task, bucket.Key));
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

    private UIElement BuildRow(TaskItem task, TaskBucket bucket)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 7, 8, 7),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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

        var due = new TextBlock
        {
            Text = DueLabel(task, bucket),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = new SolidColorBrush(
                bucket == TaskBucket.Overdue ? _palette.CriticalBackground : _palette.Muted),
        };
        Grid.SetColumn(due, 2);

        grid.Children.Add(box);
        grid.Children.Add(title);
        grid.Children.Add(due);
        row.Child = grid;

        var hover = new SolidColorBrush(_palette.RowHover);
        row.MouseEnter += (_, _) => row.Background = hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            TaskToggled?.Invoke(this, task.Id);
        };

        return row;
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
