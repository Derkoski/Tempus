using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Tempus.Domain;
using Tempus.Interop;
using Tempus.Sync;

namespace Tempus.Shell;

/// <summary>
/// Tela de configuração (D-020). Cobre o que o usuário de fato mexe — identidade, expediente e
/// pausas — e deixa o ajuste fino no <c>appsettings.json</c>.
/// <para>
/// Em <b>primeira execução</b> ela é obrigatória: sem e-mail válido não há como seguir, e o botão
/// vira <i>Sair</i> em vez de <i>Cancelar</i>. Foi escolha do usuário exigir o campo, ciente de que
/// o app tecnicamente funcionaria sem ele — <c>login_hint</c> só pré-seleciona a conta no consent.
/// O rótulo muda para a consequência de fechar ficar explícita em vez de surpreender.
/// </para>
/// </summary>
internal partial class SettingsWindow : Window
{
    /// <summary>
    /// Validação deliberadamente frouxa: exige algo antes da arroba, algo depois, e um ponto no
    /// domínio. Validar e-mail com rigor é um poço sem fundo, e o custo de errar aqui é uma tela
    /// de consent que não pré-seleciona a conta — não uma falha.
    /// </summary>
    private static readonly Regex EmailShape = new(@"^[^@\s]+@[^@\s.]+\.[^@\s]+$", RegexOptions.Compiled);

    private readonly Palette _palette;
    private readonly bool _isFirstRun;

    public SettingsWindow(
        Palette palette,
        string? loginHint,
        WorkDayOptions workDay,
        BreakOptions breaks,
        bool isFirstRun)
    {
        _palette = palette;
        _isFirstRun = isFirstRun;

        InitializeComponent();
        ApplyPalette();

        EmailBox.Text = loginHint ?? string.Empty;
        StartBox.Text = Format(workDay.StartHour, workDay.StartMinute);
        MiddayBox.Text = Format(workDay.MiddayHour, workDay.MiddayMinute);
        LunchEndBox.Text = Format(workDay.LunchEndHour, workDay.LunchEndMinute);
        EndBox.Text = Format(workDay.EndHour, workDay.EndMinute);
        BreaksBox.IsChecked = breaks.Enabled;
        BreakMinutesBox.Text = breaks.DurationMinutes.ToString();

        if (isFirstRun)
        {
            Title = "Bem-vindo ao Tempus";
            CancelButton.Content = "Sair";
            EmailHint.Text =
                "Pré-seleciona a conta na tela de consent do Google, que se repete toda semana. "
                + "É obrigatório para continuar.";
        }

        BreaksBox.Checked += (_, _) => SyncBreakRow();
        BreaksBox.Unchecked += (_, _) => SyncBreakRow();
        SyncBreakRow();

        SaveButton.Click += (_, _) => OnSave();
        CancelButton.Click += (_, _) => OnCancel();
    }

    /// <summary>O que salvar, ou <c>null</c> se o usuário desistiu.</summary>
    public UserSettings? Result { get; private set; }

    /// <summary>Primeira execução fechada sem preencher: quem chamou deve encerrar o app.</summary>
    public bool ShouldExit { get; private set; }

    private void OnSave()
    {
        HideError(EmailError);
        HideError(WorkDayError);

        var email = EmailBox.Text.Trim();
        if (!EmailShape.IsMatch(email))
        {
            ShowError(EmailError, email.Length == 0
                ? "Informe o e-mail da conta Google que o Tempus vai ler."
                : "Isso não parece um e-mail.");
            EmailBox.Focus();
            return;
        }

        if (!TryReadTime(StartBox.Text, out var start)
            || !TryReadTime(MiddayBox.Text, out var midday)
            || !TryReadTime(LunchEndBox.Text, out var lunchEnd)
            || !TryReadTime(EndBox.Text, out var end))
        {
            ShowError(WorkDayError, "Horários em HH:MM, por exemplo 08:00.");
            return;
        }

        // A ordem importa porque os períodos das pausas saem dela: manhã é início→almoço e tarde é
        // volta→fim. Fora de ordem, os dois períodos ficariam negativos e nenhuma pausa existiria.
        if (start >= midday || midday > lunchEnd || lunchEnd >= end)
        {
            ShowError(WorkDayError, "Os horários precisam estar em ordem: início < almoço ≤ volta < fim.");
            return;
        }

        if (!int.TryParse(BreakMinutesBox.Text.Trim(), out var minutes) || minutes is < 5 or > 60)
        {
            ShowError(WorkDayError, "A duração da pausa precisa estar entre 5 e 60 minutos.");
            BreakMinutesBox.Focus();
            return;
        }

        Result = new UserSettings
        {
            LoginHint = email,
            WorkDay = new WorkDaySettings
            {
                StartHour = start.Hour,
                StartMinute = start.Minute,
                MiddayHour = midday.Hour,
                MiddayMinute = midday.Minute,
                LunchEndHour = lunchEnd.Hour,
                LunchEndMinute = lunchEnd.Minute,
                EndHour = end.Hour,
                EndMinute = end.Minute,
            },
            Breaks = new BreakSettings
            {
                Enabled = BreaksBox.IsChecked == true,
                DurationMinutes = minutes,
            },
        };

        DialogResult = true;
        Close();
    }

    private void OnCancel()
    {
        ShouldExit = _isFirstRun;
        Result = null;
        DialogResult = false;
        Close();
    }

    /// <summary>A duração só faz sentido com a pausa ligada; escondê-la evita um campo morto.</summary>
    private void SyncBreakRow() => BreakDurationRow.Visibility =
        BreaksBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private static string Format(int hour, int minute) => $"{hour:00}:{minute:00}";

    private static bool TryReadTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text.Trim(), "HH:mm", out time)
        || TimeOnly.TryParseExact(text.Trim(), "H:mm", out time);

    private void ShowError(System.Windows.Controls.TextBlock target, string message)
    {
        target.Text = message;
        target.Visibility = Visibility.Visible;
    }

    private static void HideError(System.Windows.Controls.TextBlock target) =>
        target.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Os controles padrão do WPF não herdam tema: sem isto a tela fica texto escuro sobre painel
    /// escuro. As chaves alimentam os <c>DynamicResource</c> dos estilos do XAML.
    /// </summary>
    private void ApplyPalette()
    {
        var foreground = new SolidColorBrush(_palette.BarForeground);

        Resources["Fg"] = foreground;
        Resources["FgMuted"] = new SolidColorBrush(_palette.Muted);
        Resources["FieldBg"] = new SolidColorBrush(_palette.RowHover);
        Resources["FieldBorder"] = new SolidColorBrush(_palette.PanelBorder);

        Background = new SolidColorBrush(_palette.PanelBackground);
        Shell.Background = new SolidColorBrush(_palette.PanelBackground);
        Foreground = foreground;
        Divider.Background = new SolidColorBrush(_palette.PanelBorder);

        // Vermelho de erro reusa o crítico da barra: é a mesma ideia — "isto não passa assim".
        EmailError.Foreground = new SolidColorBrush(_palette.CriticalBackground);
        WorkDayError.Foreground = new SolidColorBrush(_palette.CriticalBackground);
    }

    /// <summary>
    /// A barra de título é do Windows, não do WPF, e ficaria clara sobre uma janela escura. É a
    /// mesma chave que o Explorer usa; em build que não a conheça a chamada falha sem efeito.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        NativeMethods.UseDarkTitleBar(handle, _palette.IsDark);

        // A barra é WS_EX_NOACTIVATE (D-002), e um diálogo aberto a partir dela herda o não-foco:
        // sem isto a janela nasce atrás das outras e parece que o menu não fez nada.
        NativeMethods.BringToFront(handle);
        Activate();
        EmailBox.Focus();
    }
}
