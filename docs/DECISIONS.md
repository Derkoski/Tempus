# Decisões de Arquitetura

Formato: contexto → decisão → consequências. Uma decisão revogada não é apagada; é marcada
como *Substituída por D-NNN*, para que o histórico do raciocínio sobreviva.

---

## D-001 — WPF sobre .NET 8, app não-empacotado

**Status:** Aceita · 2026-08-07

**Contexto.** O app precisa de: janela sem borda always-on-top posicionada com precisão em
coordenadas de tela, interop Win32 pesado (taskbar, DPI, notificações, sessões de áudio),
DPI per-monitor v2, e renderização de conteúdo dinâmico. O desenvolvedor tem fluência em C#.

**Decisão.** WPF sobre .NET 8 (LTS, já instalado: SDK 8.0.303), `net8.0-windows`, não-empacotado.

Alternativas descartadas:

| Opção | Motivo da recusa |
|-------|------------------|
| WinUI 3 / Windows App SDK | Controle de janela menos flexível, always-on-top e no-activate são fricção, MSIX praticamente obrigatório. Errado para utilitário de shell. |
| WinForms | Ótimo para tray, ruim para os painéis (S2/S3). Fica como fallback se WPF criar problema de composição sobre a taskbar. |
| Avalonia | Adiciona risco de abstração sobre Win32 sem entregar valor — não há requisito cross-platform. |
| Electron / web | Consumo de memória e latência de startup inaceitáveis para algo sempre aberto. |

**Consequências.** O scaffold atual (`Microsoft.NET.Sdk.Web` com minimal API "Hello World")
é descartado na Fase 2 — foi criado por engano e não representa nenhuma decisão.
Notificações toast em app não-empacotado exigem AUMID registrado via atalho no Menu Iniciar;
custo pontual, resolvido uma vez.

---

## D-002 — Barra flutuante sobre a taskbar, com abstração de superfície

**Status:** Aceita · 2026-08-07

**Contexto.** O pedido original era ocupar espaço *dentro* da taskbar. Isso era possível via
**Deskband** (API por trás de BatteryBar, NetSpeedMonitor, EverythingToolbar). A taskbar XAML
do Windows 11 **não suporta deskband**, e em builds recentes a API foi removida, não apenas
desativada. Máquina alvo: build 26200. Portanto: impossível pelo caminho suportado.

Opções reais avaliadas: múltiplos ícones de tray (robusto, pequeno), janela flutuante
posicionada sobre a taskbar (visual desejado, frágil), AppBar via `SHAppBarMessage` (robusto,
mas rouba espaço de tela), overlay no próprio botão da taskbar (1 número só), widget do Win11
(não é sempre-visível).

**Decisão.** Barra flutuante always-on-top posicionada sobre a taskbar, como escolhido pelo
usuário com o risco de fragilidade explicitamente à vista.

**Mitigação obrigatória:** toda a renderização fica atrás de uma interface `IShellSurface`,
com `FloatingBarSurface` como implementação primária e `TraySurface` como fallback previsto.
Nenhuma lógica de domínio conhece a superfície. Se um update do Windows quebrar o
posicionamento, a troca é de implementação, não de arquitetura.

**Restrições técnicas de implementação:**

- **Nunca** usar `SetParent` para reparentar dentro de `Shell_TrayWnd`. É o hack que quebra
  de forma severa em restart do explorer, mudança de DPI e updates do Windows.
- Posicionar por coordenada: localizar `Shell_TrayWnd` via `FindWindow` + `GetWindowRect`, e
  colocar a janela numa região vazia conhecida.
- Estilos de janela: `WS_EX_TOOLWINDOW` (fora do Alt+Tab) + `WS_EX_NOACTIVATE` (não rouba
  foco) + `Topmost` + `ShowInTaskbar=false`.
- Reagir a: mensagem broadcast `TaskbarCreated` (explorer reiniciou → reposicionar),
  `WM_DPICHANGED`, `WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE`, e bloqueio/desbloqueio de sessão.
- Ler estado de auto-hide via `ABM_GETSTATE` e acompanhar quando a taskbar se esconde.
- Esconder em tela cheia via `SHQueryUserNotificationState`.
- Sem timer de reposicionamento por polling em loop apertado — reagir a eventos, e no máximo
  uma verificação de sanidade a cada poucos segundos.

**Consequências.** A Fase 2 gasta esforço real com robustez de posicionamento em vez de
features. Aceito conscientemente em troca do visual pretendido. O critério de aceite 1 do
`SPEC.md` existe para provar essa robustez.

### Achado da Fase 2: perder a ordem Z é o pior modo de falha

Medido em 2026-08-07, com a barra rodando: `WindowFromPoint` sobre os pixels da barra retornava
`Shell_TrayWnd` do explorer, não a barra. Ou seja, **a barra estava atrás da taskbar**.

A taskbar também é `HWND_TOPMOST`, e o explorer a reposiciona por conta própria, o que empurra a
barra para trás dela. Não existe evento para isso. Pior: quando acontece, a barra não só fica
invisível — ela fica **surda a cliques**, porque os pixels passam a pertencer ao explorer. Como o
clique é o gesto central do produto (D-006), isso quebra tudo de uma vez.

Causa no código: `Reposition()` só chamava `SetWindowPos` quando o retângulo mudava, então a
ordem Z nunca era reafirmada depois da primeira colocação.

Correção: reafirmar `HWND_TOPMOST` a cada 1s (uma chamada de `SetWindowPos` com `SWP_NOMOVE |
SWP_NOSIZE`), independente de o retângulo ter mudado. Frequência maior que a da verificação de
posição (2s) justamente porque a consequência é mais grave.

**Lição para a superfície:** verificar posição não basta; é preciso verificar *propriedade dos
pixels*. `WindowFromPoint` sobre o retângulo da própria barra é a única checagem que não mente —
e é a que deve virar teste automatizado, não a comparação de retângulos.

---

## D-003 — OAuth em modo Testing, permanentemente

**Status:** Aceita · 2026-08-07 · **permanente, não há caminho alternativo**

**Contexto.** Acesso às APIs exige um OAuth client. Caminhos possíveis:

| Caminho | Verificação | Vida do refresh token | Restricted scopes | Viável? |
|---------|-------------|----------------------|-------------------|---------|
| External + Testing | nenhuma | **7 dias** | permitidos para test users | ✅ |
| External + Production | exigida (CASA, paga e lenta para restricted) | indefinida | só após verificação | ❌ custo desproporcional para uso pessoal |
| Internal na org GCP da TOTVS | nenhuma | indefinida | permitidos | ❌ **descartado** |

O caminho *Internal* seria tecnicamente ideal, mas foi descartado pelo usuário: a TOTVS tem
~90 mil funcionários e este é um utilitário de uso estritamente pessoal. Pedir provisionamento
de OAuth client na org corporativa para uma ferramenta de uma pessoa tem chance ~0 de aprovação,
e o pedido em si não se justifica. **Não é uma pendência — é um caminho fechado.**

**Decisão.** **External + Testing**, permanentemente, com a própria conta como test user e
re-consent a cada 7 dias como parte do funcionamento normal do app.

**Consequências.**

- O estado `Offline` do `SEVERITY.md` §0 deixa de ser tratamento de erro e passa a ser **um
  estado normal semanal do produto**. Entra na Fase 1, não depois.
- O fluxo de re-consent (`SEVERITY.md` §6) precisa custar dois cliques: barra cinza → clique →
  navegador (já logado, `login_hint` pré-seleciona a conta) → *Permitir* → volta sem reiniciar.
- **Não é possível reaproveitar a sessão do Chrome.** Tokens OAuth são vinculados ao
  `client_id`, e os cookies de sessão do Google no Chrome não são acessíveis a apps de fora — por
  design, já que o contrário seria um vetor de roubo de credencial. O que *é* aproveitado: o
  navegador já está autenticado, então o consent não pede senha nem 2FA.
- Restricted scopes (`gmail.readonly`) **continuam funcionando** em Testing mode para test users.
  Nada de escopo se perde por essa decisão.
- `client_secret.json` e o token **nunca** entram no repo (já no `.gitignore`). Token em disco
  protegido com DPAPI vinculado ao usuário.

**Risco residual — resolvido em 2026-08-11: a TOTVS NÃO bloqueia.**

O medo era o admin do Workspace recusar apps de terceiros não configurados
(*API Controls → App access control*), o que não teria contorno pelo caminho OAuth. Testado com a
conta corporativa real: o consent foi concedido, o refresh token emitido, e Calendar e Tasks
responderam com dados. **O caminho está aberto.**

Descoberta paralela, que muda o procedimento de setup: a conta TOTVS **não consegue criar projeto
no Google Cloud** — falta `resourcemanager.projects.create`, revogado do domínio, e toda conta
Workspace fica presa à organização dela, então nem "Sem organização" funciona. A saída é criar o
projeto GCP numa **conta pessoal `@gmail.com`** e adicionar o e-mail da TOTVS como *usuário de
teste*. Um OAuth client é só um identificador; ele não precisa pertencer à organização cujos dados
serão lidos. Quem autoriza é o usuário, na tela de consent.

**Plano B, agora arquivado:** se o OAuth tivesse sido bloqueado, restaria o endereço secreto em
formato iCal do Google Calendar (URL privada por calendário, HTTP puro, sem OAuth), que salvaria a
metade de agenda e perderia Tasks e Gmail. Não foi necessário.

---

## D-006 — Sem detecção de presença em call; reconhecimento por clique

**Status:** Aceita · 2026-08-07

**Contexto.** O desenho inicial usava o estado do microfone (`IAudioSessionManager2`) para
distinguir "você está na call" de "você já saiu", o que alimentava `MeetingStartedNotJoined`,
`MeetingOverran` e `MeetingOverranIntoNext`. Questionado, o usuário respondeu que não vê
necessidade: basta avisar que existe uma call, sem confirmar presença.

**Decisão.** O Tempus **não** tenta inferir se você está numa reunião. Nenhuma detecção de
microfone, câmera ou janela. Ele conhece o cronograma e avisa sobre ele; **você** comunica que
viu, clicando. O clique substitui integralmente a heurística de presença.

**Consequências.**

- Um subsistema Win32 inteiro sai do escopo (sessões de áudio, enumeração de dispositivos,
  polling de estado de captura) — provavelmente o mais frágil e menos testável do projeto.
  A Fase 5 encolhe e a Fase 3 fica mais simples.
- Some a categoria de falso positivo mais chata: outro app segurando o microfone e o Tempus
  concluindo que você está em reunião.
- **Custo aceito:** a barra não distingue "você está estourando a reunião" de "você saiu no
  horário". Mitigação embutida no modelo: `MeetingEnded` auto-limpa em 3 min, então reunião
  isolada não exige clique nenhum. Só o caso que realmente importa —
  `MeetingRanIntoNext`, a próxima já começou — persiste e exige o clique.
- Sinais renomeados para refletir que são baseados em cronograma:
  `MeetingStartedNotJoined` → `MeetingStarted`, `MeetingOverran*` → `MeetingEnded` /
  `MeetingRanIntoNext`.
- Revogável sem redesenho: se algum dia a detecção de mic for desejada, ela entra como um
  refinamento das condições dos mesmos sinais, sem mexer na arbitragem.

---

## D-007 — "Tarefas abertas" = todas as não concluídas

**Status:** Aceita · 2026-08-07 · com ressalva registrada

**Contexto.** O sinal `DayEnded` das 17:00 precisa contar "tarefas abertas do dia", e havia duas
leituras: apenas as que vencem hoje ou estão vencidas, ou todas as não concluídas. O usuário
escolheu **todas as abertas**.

**Decisão.** Todas as tarefas não concluídas, independente de data de vencimento.

**Ressalva registrada.** Se existir um backlog que nunca chega a zero, `DayEnded` fica **vermelho
todo dia**, e com a escalada de piscante do §1.1 isso significa piscar toda noite até o clique.
Pela regra do orçamento de vermelho, isso é o sinal clássico de vermelho virando papel de parede.

A leitura em que a decisão faz total sentido: `DayEnded` não é um alarme de exceção, é um
**ritual diário de encerramento** — "o dia acabou, este é seu backlog, reconheça". Um clique por
dia é um preço justo por isso. Aceito nesses termos.

**Consequências.** Implementar com a chave `DayEnded.CountMode` em `appsettings.json`, valores
`AllOpen` (padrão) e `DueTodayOrOverdue`. Assim, se depois de uma semana convivendo o vermelho
noturno incomodar, é uma linha de configuração e não uma mudança de código.

---

## D-008 — Feriados computados localmente: nacionais + PR + Pato Branco

**Status:** Aceita · 2026-08-07

**Contexto.** Os sinais de fronteira do dia (12:00 e 17:00) só devem disparar em dia útil. O
usuário definiu: seg–sex com feriados nacionais, e autorizou incluir estaduais e municipais de
Pato Branco/PR *se* houvesse fonte confiável — com a ressalva de que nesses dias ele não abre o
computador, então o custo de errar é zero.

**Decisão.** Tabela computada em código, sem rede e sem dependência externa. Datas fixas mais os
derivados da Páscoa, calculada pelo algoritmo de Meeus/Gauss (~30 linhas).

| Escopo | Datas |
|--------|-------|
| Nacionais fixos | 01/01, 21/04, 01/05, 07/09, 12/10, 02/11, 15/11, **20/11** (nacional desde 2024, Lei 14.759/2023), 25/12 |
| Nacionais móveis | Carnaval (Páscoa −48 e −47), Sexta-feira Santa (−2), Corpus Christi (+60) |
| Paraná | 19/12 — Emancipação Política do Paraná |
| Pato Branco | 29/06 — São Pedro Apóstolo, padroeiro (Lei Municipal 40/1970) · 14/12 — Emancipação Política do município |

Os municipais foram confirmados na lei municipal de 1970 e no calendário publicado pela própria
prefeitura, não inferidos.

**Consequências.** Funciona offline, para qualquer ano, sem manutenção anual. Carnaval e Corpus
Christi são ponto facultativo e não feriado legal, mas na prática se comportam como feriado —
entram. Emendas e pontos facultativos específicos da TOTVS ficam numa lista manual
`WorkDay.ExtraHolidays` em `appsettings.json`, porque não há fonte pública. Como o modo de falha
é "a barra fica âmbar às 12:00 num dia em que o usuário não está no computador", o custo de uma
data faltante é nulo — não justifica nem rede nem dependência.

---

## D-009 — Reuniões sobrepostas: o usuário declara o evento ativo

**Status:** Aceita · 2026-08-07

**Contexto.** Aceitar duas reuniões no mesmo horário é comum. Sem saber em qual você está, o fim
da primeira dispararia um `MeetingRanIntoNext` falso — o vermelho mais forte do produto, no caso
mais banal. Perguntado, o usuário sugeriu que o app pergunte em qual reunião está e use o horário
dela para determinar o fim.

**Decisão.** Um sinal `MeetingAmbiguous` de nível 1 pergunta qual é o **evento ativo**; o escolhido
passa a ser o único a alimentar os sinais de call. Mecânica completa em `SEVERITY.md` §8.

**Consequências.**

- Coerente com D-006: presença continua sendo **declarada** pelo usuário, nunca inferida pelo app.
  É a mesma filosofia do clique de reconhecimento, aplicada à desambiguação.
- A cláusula que mata o falso positivo é a definição de "próximo evento": o primeiro que começa
  após o fim do evento ativo **e não se sobrepõe a ele**. Reunião aceita em paralelo nunca conta
  como "próxima invadida".
- Exige um padrão determinístico antes da escolha (`accepted` > `tentative` > `needsAction`, então
  início mais cedo, então fim mais cedo), para a barra nunca ficar sem estado.
- Nível 1 e não 2: é uma pergunta, não um alarme. Não deve competir com sinais reais.

---

## D-010 — Identidade de ocorrência inclui o horário de fim

**Status:** Aceita · 2026-08-07

**Contexto.** Reconhecer um alerta suprime "aquela ocorrência", mas faltava definir o que
identifica uma ocorrência. Caso concreto levantado: reunião prorrogada no calendário depois do
reconhecimento — o sinal deve voltar? Resposta do usuário: sim.

**Decisão.** `occurrenceId = (eventId, início, fim)` para sinais de calendário, e
`(nomeDoSinal, dataLocal)` para os demais.

**Consequências.** Prorrogar uma reunião muda o `fim`, muda a identidade, e o sinal volta a
disparar — que é o correto, porque o reconhecimento anterior se referia a um compromisso que
terminava em outro horário. Mover ou reagendar tem o mesmo efeito, pela mesma razão.
Supressões de `DayEnded` e `MiddayCheckpoint` valem até a virada do dia e sobrevivem a reinício
do app; as de calendário não precisam sobreviver.

---

## D-004 — Keep e Chat fora de escopo

**Status:** Aceita · 2026-08-07

**Contexto.** O pedido inicial incluía Keep e Chat junto de Calendar, Tasks e Gmail.

**Google Keep.** A API existe e cobre edições Workspace, mas **exige admin com domain-wide
delegation via service account** — um usuário não consegue habilitar para si. Além disso foi
desenhada para casos de uso administrativos (DLP, varredura de conteúdo), não como API de
cliente pessoal. Custo de viabilizar é alto e o resultado é incerto.

**Google Chat.** Existe `spaces.getReadState` expondo `lastReadTime`, mas **não existe
endpoint de contagem de não-lidos**. Obter o número exigiria listar spaces, listar mensagens
de cada space e contar as posteriores ao `lastReadTime` — padrão N+1, custo de quota alto e
latência ruim para algo que atualiza a cada minuto.

**Decisão.** Ambos fora do escopo do v1. Keep provavelmente para sempre; Chat reconsiderável
se o Google publicar contagem de não-lidos.

**Consequências.** O v1 cobre Calendar + Tasks + contagem do Gmail. Registrado em
`SPEC.md` → Fora de escopo, para que nenhuma sessão futura tente "completar" a integração.

---

## D-005 — Fronteiras do dia: 12:00 é checkpoint, 17:00 é encerramento

**Status:** Aceita · 2026-08-07

**Contexto.** O pedido era "avisar que o dia terminou ao meio-dia" e "que terminou às 17:00" —
ambíguo entre dois encerramentos equivalentes e duas coisas semanticamente diferentes.

**Decisão.** Semânticas distintas: **12:00 = checkpoint de meio de jornada** (metade do dia
passou, o que estava planejado andou?) e **17:00 = fim de jornada**. Consequência direta nas
severidades: meio-dia chega no máximo a âmbar; 17:00 chega a vermelho, mas somente com tarefas
abertas. Detalhes em `SEVERITY.md` §2.2.

**Consequências.** Os horários são configuráveis (`appsettings.json` → `WorkDay`), com os
sinais desligados fora de dias úteis. A definição de "tarefas abertas do dia" permanece como
questão aberta em `SEVERITY.md` §5 e precisa ser fechada antes da Fase 3.

---

## D-011 — Slot esquerdo mostra o que vem a seguir, não a hora

**Status:** Aceita · 2026-08-10

**Contexto.** O layout inicial da barra tinha um relógio no slot esquerdo. Observação do usuário
ao ver a barra rodando: é redundante, porque o Windows já mostra a hora na própria taskbar, a
poucos centímetros. E aquele é o espaço mais visível da barra.

**Decisão.** O slot esquerdo passa a mostrar **o próximo compromisso**: hora absoluta em destaque
mais o título em texto secundário, truncado. Nunca colore.

Consequência de desenho que vale mais que a troca em si: os dois slots de texto ganham papéis
distintos e param de se repetir.

| Slot | Pergunta que responde | Presença | Cor |
|------|----------------------|----------|-----|
| Esquerdo (`NextUp`) | "o que vem depois?" | **sempre** | nunca |
| Meio (`Reason`) | "algo precisa de mim agora?" | **raro** | sim, é o único que colore |

Antes, o estado `Calm` usava a área de motivo para mostrar o próximo compromisso — o que fazia a
mesma informação aparecer em dois lugares e mantinha a área de alarme sempre ocupada. Agora
`Calm` tem motivo **vazio**, e uma barra calma tem o meio em branco. Isso é o objetivo, não uma
falta: o meio ocupado passa a significar, por si só, que algo precisa de atenção.

**Consequências.**

- `NextUpResolver.Resolve` é função pura de (agenda, agora) → próximo compromisso, testável sem
  UI nem rede (regra 8). Uma reunião **em curso** não conta como próxima: durante ela o que
  interessa é o que vem depois.
- Estados vazios são explícitos: `livre` quando não há mais nada hoje, `—` quando `Offline`.
  Nunca o último valor conhecido (`SEVERITY.md` §0).
- Clicar no slot abre a agenda e **nunca** reconhece alerta. Reconhecer continua exclusivo da
  área de motivo, para que o gesto central do produto (D-006) tenha um lugar único e previsível.
- O timer de 1s perdeu sua razão original (atualizar o relógio), mas continua necessário para
  reafirmar a ordem Z — ver "Achado da Fase 2" em D-002.
- A barra ficou sem relógio próprio. Aceito: o Windows já tem um.

### Tarefas com hora exata: fora, por limitação do Google

O pedido original incluía "próxima ação minha, como uma tarefa com horário marcado exato" no mesmo
slot. **Não é possível pelo Google Tasks:** a API aceita um timestamp RFC 3339 mas grava apenas
ano/mês/dia — a hora é descartada, e não há como ler nem escrever hora de vencimento. Não é
limitação de biblioteca nem de cliente; é da API.

Alternativas apresentadas ao usuário e a escolha:

| Saída | Custo | Escolhida |
|-------|-------|-----------|
| Hora no início do título (`14:30 Ligar…`), com o Tempus lendo o `HH:mm` | Título carrega o horário | — |
| Hora só local no Tempus, ligada ao id da tarefa | Hora não existe fora da máquina; tarefa criada no celular nasce sem hora | — |
| Tarefa com hora vira evento no Calendar | Amplia escopo: a agenda passaria de somente-leitura para escrita | — |
| **Só reuniões alimentam o slot** | O pedido fica pela metade | ✅ |

**Decisão.** O slot é alimentado **somente pela agenda**. Tarefas continuam aparecendo apenas no
contador e no painel S2.

**Consequências.** `NextUp` não tem discriminador de origem — existe uma só, e um enum de um
membro seria código morto sugerindo uma decisão que foi recusada. Registrado na lista de
fora-de-escopo do `SPEC.md` para que nenhuma sessão futura tente "completar" isso.

---

## D-012 — Humor temporal: um segundo vocabulário de cor, ambiente

**Status:** Aceita · 2026-08-11

**Contexto.** Com dados reais na barra, o estado mais frequente virou "livre, sem reunião agora" —
e ele não dizia nada além de `livre`. Pedido do usuário: mostrar o próximo evento com contagem
regressiva em dias e horas, em cor calma, "porque vai ser um dos status que mais vai aparecer";
verde para bater o olho e saber que está livre; âmbar quando a reunião se aproxima; vermelho a 5
minutos. E, durante uma reunião, o que importa é quanto falta para ela acabar e o que vem depois —
com o título, não só "há uma próxima".

**Decisão.** O slot esquerdo ganha **vocabulário de cor próprio**, independente da escala de
severidade: `TimeMood` com Free / InMeeting / Approaching / EndingSoon / Imminent. Tabela completa
em `SEVERITY.md` §1.5.

**Duas tensões com o modelo existente, e como foram resolvidas:**

**1. Vermelho a 5 minutos estoura o orçamento de vermelho.** A §1 dizia 0–2× por dia, com ~3 como
sinal de que o modelo está errado. Um dia com 6 reuniões produz 6 vermelhos de contagem regressiva.

Resolvido **por forma, não por cor**: os dois vermelhos passam a ser distinguíveis pelo
comportamento. O de contagem regressiva dura no máximo 5 minutos, **se resolve sozinho** quando a
reunião começa e nunca pisca. O de alarme (`MeetingRanIntoNext`, `DayEnded`) **escala para
piscante em 5 min** e só sai com clique. Mesma cor, urgências distinguíveis sem esforço.

**2. A invariante I1 dizia "uma cor por vez".** Agora há duas áreas coloridas simultâneas.

I1 foi revista, não abandonada: no máximo **duas** áreas, com vocabulários distintos — ambiente e
alarme. O que continua proibido é **dois alarmes** competindo. A separação de forma sustenta isso:
texto tingido é ambiente, preenchimento é alarme, e o slot de tempo só preenche nos dois humores
que pedem antecipação.

**Consequências.**

- `NextUp` e `NextUpResolver` foram substituídos por `TimeStatus` e `TimeStatusResolver`, que
  respondem uma pergunta mais ampla — não só "o que vem depois" mas "como está meu tempo". Função
  pura de (agenda, agora, limiares), testável sem UI nem rede (regra 8).
- Verde entra na paleta **dessaturado**. É o estado mais frequente do dia; saturado, a barra
  gritaria o tempo todo.
- O layout inverteu as prioridades de largura: o slot de tempo passou a flexível e o chip a
  `Auto`. Assim o alarme sempre cabe inteiro e o ambiente é que trunca — a ordem certa.
- O redesenho de estado passou de 20s para 5s. Não custa rede, só recontagem de tempo, mas a 20s
  uma contagem regressiva ficaria visivelmente atrasada perto da virada de minuto.
- Limiares em `appsettings.json` → `Time`, para ajustar sem recompilar.

---

## D-013 — Estourar reunião é dano a terceiros, não atraso próprio

**Status:** Aceita · 2026-08-11 · **corrige D-012**

**Contexto.** D-012 fez `EndingSoon` colorir apenas quando havia outra reunião em seguida. O
raciocínio era: "se tem a tarde livre, acabar tarde não custa nada". O usuário corrigiu com o
incidente que originou o projeto — segurou a equipe inteira numa week review até **12:22**, quando
o horário de todos terminava 12:00.

O erro do meu raciocínio foi o referencial. Eu media o custo pela **minha** agenda; o custo real é
o **tempo das outras pessoas**, que foi comprometido pela duração marcada. Uma reunião de uma hora
tem uma hora do dia de cada participante reservada, e estourar consome tempo alheio
independentemente do que exista depois na minha agenda. Ter a tarde livre não devolve os 22
minutos a quem estava na sala.

**Decisão.**

1. `EndingSoon` dispara **sempre** que a reunião atual está a ≤5 min do fim marcado, com ou sem
   evento seguinte.
2. Novo humor `Overrun`: vermelho preenchido quando o horário marcado já passou, com prioridade
   sobre "próxima reunião". Acusa por 10 min (`Time.OverrunMinutes`) e some.
3. Novo indicador de **fronteira do dia** (`SEVERITY.md` §1.6): contagem regressiva para 12:00 e
   17:00, em gradiente verde→âmbar→vermelho, no canto esquerdo da barra.

**Por que a fronteira do dia é um terceiro elemento, e não mais um estado:**

O humor temporal cobre o caso "estou numa reunião que vai acabar". A fronteira cobre o outro, que
o incidente também expõe: estar trabalhando **sem agenda nenhuma** e perder a hora. São perguntas
diferentes, e a fronteira é um gradiente contínuo em vez de uma escala de estados — não cabe na
gramática do §0.5. Por isso ocupa espaço próprio, pequeno, e desaparece por completo fora da
janela de aviso.

**Consequências.**

- `Overrun` some sozinho depois de 10 min. Sem detecção de presença (D-006), o app não sabe se
  você encerrou; insistir para sempre viraria ruído e o vermelho perderia valor. O aviso vale no
  intervalo em que o dano acontece.
- Falso positivo aceito: encerrar 2 min antes do horário marcado ainda produz `Overrun` na virada.
  A assimetria justifica — o custo de um alerta desnecessário é uma olhada; o de um alerta que não
  veio foi a week review das 12:22.
- Feriados de D-008 saíram do papel: nacionais, do Paraná e de Pato Branco, com Páscoa por
  Meeus/Jones/Butcher. Sem eles, a barra faria contagem regressiva de expediente em feriado.
- `WorkDay` agora é lido do `appsettings.json`; os horários deixaram de ser suposição no código.
