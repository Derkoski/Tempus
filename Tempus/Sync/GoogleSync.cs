using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Google.Apis.Tasks.v1;
using Tempus.Domain;

// Sem "using Google.Apis.Tasks.v1.Data": ele traz um tipo Task que colide com o do TPL em cada
// assinatura async deste arquivo. Aliases nomeiam só o que é usado.
using GTask = Google.Apis.Tasks.v1.Data.Task;
using GTaskList = Google.Apis.Tasks.v1.Data.TaskList;

namespace Tempus.Sync;

/// <summary>
/// Traz agenda, tarefas e a contagem de e-mail não lido do Google, e mantém um retrato atual.
/// <para>
/// Sem webhooks: push do Google exige endpoint HTTPS público, o que não faz sentido para um app
/// local. Polling, com ritmo adaptativo (<see cref="SyncOptions"/>) — rápido enquanto o usuário
/// está na máquina, lento quando não está.
/// </para>
/// <para>
/// <b>Sem <c>syncToken</c>, ao contrário do que o roadmap previa.</b> Sync token é para
/// sincronizar um calendário inteiro e é incompatível com <c>timeMin</c>/<c>timeMax</c> — e o que
/// a barra precisa é só "os eventos de hoje". Uma consulta com janela de um dia é barata e
/// dispensa o estado extra de invalidação por HTTP 410.
/// </para>
/// </summary>
internal sealed class GoogleSync : IDisposable
{
    /// <summary>As listas de tarefas mudam raramente; relistar a cada ciclo é requisição jogada fora.</summary>
    private static readonly TimeSpan TaskListCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>O contador de e-mail não precisa do mesmo ritmo da agenda — nada nele é urgente.</summary>
    private static readonly TimeSpan MailInterval = TimeSpan.FromSeconds(60);

    private readonly GoogleAuth _auth;
    private readonly SyncOptions _options;
    private readonly string _mailQuery;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CalendarService? _calendar;
    private TasksService? _tasks;
    private GmailService? _gmail;
    private CancellationTokenSource? _cts;

    private IList<GTaskList>? _taskLists;
    private DateTimeOffset _taskListsAt;
    private int? _unreadMail;
    private DateTimeOffset _unreadAt;

    public GoogleSync(GoogleAuth auth, SyncOptions options, string mailQuery)
    {
        _auth = auth;
        _options = options;
        _mailQuery = mailQuery;
    }

    public event EventHandler<SyncSnapshot>? Updated;

    public SyncSnapshot Current { get; private set; } = SyncSnapshot.Starting;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    /// <summary>Re-consent a partir de um clique. Único caminho que pode abrir o navegador.</summary>
    public async Task ReauthorizeAsync()
    {
        var ct = _cts?.Token ?? CancellationToken.None;

        // O navegador pode demorar a aparecer, e sem isto o clique não dá retorno nenhum — o
        // usuário fica sem saber se o app registrou a ação ou se ela se perdeu.
        Publish(Current with { Health = SyncHealth.NeedsAuth, Message = "Abrindo o navegador…" });

        if (await ConnectAsync(interactive: true, ct)) await PollAsync(ct);
    }

    public Task RefreshAsync() => PollAsync(_cts?.Token ?? CancellationToken.None);

    public async Task<bool> CompleteTaskAsync(TaskItem task)
    {
        if (_tasks is null || task.ListId is null) return false;

        var ct = _cts?.Token ?? CancellationToken.None;

        try
        {
            await _tasks.Tasks.Patch(new GTask { Status = "completed" }, task.ListId, task.Id)
                .ExecuteAsync(ct);

            await PollAsync(ct);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> CreateTaskAsync(string title)
    {
        var listId = Current.DefaultTaskListId;
        if (_tasks is null || listId is null) return false;

        var ct = _cts?.Token ?? CancellationToken.None;

        try
        {
            await _tasks.Tasks.Insert(new GTask { Title = title }, listId).ExecuteAsync(ct);

            await PollAsync(ct);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- loop

    private async Task RunAsync(CancellationToken ct)
    {
        await ConnectAsync(interactive: false, ct);

        while (!ct.IsCancellationRequested)
        {
            await PollAsync(ct);

            try
            {
                await Task.Delay(NextDelay(), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Com o usuário na máquina, o intervalo curto define a latência de "criei no celular,
    /// apareceu na barra". Com a máquina ociosa ou bloqueada, ninguém está olhando — então o
    /// ritmo cai e o consumo em repouso volta a ser indistinguível de zero (regra 9).
    /// </summary>
    private TimeSpan NextDelay() =>
        UserIdle.Duration() > _options.IdleAfter ? _options.Idle : _options.Active;

    private async Task<bool> ConnectAsync(bool interactive, CancellationToken ct)
    {
        if (!_auth.HasClientSecret)
        {
            Publish(Current with
            {
                Health = SyncHealth.NotConfigured,
                Message = "Falta o client_secret.json — clique para ver onde colocar",
            });
            return false;
        }

        try
        {
            var credential = interactive
                ? await _auth.AuthorizeAsync(ct)
                : await _auth.TryRestoreAsync(ct);

            if (credential is null)
            {
                Publish(Current with
                {
                    Health = SyncHealth.NeedsAuth,
                    Message = await _auth.HasStoredTokenAsync()
                        ? "Login do Google expirou — clique para entrar"
                        : "Conectar ao Google — clique para entrar",
                });
                return false;
            }

            var initializer = new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Tempus",
            };

            _calendar = new CalendarService(initializer);
            _tasks = new TasksService(initializer);
            _gmail = new GmailService(initializer);

            _taskLists = null; // conta pode ter mudado
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Publish(Current with
            {
                Health = SyncHealth.NeedsAuth,
                Message = $"Falha ao autenticar: {ex.Message}",
            });
            return false;
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return; // uma rodada por vez

        try
        {
            if (_calendar is null || _tasks is null)
            {
                if (!await ConnectAsync(interactive: false, ct)) return;
            }

            var (agenda, upcoming) = await ReadAgendaAsync(ct);
            var (tasks, defaultList) = await ReadTasksAsync(ct);
            var unread = await ReadUnreadMailAsync(ct);

            Publish(new SyncSnapshot
            {
                Health = SyncHealth.Ok,
                Agenda = agenda,
                Upcoming = upcoming,
                Tasks = tasks,
                UnreadMail = unread,
                DefaultTaskListId = defaultList,
                LastSuccessAt = DateTimeOffset.Now,
                Message = null,
            });
        }
        catch (TokenResponseException)
        {
            // Token revogado no meio do caminho. Descartar os serviços força reconexão.
            _calendar = null;
            _tasks = null;
            _gmail = null;

            Publish(Current with
            {
                Health = SyncHealth.NeedsAuth,
                Message = "Login do Google expirou — clique para entrar",
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Publish(Current with
            {
                Health = SyncHealth.Failing,
                Message = $"Sync falhando: {ex.Message}",
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Publish(SyncSnapshot snapshot)
    {
        Current = snapshot;
        Updated?.Invoke(this, snapshot);
    }

    // ---------------------------------------------------------------- leitura

    /// <summary>Quantos dias além de hoje a consulta cobre, para alimentar o <see cref="Lookahead"/>.</summary>
    private const int LookaheadDays = 8;

    /// <summary>
    /// Uma única consulta cobre hoje e a semana seguinte; o resultado é fatiado depois. Duas
    /// consultas separadas dobrariam as requisições para obter o mesmo, e uma semana de eventos
    /// são poucos KB.
    /// </summary>
    private async Task<(IReadOnlyList<AgendaItem> Today, IReadOnlyList<AgendaItem> Upcoming)>
        ReadAgendaAsync(CancellationToken ct)
    {
        var dayStart = new DateTimeOffset(DateTime.Today);

        var request = _calendar!.Events.List("primary");
        request.TimeMinDateTimeOffset = dayStart;
        request.TimeMaxDateTimeOffset = dayStart.AddDays(LookaheadDays);
        request.SingleEvents = true; // expande recorrências em ocorrências
        request.ShowDeleted = false;
        request.MaxResults = 250;
        request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

        var response = await request.ExecuteAsync(ct);

        var today = new List<AgendaItem>();
        var upcoming = new List<AgendaItem>();
        var tomorrow = DateTime.Today.AddDays(1);

        foreach (var ev in response.Items ?? [])
        {
            if (ShouldIgnore(ev)) continue;
            if (Map(ev) is not { } item) continue;

            if (item.Start.ToLocalTime().Date < tomorrow) today.Add(item);
            else upcoming.Add(item);
        }

        return (today, upcoming);
    }

    /// <summary>Regras do <c>SEVERITY.md</c> §2.1 sobre o que não é compromisso de verdade.</summary>
    private static bool ShouldIgnore(Event ev)
    {
        if (ev.Status == "cancelled") return true;
        if (ev.Transparency == "transparent") return true; // marcado como Livre
        if (ev.EventType is "outOfOffice" or "focusTime" or "workingLocation") return true;

        return ev.Attendees?.FirstOrDefault(a => a.Self == true)?.ResponseStatus == "declined";
    }

    private static AgendaItem? Map(Event ev)
    {
        var isAllDay = ev.Start?.Date is not null;
        DateTimeOffset start;
        DateTimeOffset end;

        if (isAllDay)
        {
            if (!DateTime.TryParse(ev.Start!.Date, out var date)) return null;

            start = new DateTimeOffset(date.Date);
            end = start.AddDays(1);
        }
        else
        {
            if (ev.Start?.DateTimeDateTimeOffset is not { } from) return null;

            start = from;
            end = ev.End?.DateTimeDateTimeOffset ?? from;
        }

        return new AgendaItem
        {
            Id = ev.Id ?? $"{start:O}-{ev.Summary}",
            Title = string.IsNullOrWhiteSpace(ev.Summary) ? "(sem título)" : ev.Summary,
            Start = start,
            End = end,
            IsAllDay = isAllDay,
            Conference = ExtractConference(ev),
            Rsvp = ExtractRsvp(ev),
        };
    }

    /// <summary>
    /// Sua resposta ao convite (D-018). Sem lista de convidados não há resposta a dar — é
    /// compromisso próprio, e marcar "pendente" nele seria cobrar uma ação que não existe.
    /// </summary>
    private static Rsvp ExtractRsvp(Event ev)
    {
        var self = ev.Attendees?.FirstOrDefault(a => a.Self == true);
        if (self is null) return Rsvp.None;

        return self.ResponseStatus switch
        {
            "accepted" => Rsvp.Accepted,
            "tentative" => Rsvp.Tentative,
            "needsAction" => Rsvp.NeedsAction,
            // "declined" não chega aqui: ShouldIgnore descarta antes.
            _ => Rsvp.None,
        };
    }

    /// <summary>
    /// Acha a call em cascata, do campo estruturado ao texto livre (D-017).
    /// <para>
    /// As duas primeiras fontes são campos que o Google preenche: o que está ali <b>é</b> a call,
    /// então qualquer URL serve. As duas últimas são texto digitado por gente, e aí só passa
    /// provedor reconhecido — um convite carrega rastreador, anexo e link de descadastro, e abrir
    /// o primeiro que aparecer seria um clique no escuro.
    /// </para>
    /// </summary>
    private static Conference? ExtractConference(Event ev)
    {
        var video = ev.ConferenceData?.EntryPoints?
            .FirstOrDefault(e => e.EntryPointType == "video");

        return Conference.FromUrl(video?.Uri)
            ?? Conference.FromUrl(ev.HangoutLink)
            ?? Conference.FindIn(ev.Location)
            ?? Conference.FindIn(ev.Description);
    }

    private async Task<(IReadOnlyList<TaskItem> Tasks, string? DefaultList)>
        ReadTasksAsync(CancellationToken ct)
    {
        var lists = await GetTaskListsAsync(ct);
        var result = new List<TaskItem>();
        string? defaultList = null;

        foreach (var list in lists)
        {
            if (list.Id is null) continue;

            defaultList ??= list.Id; // a primeira lista é a padrão do Google Tasks

            var request = _tasks!.Tasks.List(list.Id);
            request.ShowCompleted = false;
            request.ShowHidden = false;
            request.MaxResults = 100;

            var response = await request.ExecuteAsync(ct);

            foreach (var task in response.Items ?? [])
            {
                if (task.Id is null || string.IsNullOrWhiteSpace(task.Title)) continue;
                if (task.Deleted == true) continue;

                result.Add(new TaskItem
                {
                    Id = task.Id,
                    ListId = list.Id,
                    Title = task.Title,
                    Due = ReadDue(task),
                    IsCompleted = task.Status == "completed",
                });
            }
        }

        return (result, defaultList);
    }

    private async Task<IList<GTaskList>> GetTaskListsAsync(CancellationToken ct)
    {
        if (_taskLists is not null && DateTimeOffset.Now - _taskListsAt < TaskListCacheTtl)
            return _taskLists;

        var response = await _tasks!.Tasklists.List().ExecuteAsync(ct);

        _taskLists = response.Items ?? [];
        _taskListsAt = DateTimeOffset.Now;
        return _taskLists;
    }

    /// <summary>
    /// Conta <b>conversas</b> que casam com <see cref="GoogleOptions.MailQuery"/>.
    /// <para>
    /// Antes isto lia <c>messagesUnread</c> do rótulo INBOX, o que errava por dois motivos:
    /// contava mensagens em vez de conversas (uma thread com 5 respostas não lidas virava 5, e o
    /// Gmail mostra 1), e o rótulo INBOX abrange todas as abas — Promoções, Social e Atualizações
    /// entravam na conta.
    /// </para>
    /// <para>
    /// Só os identificadores das conversas trafegam; nenhum assunto, remetente ou corpo é lido.
    /// Ler conteúdo de e-mail está na lista de fora-de-escopo do SPEC.
    /// </para>
    /// <para>
    /// Uma página de 100 basta: a barra exibe <c>99+</c> acima disso, então contar além de 100
    /// seria paginar para produzir um número que nunca apareceria.
    /// </para>
    /// </summary>
    private async Task<int?> ReadUnreadMailAsync(CancellationToken ct)
    {
        if (_gmail is null) return _unreadMail;
        if (_unreadMail is not null && DateTimeOffset.Now - _unreadAt < MailInterval) return _unreadMail;

        try
        {
            var request = _gmail.Users.Threads.List("me");
            request.Q = _mailQuery;
            request.MaxResults = 100;

            var response = await request.ExecuteAsync(ct);

            _unreadMail = response.Threads?.Count ?? 0;
            _unreadAt = DateTimeOffset.Now;
        }
        catch (Exception)
        {
            // Nunca derrubar a rodada por causa do e-mail: agenda e tarefas importam mais, e o
            // caso comum de falha aqui é o token ainda não ter o escopo do Gmail (recém-adicionado,
            // pendente de re-consent). O contador fica em "—", que é honesto, e o resto segue.
        }

        return _unreadMail;
    }

    /// <summary>
    /// O Google grava vencimento como meia-noite <b>UTC</b> do dia, e descarta a hora. Converter
    /// para horário local antes de extrair a data jogaria o dia para trás em qualquer fuso
    /// negativo — no Brasil, toda tarefa apareceria vencendo um dia antes.
    /// </summary>
    private static DateOnly? ReadDue(GTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Due)) return null;

        return DateTimeOffset.TryParse(
            task.Due,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var due)
            ? DateOnly.FromDateTime(due.UtcDateTime.Date)
            : null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _calendar?.Dispose();
        _tasks?.Dispose();
        _gmail?.Dispose();
        _gate.Dispose();
    }
}
