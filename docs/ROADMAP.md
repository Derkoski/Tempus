# Roadmap

Regra que governa este roadmap: **cada fase termina em algo que roda.** Nunca uma fase que
entrega "a camada de serviços" sem nada visível. Isso mantém o risco descoberto cedo e dá
ponto de parada limpo em qualquer sessão.

As fases 1 e 2 são **independentes** — se a TI da TOTVS demorar com o OAuth client (D-003),
a Fase 2 anda inteira contra dados falsos.

---

## Fase 0 — Fundação ✅ *concluída em 2026-08-07*

- [x] `git init` + `.gitignore` com proteção de segredos
- [x] `CLAUDE.md` — briefing de sessão
- [x] `docs/SPEC.md` — spec de produto e critérios de aceite
- [x] `docs/SEVERITY.md` — modelo de severidade e cor
- [x] `docs/DECISIONS.md` — D-001 a D-005
- [x] `docs/ROADMAP.md`

---

## Fase 1 — Spike de OAuth e dados reais

**Entrega:** um app de console que autentica e imprime a agenda de hoje e as tarefas abertas,
lidas da conta real.

### 1a — Teste go/no-go ✅ *passou em 2026-08-11*

O único risco que podia matar o projeto inteiro (D-003).

- [x] Projeto GCP — **numa conta pessoal `@gmail.com`**, não na TOTVS: a conta corporativa não tem
      `resourcemanager.projects.create`, e conta Workspace fica presa à organização dela
- [x] OAuth client Desktop, consent screen External + Testing, **e-mail da TOTVS como test user**
- [x] Consent com a conta corporativa: **o admin permitiu** — o *App access control* não barra
- [x] Chamadas reais de Calendar e Tasks retornando dados
- [x] Token gravado cifrado com DPAPI (cabeçalho de blob conferido, não é JSON em claro)

O fallback de iCal fica arquivado, sem necessidade de uso.

### 1b — Integração *(código pronto, esperando credenciais)*

- [x] Fluxo OAuth com loopback local, token persistido com **DPAPI** (`DpapiDataStore`) — o
      `FileDataStore` padrão do Google grava JSON em claro, o que a regra 5 não permite
- [x] `login_hint` na URL de consent, via `GoogleAuthorizationCodeFlow` estendido
- [x] Separação entre restauração **silenciosa** (startup, nunca abre navegador) e autorização
      **interativa** (só a partir de clique) — com D-003 permanente, "token expirado" é rotina
      semanal e abrir navegador sozinho seria hostil
- [x] `Google.Apis.Calendar.v3` — eventos de hoje, com filtro de `cancelled`, `transparent`,
      `declined`, `outOfOffice`/`focusTime`, dia inteiro e extração do link de Meet
- [x] `Google.Apis.Tasks.v1` — tarefas abertas de todas as listas, com vencimento lido em **UTC**
      (converter para local jogaria o dia para trás em fuso negativo)
- [x] Escrita: concluir e criar tarefa (antecipado da Fase 4 — a UI já existia e sem isso mentiria)
- [x] Estado `Offline` por `NeedsAuth`, `NotConfigured` ou sync > 10 min, com contadores em `—`
- [x] Re-consent sem reiniciar o app
- [ ] **Credenciais do GCP** — depende do usuário; é o go/no-go de 1a

**Desvio consciente do plano original:** sem `syncToken`. Ele serve para sincronizar um calendário
inteiro e é **incompatível com `timeMin`/`timeMax`** — e o que a barra precisa é só "os eventos de
hoje". Uma consulta com janela de um dia é uma requisição por minuto, folgada na quota, e dispensa
o estado extra de invalidação por HTTP 410.

---

## Fase 2 — Spike visual da barra

**Entrega:** a barra visível sobre a taskbar com dados falsos, passando o critério de aceite 1
do `SPEC.md`.

- [x] Descartar o scaffold `Microsoft.NET.Sdk.Web`; criar projeto WPF `net8.0-windows`
- [x] Interface `IShellSurface` + `FloatingBarSurface` (D-002)
- [x] Janela sem borda: `WS_EX_TOOLWINDOW`, `WS_EX_NOACTIVATE`, topmost, fora da taskbar
      — os três estilos verificados por `GetWindowLongPtr`
- [x] Posicionamento via rect do `Shell_TrayWnd`, com teto rígido contra a bandeja
- [x] Largura de 12 slots (480px): 5 slots deixavam ~64px de texto, pequeno demais
- [x] Reafirmação de ordem Z a cada 1s — ver "Achado da Fase 2" em D-002
- [x] Robustez escrita: `TaskbarCreated`, `WM_DPICHANGED`, `WM_DISPLAYCHANGE`, lock/unlock
- [x] Esconder em tela cheia via `SHQueryUserNotificationState`
- [x] Layout: relógio · motivo · contador de tarefas · contador de e-mail
- [x] Consumo em repouso: 0,13% de um núcleo (critério de aceite 10)
- [x] Painéis S2 e S3 construídos, com dados falsos e o laço painel→barra
- [ ] **Verificar que os painéis abrem.** Escritos e compilando, mas nunca exercitados: a única
      tentativa com coordenadas corretas ocorreu enquanto a barra ainda estava atrás da taskbar,
      então o clique foi para o explorer. Não testado ≠ quebrado.
- [ ] Exercitar `TaskbarCreated` com restart real do explorer (o handler existe, nunca rodou)
- [ ] Verificar `WindowFromPoint` como teste de propriedade dos pixels, não só de retângulo

**Risco que esta fase mata:** o posicionamento sobre a taskbar ser inviável na prática. Segue de
pé, mas o achado da ordem Z mostrou que a fragilidade prevista em D-002 é real e se manifesta em
minutos de uso, não em updates do Windows. Se reaparecer sob outra forma, `TraySurface` entra sem
tocar em lógica de domínio.

---

## Fase 3 — Máquina de estados, cores e toasts

**Entrega:** a barra reage aos dados reais da Fase 1 com as cores e avisos corretos.

- [x] Questões abertas fechadas — nenhuma pendência bloqueando esta fase
- [ ] Estado `Offline` que precede e anula a escala (`SEVERITY.md` §0), com contadores em `—`
- [ ] Identidade de ocorrência `(eventId, início, fim)` e supressão por ocorrência (D-010)
- [ ] `MeetingAmbiguous` + seletor de evento ativo, com padrão determinístico (D-009)
- [ ] Sinais como funções puras, testáveis sem UI nem rede
- [ ] Arbitragem com desempate por categoria (`SEVERITY.md` §4)
- [ ] Histerese de 20s na descida, subida imediata (I4, I5)
- [ ] **Reconhecimento por clique**: suprime a ocorrência, volta a `Calm` imediatamente (D-006)
- [ ] **Escalada do nível 3**: vermelho sólido → pisca âmbar↔vermelho após 5 min, período ~1,2s,
      sólido se as animações do sistema estiverem desligadas
- [ ] Persistência da supressão de `DayEnded` até a virada do dia
- [ ] Testes das invariantes I1–I8
- [ ] Toasts com AUMID registrado, com deduplicação e supressão em apresentação
- [ ] Feriados nacionais computados localmente, com Páscoa por Meeus/Gauss (D-008)
- [ ] Sinais de fronteira do dia com 12:00 e 17:00 configuráveis, e `DayEnded.CountMode` (D-007)

**Marco:** ao fim desta fase o produto já entrega o valor central. Fases 4 e 5 são
complementos.

---

## Fase 4 — Tasks bidirecional

**Entrega:** criar/concluir/editar tarefa no painel reflete no Google Tasks e vice-versa.

- [ ] Escritas: criar, concluir, editar título e vencimento
- [ ] Fila local de escritas offline, reproduzida ao voltar a conexão
- [ ] Resolução de conflito: última escrita vence, Google ganha em empate
- [ ] Atualização otimista da UI com reversão em caso de falha
- [ ] Critério de aceite 6 do `SPEC.md` verificado nos dois sentidos

---

## Fase 5 — Gmail

**Entrega:** contador de não-lidos real.

- [ ] Adicionar `gmail.readonly` ao consent (restricted, mas permitido em Testing mode para test
      users — exige novo consent, não exige verificação)
- [ ] `users.labels.get` no `UNREAD` → `messagesUnread`, polling de 120s, sem tocar em conteúdo
- [ ] Contador sempre neutro, nunca colorindo a barra (`SEVERITY.md` §2.4)
- [ ] Critério de aceite 7 do `SPEC.md` verificado

*Fase encolhida por D-006: a detecção de microfone que morava aqui saiu do escopo, e
`MeetingRanIntoNext` passou a ser baseado apenas em cronograma, entregue na Fase 3.*

---

## Depois (não comprometido)

- Reposicionar/redimensionar a barra por arrastar, com posição persistida
- Ações rápidas no toast ("entrar na call", "concluir tarefa")
- Registro de foco: quanto tempo em reunião vs. livre por dia
- Feriados municipais/estaduais e emendas da TOTVS por lista manual (`SEVERITY.md` §7)
- Reconsiderar Chat se o Google publicar contagem de não-lidos (D-004)
- Reconsiderar detecção de microfone como refinamento dos sinais existentes, se o clique se
  mostrar insuficiente na prática (D-006 é revogável sem redesenho)
