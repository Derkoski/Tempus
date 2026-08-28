using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tempus.Domain;
using Tempus.Interop;

namespace Tempus.Shell;

/// <summary>
/// Superfície S3 do SPEC: a timeline do dia.
/// <para>
/// O elemento que carrega o valor de produto não são os eventos — é o que aparece
/// <b>entre</b> eles. Ver um "sem intervalo" antes da próxima reunião é o que permite não
/// estourar a atual, e é a informação que nenhuma das ferramentas do Google mostra.
/// </para>
/// </summary>
internal partial class AgendaPanel : Window
{
    private readonly PanelHost _host;
    private readonly Palette _palette;

    public AgendaPanel(Palette palette, NativeMethods.RECT anchor)
    {
        _palette = palette;
        InitializeComponent();

        _host = new PanelHost(this) { Anchor = anchor };
        _host.CloseRequested += (_, _) => Dismissed?.Invoke(this, EventArgs.Empty);

        ApplyPalette();
    }

    /// <summary>Usuário clicou num evento com Meet. Carrega a URL.</summary>
    public event EventHandler<string>? MeetingActivated;

    /// <summary>Perdeu o foco ou levou <c>Esc</c>: a superfície deve fechar este painel.</summary>
    public event EventHandler? Dismissed;

    public void ShowAt(NativeMethods.RECT anchor) => _host.ShowAt(anchor);

    /// <summary>
    /// Quantos dias o resumo cobre, contando hoje. Casa com a janela que o <c>GoogleSync</c>
    /// carrega — pedir mais mostraria dias vazios por falta de dado, não por falta de compromisso,
    /// que é a regra 10 aplicada a uma agenda.
    /// </summary>
    private const int DiasNoResumo = 15;

    private static readonly System.Globalization.CultureInfo Brasil = new("pt-BR");

    private IReadOnlyList<AgendaItem> _todosOsDias = [];
    private IReadOnlyList<BreakSlot> _breaks = [];
    private WorkDayOptions _work = WorkDayOptions.Default;
    private DateTimeOffset _now;

    /// <summary>
    /// Em qual dos dois modos o painel está.
    /// <para>
    /// Separado do dia selecionado de propósito. Enquanto um campo só carregava as duas coisas
    /// (<c>null</c> = resumo <b>e</b> <c>null</c> = ainda não inicializado), todo <c>Render</c>
    /// vindo do sync devolvia o painel para hoje — e abrir o painel dispara uma rodada, que
    /// chegava um segundo depois e desfazia o clique em "Próximos dias".
    /// </para>
    /// <para>
    /// Estado do <b>painel</b>, e não do <c>App</c>: morre quando o painel fecha, que é o
    /// comportamento certo para uma consulta. Reabrir cai em hoje.
    /// </para>
    /// </summary>
    private bool _resumo;

    /// <summary>
    /// O dia da timeline, ou <c>null</c> para hoje. Guardar a ausência em vez da data de hoje faz
    /// o painel acompanhar a virada do dia sozinho.
    /// </summary>
    private DateOnly? _dia;

    /// <summary>
    /// O dia aberto no resumo, ou <c>null</c>. Um por vez: as janelas livres — que é o que se
    /// compara — já aparecem em todas as linhas, e manter vários abertos só alongaria a lista.
    /// </summary>
    private DateOnly? _expanded;

    public void Render(
        IReadOnlyList<AgendaItem> agenda,
        IReadOnlyList<BreakSlot> breaks,
        DateTimeOffset now,
        WorkDayOptions work)
    {
        _todosOsDias = agenda;
        _breaks = breaks;
        _work = work;
        _now = now;

        // Só dados entram aqui. Onde o usuário está navegando é decisão dele, e um retrato novo
        // do Google não é motivo para mudá-la.
        Draw();
    }

    private void Draw()
    {
        // O padrão é a timeline de hoje: abrir o painel é, na esmagadora maioria das vezes,
        // perguntar sobre agora — trocá-lo pelo resumo custaria um clique no caso comum para
        // servir o ocasional.
        if (_resumo) DrawWeek();
        else DrawDay(_dia ?? DateOnly.FromDateTime(_now.Date));

        RepositionAfterLayout();
    }

    private void DrawDay(DateOnly day)
    {
        var hoje = DateOnly.FromDateTime(_now.Date);
        var now = _now;

        HeaderText.Text = day == hoje
            ? "Agenda de hoje"
            : Brasil.TextInfo.ToTitleCase(day.ToString("dddd, dd/MM", Brasil));

        ShowLink("Próximos dias  ›", () => { _resumo = true; Draw(); });

        // Pausas de descanso são planejamento do dia corrente. Projetá-las num dia futuro
        // inventaria compromisso que não existe.
        var breaks = day == hoje ? _breaks : [];

        var agenda = _todosOsDias
            .Where(e => DateOnly.FromDateTime(e.Start.ToLocalTime().Date) == day)
            .ToList();

        var events = agenda
            .Where(e => !e.IsAllDay) // dia inteiro é marcador, não compromisso (SEVERITY.md §2.1)
            .OrderBy(e => e.Start)
            .ToList();

        // A contagem do cabeçalho ignora as pausas: ela responde "quantos compromissos tenho", e
        // descanso não é compromisso. Inflá-la faria o dia parecer mais cheio do que é.
        HeaderCount.Text = events.Count switch
        {
            0 => "sem compromissos",
            1 => "1 compromisso",
            _ => $"{events.Count} compromissos",
        };

        Timeline.Children.Clear();

        if (events.Count == 0 && breaks.Count == 0)
        {
            Timeline.Children.Add(new TextBlock
            {
                Text = day == hoje ? "Nenhuma reunião hoje." : "Nenhuma reunião neste dia.",
                Margin = new Thickness(6, 14, 6, 18),
                Foreground = new SolidColorBrush(_palette.Muted),
            });
            return;
        }

        var next = events.FirstOrDefault(e => e.Start > now);

        // Reuniões e pausas entram numa linha do tempo só, ordenadas por horário. O intervalo
        // entre linhas passa a considerar a pausa — sem isso o painel diria "45 min livre" num
        // vão que já tem descanso marcado dentro.
        var blocks = events
            .Select(e => new TimelineBlock(e.Start, e.End, e, null))
            .Concat(breaks.Select(b => new TimelineBlock(b.Start, b.End, null, b)))
            .OrderBy(b => b.Start)
            .ToList();

        for (var i = 0; i < blocks.Count; i++)
        {
            var current = blocks[i];

            Timeline.Children.Add(current switch
            {
                { Event: { } item } => BuildEventRow(item, now, isNext: item == next),
                { Break: { } pause } => BuildBreakRow(pause, now),
                _ => new TextBlock(),
            });

            if (i + 1 < blocks.Count)
                Timeline.Children.Add(BuildGapRow(new AgendaGap(blocks[i + 1].Start - current.End)));
        }
    }

    /// <summary>
    /// O resumo dos próximos dias — a resposta para "você tem horário livre quinta?", que é a
    /// pergunta que vem de outra pessoa e obrigava a abrir o Google Agenda (D-043).
    /// <para>
    /// Cada linha diz duas coisas e só: <b>quanto</b> o dia está cheio e <b>onde</b> ele está
    /// livre. O detalhe de qual reunião é fica a um clique, na timeline do dia.
    /// </para>
    /// </summary>
    private void DrawWeek()
    {
        var hoje = DateOnly.FromDateTime(_now.Date);

        HeaderText.Text = "Próximos dias";
        HeaderCount.Text = "";
        ShowLink("‹  Voltar", () => { _resumo = false; _dia = null; Draw(); });

        Timeline.Children.Clear();

        var dias = DayAvailability.Summarize(_todosOsDias, hoje, DiasNoResumo, _work, _now);

        foreach (var dia in dias)
        {
            Timeline.Children.Add(BuildDaySummaryRow(dia, hoje));

            if (_expanded == dia.Day) Timeline.Children.Add(BuildDayDetail(dia.Day));
        }
    }

    /// <summary>
    /// Os compromissos do dia, abertos <b>no lugar</b>.
    /// <para>
    /// Expandir em vez de navegar não é preferência de estilo: quem pergunta "dá pra remarcar
    /// alguma coisa?" está <b>comparando</b>, e trocar a tela pela timeline de um dia esconde
    /// justamente os outros dias contra os quais ele está decidindo. As janelas livres continuam
    /// visíveis acima e abaixo enquanto ele lê o que há aqui dentro.
    /// </para>
    /// <para>
    /// Deliberadamente quieto: hora e título, sem o fundo tingido por serviço nem o clique de
    /// entrar na call. Esta lista serve para <b>decidir</b>, e entrar numa reunião de quinta-feira
    /// não é uma decisão que se toma hoje — a timeline do dia continua sendo o lugar disso.
    /// </para>
    /// </summary>
    private UIElement BuildDayDetail(DateOnly day)
    {
        var itens = _todosOsDias
            .Where(e => !e.IsAllDay && DateOnly.FromDateTime(e.Start.ToLocalTime().Date) == day)
            .OrderBy(e => e.Start)
            .ToList();

        var lista = new StackPanel { Margin = new Thickness(18, 0, 6, 8) };

        foreach (var item in itens)
        {
            var linha = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };

            var hora = new TextBlock
            {
                Text = $"{item.Start.ToLocalTime():HH:mm}–{item.End.ToLocalTime():HH:mm}",
                FontSize = 12,
                Width = 92,
                Foreground = new SolidColorBrush(_palette.Muted),
            };

            DockPanel.SetDock(hora, Dock.Left);
            linha.Children.Add(hora);

            linha.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(_palette.BarForeground),
            });

            lista.Children.Add(linha);
        }

        // A porta para o detalhe completo — gaps, marca do serviço e o clique de entrar. Fica
        // aqui dentro, e não na linha do dia, para o gesto principal da lista continuar sendo um só.
        var abrir = new TextBlock
        {
            Text = "ver o dia todo  ›",
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            Cursor = Cursors.Hand,
            Foreground = new SolidColorBrush(_palette.OutlineFor(Severity.Info)),
        };

        abrir.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _resumo = false;
            _dia = day;
            Draw();
        };

        lista.Children.Add(abrir);
        return lista;
    }

    private UIElement BuildDaySummaryRow(DaySummary dia, DateOnly hoje)
    {
        var nome = dia.Day == hoje
            ? "hoje"
            : dia.Day == hoje.AddDays(1)
                ? "amanhã"
                : dia.Day.ToString("ddd dd/MM", Brasil);

        var titulo = new TextBlock
        {
            Text = Brasil.TextInfo.ToTitleCase(nome),
            FontWeight = dia.Day == hoje ? FontWeights.SemiBold : FontWeights.Normal,
            Width = 96,
            VerticalAlignment = VerticalAlignment.Center,

            // Dia não útil fica apagado: ele está na lista para explicar o vazio, não para ser
            // considerado.
            Foreground = new SolidColorBrush(
                dia.IsWorkingDay ? _palette.BarForeground : _palette.Muted),
        };

        var contagem = new TextBlock
        {
            Text = dia.Meetings switch
            {
                0 => "livre",
                1 => "1 compromisso",
                _ => $"{dia.Meetings} compromissos",
            },
            FontSize = 12,
            Foreground = new SolidColorBrush(_palette.Muted),
        };

        // A linha que responde a pergunta. Verde de "Livre" quando há janela, apagado quando não —
        // sem cor nova entrando por causa disto (regra 1).
        var janelas = new TextBlock
        {
            Text = dia.HasFree
                ? string.Join("   ", dia.Free.Select(f => f.Label))
                : dia.Note ?? "sem janelas",
            FontSize = 12,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(
                dia.HasFree ? _palette.FreeForeground : _palette.Muted),
        };

        var texto = new StackPanel();
        texto.Children.Add(contagem);
        texto.Children.Add(janelas);

        var aberto = _expanded == dia.Day;

        // O galho diz que a linha abre, e para que lado. Dia sem compromisso não ganha nenhum:
        // prometer expansão a quem não tem o que mostrar é afordância que mente.
        var galho = new TextBlock
        {
            Text = dia.Meetings == 0 ? "" : aberto ? "⌄" : "›",
            FontSize = 12,
            Width = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(_palette.Muted),
        };

        var linha = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(galho, Dock.Left);
        DockPanel.SetDock(titulo, Dock.Left);
        linha.Children.Add(galho);
        linha.Children.Add(titulo);
        linha.Children.Add(texto);

        var row = new Border
        {
            Child = linha,
            Padding = new Thickness(6, 6, 6, 6),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Cursor = dia.Meetings == 0 ? Cursors.Arrow : Cursors.Hand,
        };

        if (dia.Meetings == 0) return row;

        var hover = new SolidColorBrush(_palette.RowHover);
        row.MouseEnter += (_, _) => row.Background = hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

        // Abre no lugar, e não navega: ver o que há num dia é o passo do meio da decisão, e a
        // comparação com os outros dias precisa continuar na tela. Um aberto por vez mantém a
        // lista curta — as janelas livres, que são o que se compara, aparecem em todas as linhas
        // o tempo todo.
        var alvo = dia.Day;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _expanded = _expanded == alvo ? null : alvo;
            Draw();
        };

        return row;
    }

    /// <summary>
    /// O link do cabeçalho. O tratador é trocado a cada desenho, então precisa ser desligado
    /// antes — o painel redesenha a cada sync, e sem isto acumularia um tratador por rodada.
    /// </summary>
    private void ShowLink(string text, Action onClick)
    {
        HeaderLink.Text = text;

        HeaderLink.MouseLeftButtonUp -= OnHeaderLink;
        _headerAction = onClick;
        HeaderLink.MouseLeftButtonUp += OnHeaderLink;
    }

    private Action? _headerAction;

    private void OnHeaderLink(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _headerAction?.Invoke();
    }

    /// <summary>Uma linha da timeline: ou uma reunião, ou uma pausa. Nunca as duas.</summary>
    private readonly record struct TimelineBlock(
        DateTimeOffset Start,
        DateTimeOffset End,
        AgendaItem? Event,
        BreakSlot? Break);

    /// <summary>
    /// Uma pausa de descanso.
    /// <para>
    /// Deliberadamente diferente de uma reunião: sem fundo de serviço, sem marca de convite, e com
    /// o título em itálico no verde de "Livre". Nenhuma cor nova entra por causa dela (regra 1) —
    /// o que a distingue é a forma, não o tom.
    /// </para>
    /// </summary>
    private UIElement BuildBreakRow(BreakSlot pause, DateTimeOffset now)
    {
        var isRunning = pause.IsRunningAt(now);
        var isPast = pause.HasEndedBy(now);

        var row = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 7, 8, 7),
            Background = Brushes.Transparent,
            ToolTip = isPast
                ? $"{pause.Label} — já passou"
                : $"{pause.Label} — até {pause.End.ToLocalTime():HH:mm}{Environment.NewLine}Levante e descanse",
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var marker = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 1, 9, 1),
            Background = new SolidColorBrush(
                isRunning ? _palette.FreeForeground : Colors.Transparent),
        };
        Grid.SetColumn(marker, 0);

        var time = new TextBlock
        {
            Text = $"{pause.Start.ToLocalTime():HH:mm}–{pause.End.ToLocalTime():HH:mm}",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            FontWeight = isRunning ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(_palette.Muted),
        };
        Grid.SetColumn(time, 1);

        var title = new TextBlock
        {
            Text = pause.Label,
            FontStyle = FontStyles.Italic,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = isPast ? 0.45 : 1.0,
            Foreground = new SolidColorBrush(_palette.FreeForeground),
        };
        Grid.SetColumn(title, 3);

        grid.Children.Add(marker);
        grid.Children.Add(time);
        grid.Children.Add(title);

        row.Child = grid;
        return row;
    }

    private UIElement BuildEventRow(AgendaItem item, DateTimeOffset now, bool isNext)
    {
        var isRunning = item.IsRunningAt(now);
        var isPast = item.HasEndedBy(now);

        var row = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 7, 8, 7),
            Background = Brushes.Transparent,
            Cursor = item.Conference is null ? Cursors.Arrow : Cursors.Hand,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // faixa de estado
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // horário
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // resposta ao convite
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // origem
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // selo de estado

        // Faixa vertical marcando o evento em curso.
        var marker = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 1, 9, 1),
            Background = new SolidColorBrush(isRunning
                ? _palette.AttentionBackground
                : isNext ? _palette.InfoBackground : Colors.Transparent),
        };
        Grid.SetColumn(marker, 0);

        var time = new TextBlock
        {
            Text = $"{item.Start.ToLocalTime():HH:mm}–{item.End.ToLocalTime():HH:mm}",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            FontWeight = isRunning ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(isPast ? _palette.Muted : _palette.BarForeground),
        };
        Grid.SetColumn(time, 1);

        var title = new TextBlock
        {
            Text = item.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = isRunning ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(isPast ? _palette.Muted : _palette.BarForeground),
        };

        // O serviço da call é o fundo do título (D-017). Fica na coluna estrelada, então o
        // Border acompanha a largura e o texto dentro continua truncando com reticências.
        var titleHost = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(item.Conference is null ? 0 : 6, 2, 6, 2),
            Margin = new Thickness(item.Conference is null ? 0 : -6, 0, 0, 0),
            Background = new SolidColorBrush(ProviderTint(item.Conference, isPast)),
            Child = title,
        };
        Grid.SetColumn(titleHost, 3);

        grid.Children.Add(marker);
        grid.Children.Add(time);
        grid.Children.Add(titleHost);

        if (BuildRsvpMark(item.Rsvp, isPast) is { } mark)
        {
            Grid.SetColumn(mark, 2);
            grid.Children.Add(mark);
        }

        // De qual calendário veio, quando não é o principal (D-034). Texto apagado, sem cor: a
        // origem é identidade, não severidade, e não paga nada do orçamento da regra 1. O
        // principal não se anuncia — seria ruído em quase toda linha.
        if (item.Source is { Length: > 0 } source)
        {
            var origin = new TextBlock
            {
                Text = source,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Foreground = new SolidColorBrush(_palette.Muted),
            };

            Grid.SetColumn(origin, 4);
            grid.Children.Add(origin);
        }

        var badge = BuildBadge(isRunning, isNext, isPast);
        if (badge is not null)
        {
            Grid.SetColumn(badge, 5);
            grid.Children.Add(badge);
        }

        row.Child = grid;

        if (item.Conference is { } conference)
        {
            var hover = new SolidColorBrush(_palette.RowHover);
            row.MouseEnter += (_, _) => row.Background = hover;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                MeetingActivated?.Invoke(this, conference.Url);
            };
            row.ToolTip = item.Source is { Length: > 0 } from
                ? $"Clique para entrar no {conference.ProviderName}{Environment.NewLine}Veio de: {from}"
                : $"Clique para entrar no {conference.ProviderName}";
        }
        else if (item.Source is { Length: > 0 } origin)
        {
            row.ToolTip = $"Veio de: {origin}";
        }

        return row;
    }

    /// <summary>
    /// Fundo do título na cor do serviço (D-017). Tingido, e não saturado: quase todo evento tem
    /// call, e chapado o painel viraria uma parede de azul e verde onde nada se destaca.
    /// Transparente quando não há call — a ausência já diz "presencial".
    /// </summary>
    private Color ProviderTint(Conference? conference, bool isPast)
    {
        if (conference is null) return Colors.Transparent;

        var c = _palette.ForProvider(conference.Provider);

        // Evento passado desbota junto com o resto da linha: o que já acabou não deve competir
        // com o que ainda vai acontecer.
        var alpha = (byte)(isPast ? 0x22 : 0x4D);

        return Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    /// <summary>
    /// Sua resposta ao convite (D-018).
    /// <para>
    /// Três <b>formas distintas</b> — ✓, ?, ● — e não variações de um mesmo círculo. A primeira
    /// versão distinguia por preenchimento (cheio vs. anel vazado) para economizar cor, e falhou
    /// no uso real: a 9px, cheio e vazado só se distinguem de perto. Forma diferente se lê de
    /// relance; preenchimento, não.
    /// </para>
    /// <para>
    /// Só o pendente colore. Aceito é o estado normal e não precisa competir com nada; "não
    /// respondi" é o único que exige ação, e usa o âmbar de atenção do próprio modelo de
    /// severidade — a mesma cor querendo dizer a mesma coisa.
    /// </para>
    /// </summary>
    private UIElement? BuildRsvpMark(Rsvp rsvp, bool isPast)
    {
        if (rsvp is Rsvp.None) return null;

        var margin = new Thickness(0, 0, 9, 0);

        UIElement mark = rsvp switch
        {
            Rsvp.NeedsAction => new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(1, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(_palette.AttentionBackground),
            },

            // E73E é o mesmo CheckMark do contador de tarefas — check já significa "resolvido"
            // no resto da barra, e repetir o símbolo é coerência, não preguiça.
            Rsvp.Accepted => new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 10,
                Margin = margin,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(_palette.Muted),
            },

            _ => new TextBlock
            {
                Text = "?",
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Margin = margin,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(_palette.Muted),
            },
        };

        if (mark is FrameworkElement fe)
        {
            fe.Opacity = isPast ? 0.4 : 1.0;
            fe.ToolTip = rsvp switch
            {
                Rsvp.Accepted => "Você aceitou",
                Rsvp.Tentative => "Você marcou como talvez",
                _ => "Você ainda não respondeu",
            };
        }

        return mark;
    }

    private UIElement? BuildBadge(bool isRunning, bool isNext, bool isPast)
    {
        var (text, background, foreground) = (isRunning, isNext, isPast) switch
        {
            (true, _, _) => ("agora", _palette.AttentionBackground, _palette.AttentionForeground),
            (_, true, _) => ("próxima", _palette.InfoBackground, _palette.InfoForeground),
            _ => ("", Colors.Transparent, Colors.Transparent),
        };

        if (text.Length == 0) return null;

        return new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(background),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(foreground),
            },
        };
    }

    /// <summary>
    /// A linha entre dois eventos. Sem intervalo e sobreposição ganham cor de alerta porque são
    /// exatamente as situações que produzem o vermelho principal do produto
    /// (<c>MeetingRanIntoNext</c>); um intervalo folgado fica discreto.
    /// </summary>
    private UIElement BuildGapRow(AgendaGap gap)
    {
        var tense = gap.IsOverlap || gap.IsBackToBack;
        var accent = gap.IsOverlap ? _palette.CriticalBackground : _palette.AttentionBackground;
        var color = tense ? accent : _palette.Muted;

        var grid = new Grid { Margin = new Thickness(6, 1, 8, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Alinha com a faixa vertical dos eventos, mantendo a coluna de tempo intacta.
        var stem = new Border
        {
            Width = 3,
            Height = tense ? 14.0 : 10.0,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 9, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(color) { Opacity = tense ? 0.9 : 0.35 },
        };
        Grid.SetColumn(stem, 0);

        var label = new TextBlock
        {
            Text = gap.Label,
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = tense ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(color),
        };
        Grid.SetColumn(label, 1);

        grid.Children.Add(stem);
        grid.Children.Add(label);
        return grid;
    }

    private void RepositionAfterLayout() => Dispatcher.BeginInvoke(
        new Action(() => _host.Reposition()), System.Windows.Threading.DispatcherPriority.Loaded);

    private void ApplyPalette()
    {
        Background = Brushes.Transparent;
        Shell.Background = new SolidColorBrush(_palette.PanelBackground);
        Shell.BorderBrush = new SolidColorBrush(_palette.PanelBorder);

        HeaderText.Foreground = new SolidColorBrush(_palette.BarForeground);
        HeaderCount.Foreground = new SolidColorBrush(_palette.Muted);

        // Azul de Info: é navegação, e a barra já usa esse tom para "algo se aproxima, sem
        // urgência". Nenhuma cor nova entra por causa disto (regra 1).
        HeaderLink.Foreground = new SolidColorBrush(_palette.OutlineFor(Severity.Info));
    }
}
