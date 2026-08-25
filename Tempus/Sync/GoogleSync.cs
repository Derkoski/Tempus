using System.IO;
using Google;
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

    /// <summary>Calendário novo é evento raro; relistar a cada ciclo é requisição jogada fora.</summary>
    private static readonly TimeSpan CalendarCacheTtl = TimeSpan.FromMinutes(10);

    private readonly GoogleAuth _auth;
    private readonly SyncOptions _options;
    private readonly GoogleOptions _google;
    private readonly string _mailQuery;

    private IReadOnlyList<CalendarSource>? _calendars;
    private DateTimeOffset _calendarsAt;

    /// <summary>Por quantos dias para trás as concluídas continuam alcançáveis (D-030).</summary>
    private readonly int _completedDays;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private CalendarService? _calendar;
    private TasksService? _tasks;
    private GmailService? _gmail;
    private CancellationTokenSource? _cts;

    private IList<GTaskList>? _taskLists;
    private DateTimeOffset _taskListsAt;
    private int? _unreadMail;
    private DateTimeOffset _unreadAt;

    public GoogleSync(GoogleAuth auth, SyncOptions options, GoogleOptions google, int completedDays = 7)
    {
        _auth = auth;
        _options = options;
        _google = google;
        _mailQuery = google.MailQuery;
        _completedDays = completedDays;
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

    /// <summary>
    /// Executa uma intenção de escrita. É o executor que a <see cref="WriteQueue"/> injeta.
    /// <para>
    /// <b>Nunca lança e nunca engole em silêncio:</b> devolve sucesso ou o código HTTP que a fila
    /// precisa para decidir se repete. Era o silêncio da versão anterior — <c>catch { return
    /// false; }</c> com o resultado descartado por quem chamava — que fazia um clique sumir sem
    /// deixar rastro.
    /// </para>
    /// <para>
    /// Também <b>não sincroniza no fim</b>. Antes cada escrita disparava um <c>PollAsync</c>
    /// completo — calendário, tarefas e Gmail — só para a tela reagir. A projeção otimista já faz
    /// a tela reagir, e três cliques seguidos não precisam custar três leituras de tudo.
    /// </para>
    /// </summary>
    public async Task<WriteOutcome> ApplyAsync(PendingWrite write, CancellationToken ct)
    {
        if (_tasks is null) return WriteOutcome.Failed(null, "Ainda não conectado ao Google");

        // A lista pode não existir no momento do clique — o app pode ter subido sem sync ainda.
        // Resolver aqui, e não no enfileiramento, deixa a intenção sobreviver a esse intervalo.
        var listId = write.ListId ?? Current.DefaultTaskListId;
        if (listId is null) return WriteOutcome.Failed(null, "Nenhuma lista de tarefas conhecida");

        try
        {
            switch (write.Kind)
            {
                case WriteKind.Create:
                    await _tasks.Tasks.Insert(new GTask { Title = write.Title }, listId)
                        .ExecuteAsync(ct);
                    break;

                case WriteKind.Complete:
                    await _tasks.Tasks
                        .Patch(new GTask { Status = "completed" }, listId, write.TaskId)
                        .ExecuteAsync(ct);
                    break;

                // Voltar o status para needsAction limpa o carimbo de conclusão e desesconde a
                // tarefa; não é preciso mexer em `completed` nem em `hidden` à mão.
                case WriteKind.Reopen:
                    await _tasks.Tasks
                        .Patch(new GTask { Status = "needsAction" }, listId, write.TaskId)
                        .ExecuteAsync(ct);
                    break;

                // Excluir não tem volta: a API do Google Tasks não expõe lixeira nem restauração,
                // então quem enfileirou precisa ter confirmado antes (D-024).
                case WriteKind.Delete:
                    await _tasks.Tasks.Delete(listId, write.TaskId).ExecuteAsync(ct);
                    break;

                case WriteKind.Reschedule:
                    await RescheduleAsync(listId, write, ct);
                    break;

                // Título é campo de texto: um Patch simples basta, e o alerta do Reschedule não se
                // aplica — a UI nunca envia vazio, então nunca há nada a "limpar" aqui.
                case WriteKind.Rename:
                    await _tasks.Tasks
                        .Patch(new GTask { Title = write.Title }, listId, write.TaskId)
                        .ExecuteAsync(ct);
                    break;
            }

            return WriteOutcome.Ok;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GoogleApiException ex)
        {
            return WriteOutcome.Failed((int)ex.HttpStatusCode, ex.Error?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            // Sem resposta HTTP: rede, DNS, timeout. A política trata como passageiro.
            return WriteOutcome.Failed(null, ex.Message);
        }
    }

    /// <summary>
    /// Relê só as tarefas. Chamada quando a fila de escritas esvazia, para trocar as linhas
    /// provisórias pelas de verdade sem pagar uma rodada completa de sync.
    /// </summary>
    public async Task RefreshTasksAsync()
    {
        if (_tasks is null) return;

        var ct = _cts?.Token ?? CancellationToken.None;

        try
        {
            var (tasks, defaultList) = await ReadTasksAsync(ct);

            // LastSuccessAt fica de fora de propósito: ele responde por "o retrato inteiro é
            // confiável", e uma leitura parcial não pode adiar a ida para Offline (§0).
            Publish(Current with { Tasks = tasks, DefaultTaskListId = defaultList });
        }
        catch (Exception)
        {
            // Falhar aqui não muda nada de importante: o próximo ciclo de sync relê tudo.
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
    /// <summary>
    /// Quantos dias de agenda o retrato carrega. Subiu de 8 para 15 no D-043, quando o painel
    /// passou a mostrar os próximos dias.
    /// <para>
    /// <b>Alargar a janela não acrescenta requisição nenhuma</b> — continua uma por calendário por
    /// ciclo, só com um intervalo maior, e <c>MaxResults = 250</c> cobre quinze dias com folga. Era
    /// a alternativa a buscar sob demanda, que traria estado de carregamento, erro por dia e cache
    /// a invalidar: três modos de falha novos por um alcance que raramente se usa.
    /// </para>
    /// </summary>
    private const int LookaheadDays = 15;

    /// <summary>
    /// Uma única consulta cobre hoje e a semana seguinte; o resultado é fatiado depois. Duas
    /// consultas separadas dobrariam as requisições para obter o mesmo, e uma semana de eventos
    /// são poucos KB.
    /// </summary>
    private async Task<(IReadOnlyList<AgendaItem> Today, IReadOnlyList<AgendaItem> Upcoming)>
        ReadAgendaAsync(CancellationToken ct)
    {
        var all = new List<AgendaItem>();

        // O principal primeiro, sempre: na fusão vence quem chega antes, e é dele que vêm o RSVP e
        // o conferenceData que o calendário assinado não tem (D-034).
        foreach (var calendar in await GetCalendarsAsync(ct))
            all.AddRange(await ReadCalendarAsync(calendar, ct));

        var today = new List<AgendaItem>();
        var upcoming = new List<AgendaItem>();
        var tomorrow = DateTime.Today.AddDays(1);

        foreach (var item in AgendaMerge.Dedupe(all))
        {
            if (item.Start.ToLocalTime().Date < tomorrow) today.Add(item);
            else upcoming.Add(item);
        }

        // A ordem por horário vinha do OrderBy da consulta; com vários calendários fundidos ela
        // precisa ser refeita, senão os eventos do segundo calendário viriam todos depois.
        today.Sort((a, b) => a.Start.CompareTo(b.Start));
        upcoming.Sort((a, b) => a.Start.CompareTo(b.Start));

        return (today, upcoming);
    }

    private async Task<List<AgendaItem>> ReadCalendarAsync(CalendarSource calendar, CancellationToken ct)
    {
        var dayStart = new DateTimeOffset(DateTime.Today);

        var request = _calendar!.Events.List(calendar.Id);
        request.TimeMinDateTimeOffset = dayStart;
        request.TimeMaxDateTimeOffset = dayStart.AddDays(LookaheadDays);
        request.SingleEvents = true; // expande recorrências em ocorrências
        request.ShowDeleted = false;
        request.MaxResults = 250;
        request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

        var items = new List<AgendaItem>();

        try
        {
            var response = await request.ExecuteAsync(ct);

            foreach (var ev in response.Items ?? [])
            {
                if (ShouldIgnore(ev)) continue;
                if (Map(ev) is not { } item) continue;

                items.Add(item with { Source = calendar.Label });
            }
        }
        catch (Exception) when (!calendar.IsPrimary)
        {
            // Um calendário secundário que falha não pode derrubar a agenda inteira: perder o
            // import do Teams é ruim, perder também o principal seria pior. O principal continua
            // propagando o erro, porque sem ele não há retrato nenhum.
        }

        return items;
    }

    /// <summary>
    /// Quais calendários ler (D-034). Cacheado como as listas de tarefas: calendário novo é evento
    /// raro, e relistar a cada ciclo seria requisição jogada fora.
    /// </summary>
    private async Task<IReadOnlyList<CalendarSource>> GetCalendarsAsync(CancellationToken ct)
    {
        if (_calendars is not null && DateTimeOffset.Now - _calendarsAt < CalendarCacheTtl)
            return _calendars;

        var sources = new List<CalendarSource>();

        try
        {
            var response = await _calendar!.CalendarList.List().ExecuteAsync(ct);

            foreach (var entry in response.Items ?? [])
            {
                if (entry.Id is null) continue;
                if (IsIgnored(entry)) continue;

                // O principal SEMPRE, e os demais só se visíveis no Google Agenda. A ordem importa:
                // `selected` é documentado como "Optional. The default is False", e o principal
                // costuma vir sem o campo — filtrar só por ele excluiria justamente o calendário
                // que não pode faltar.
                var primary = entry.Primary == true;
                if (!primary && entry.Selected != true) continue;

                sources.Add(new CalendarSource(entry.Id, LabelFor(entry), primary));
            }
        }
        catch (Exception)
        {
            // Sem a lista, degrada para o comportamento antigo em vez de ficar sem agenda.
        }

        // Rede fora ou permissão negada: o principal sozinho ainda responde a maior parte.
        if (sources.Count == 0) sources.Add(new CalendarSource("primary", null, IsPrimary: true));

        _calendars = [.. sources.OrderByDescending(c => c.IsPrimary)];
        _calendarsAt = DateTimeOffset.Now;

        return _calendars;
    }

    private bool IsIgnored(CalendarListEntry entry) =>
        _google.IgnoredCalendars.Any(ignored =>
            string.Equals(ignored, entry.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ignored, entry.Summary, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Como a origem aparece no painel. <c>null</c> no principal — é o caso comum, e anunciá-lo
    /// seria ruído em toda linha.
    /// </summary>
    private string? LabelFor(CalendarListEntry entry)
    {
        if (entry.Primary == true) return null;

        foreach (var (key, label) in _google.CalendarLabels)
            if (string.Equals(key, entry.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, entry.Summary, StringComparison.OrdinalIgnoreCase))
                return label;

        return entry.SummaryOverride ?? entry.Summary;
    }

    private sealed record CalendarSource(string Id, string? Label, bool IsPrimary);

    /// <summary>
    /// <c>--dump-agenda</c>: escreve num arquivo quais calendários foram lidos e o que veio de
    /// cada um. Irmã da <c>--dump-tasks</c> (D-031), pelo mesmo motivo: "não aparece" pode ser
    /// calendário não lido, evento filtrado pelo §2.1, ou janela de tempo — e olhar a barra não
    /// distingue os três.
    /// </summary>
    public async Task<string> DumpAgendaAsync(string path)
    {
        var ct = _cts?.Token ?? CancellationToken.None;

        if (_calendar is null && !await ConnectAsync(interactive: false, ct))
            return "Não conectou ao Google — sem token válido?";

        var lines = new List<string>();

        foreach (var calendar in await GetCalendarsAsync(ct))
        {
            lines.Add($"=== '{calendar.Label ?? "(principal)"}'  id={calendar.Id}");

            var items = await ReadCalendarAsync(calendar, ct);

            foreach (var item in items.OrderBy(i => i.Start))
            {
                lines.Add(
                    $"  {item.Start.ToLocalTime():dd/MM HH:mm}–{item.End.ToLocalTime():HH:mm}  " +
                    $"{item.Title}{(item.IsAllDay ? "  [dia inteiro]" : "")}\n" +
                    $"      call: {item.Conference?.Url ?? "(nenhuma)"}");
            }

            if (items.Count == 0) lines.Add("  (nada na janela consultada)");
        }

        await File.WriteAllTextAsync(path, string.Join("\n", lines), ct);
        return path;
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

            var open = _tasks!.Tasks.List(list.Id);
            open.ShowCompleted = false;
            open.ShowHidden = false;
            open.ShowAssigned = true;
            open.MaxResults = 100;

            result.AddRange(Read(await open.ExecuteAsync(ct), list.Id));

            // Segunda requisição, e não `showCompleted` na primeira, por dois motivos. Um: com
            // `MaxResults` compartilhado, um monte de concluídas empurraria tarefas abertas para
            // fora do retrato — perder aberta para mostrar concluída é o pior negócio possível.
            // Dois: `completedMin` filtra por data de conclusão, e é plausível que exclua quem não
            // tem nenhuma, que é justamente toda tarefa aberta. Separadas, cada consulta tem um
            // trabalho e nenhuma das dúvidas importa.
            var done = _tasks.Tasks.List(list.Id);
            done.ShowCompleted = true;
            done.ShowHidden = true; // no Google Tasks, concluir também esconde
            done.ShowAssigned = true;
            done.CompletedMin = DateTime.UtcNow
                .AddDays(-Math.Max(1, _completedDays))
                .ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            done.MaxResults = 100;

            // Filtrado no cliente também: a consulta pode devolver abertas, e elas já vieram da
            // primeira — duplicar contaria a mesma tarefa duas vezes no chip da barra.
            result.AddRange(Read(await done.ExecuteAsync(ct), list.Id).Where(t => t.IsCompleted));
        }

        return (result, defaultList);
    }

    private static IEnumerable<TaskItem> Read(Google.Apis.Tasks.v1.Data.Tasks response, string listId)
    {
        foreach (var task in response.Items ?? [])
        {
            if (task.Id is null || string.IsNullOrWhiteSpace(task.Title)) continue;
            if (task.Deleted == true) continue;

            yield return new TaskItem
            {
                Id = task.Id,
                ListId = listId,
                Title = task.Title,
                Due = ReadDue(task),
                IsCompleted = task.Status == "completed",
                CompletedAt = ReadCompleted(task),
            };
        }
    }

    /// <summary>
    /// <c>--dump-tasks</c>: escreve num arquivo o que o Google devolveu, cru e já convertido.
    /// <para>
    /// Existe porque "a data não apareceu" tem três culpados possíveis — o Google não mandou, o
    /// parse errou, ou a tela não desenhou — e olhar a barra não distingue os três. Aqui só a
    /// primeira metade do caminho aparece, então o que sobra fica isolado.
    /// </para>
    /// </summary>
    public async Task<string> DumpTasksAsync(string path)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        var lines = new List<string>();

        if (_tasks is null && !await ConnectAsync(interactive: false, ct))
            return "Não conectou ao Google — sem token válido?";

        foreach (var list in await GetTaskListsAsync(ct))
        {
            if (list.Id is null) continue;

            lines.Add($"=== lista '{list.Title}' ({list.Id})");

            var request = _tasks!.Tasks.List(list.Id);
            request.ShowCompleted = false;
            request.ShowHidden = false;
            request.ShowAssigned = true;
            request.MaxResults = 100;

            foreach (var task in (await request.ExecuteAsync(ct)).Items ?? [])
            {
                var origem = task.AssignmentInfo is { } info
                    ? $"{info.SurfaceType} · {info.LinkToTask}"
                    : "(própria)";

                lines.Add(
                    $"  título   : {task.Title}\n" +
                    $"  origem   : {origem}\n" +
                    $"  due lido : {ReadDue(task)?.ToString("yyyy-MM-dd") ?? "(null)"}\n" +
                    $"  JSON cru : {Google.Apis.Json.NewtonsoftJsonSerializer.Instance.Serialize(task)}\n");
            }
        }

        await File.WriteAllTextAsync(path, string.Join("\n", lines), ct);
        return path;
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
    /// Define ou apaga o vencimento (D-042). Os dois caminhos são <b>diferentes</b>, e é aí que
    /// mora a armadilha desta operação.
    /// <para>
    /// <b>Apagar não pode ser um <c>Patch</c> com <c>Due = null</c>.</b> O serializador do cliente
    /// Google usa <c>NullValueHandling.Ignore</c>, então o campo nulo simplesmente não é enviado —
    /// e um PATCH sem <c>due</c> quer dizer "não mexa no due". A chamada voltaria sucesso e a data
    /// continuaria lá: falha silenciosa, da mesma família do D-031 e do D-040.
    /// </para>
    /// <para>
    /// Apagar exige PUT, que substitui o recurso. E aí mora o perigo de verdade: montar o
    /// <c>GTask</c> à mão a partir do que temos apagaria as <b>notas</b> da tarefa, porque
    /// <c>TaskItem</c> não as carrega — perda de dado causada por um gesto de limpar data. Daí o
    /// <c>get</c> antes: o objeto vem completo do servidor, e o PUT devolve tudo menos o
    /// vencimento.
    /// </para>
    /// </summary>
    private async Task RescheduleAsync(string listId, PendingWrite write, CancellationToken ct)
    {
        if (write.Due is { } due)
        {
            // Meia-noite UTC, no formato que a medição 3 do D-031 provou funcionar nos dois
            // sentidos. Espelha o ReadDue: lá se extrai a data do UTC, aqui se escreve nele.
            var stamp = due.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

            await _tasks!.Tasks
                .Patch(new GTask { Due = stamp }, listId, write.TaskId)
                .ExecuteAsync(ct);

            return;
        }

        var atual = await _tasks!.Tasks.Get(listId, write.TaskId).ExecuteAsync(ct);
        atual.Due = null;

        await _tasks.Tasks.Update(atual, listId, write.TaskId).ExecuteAsync(ct);
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

    /// <summary>
    /// Instante da conclusão. Aqui a hora <b>importa</b> — é o que ordena as concluídas da mais
    /// recente para a mais antiga, deixando a marcada sem querer no topo (D-030). Por isso, ao
    /// contrário do vencimento, este converte para o fuso local.
    /// </summary>
    private static DateTimeOffset? ReadCompleted(GTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Completed)) return null;

        return DateTimeOffset.TryParse(
            task.Completed,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var completed)
            ? completed.ToLocalTime()
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
