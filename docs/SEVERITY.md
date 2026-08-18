# Modelo de Severidade e Cor

> Este é o documento mais importante do Tempus. O requisito central do produto é:
> **"quero que ele fique com essa cor apenas quando realmente precisa da minha atenção."**
>
> Cor não é decoração aqui — é um canal de comunicação escasso. Se vermelho aparece
> quando não precisa, ele para de funcionar quando precisa. Toda decisão abaixo existe
> para proteger o valor do vermelho.

## 0. Estado Offline — fora da escala

Antes da escala de severidade existe um estado que a **precede e anula**: quando o Tempus não
sabe o que está acontecendo, ele não pode alarmar sobre nada.

| Estado | Condição | Visual |
|--------|----------|--------|
| `Offline` | `AuthExpired`, ou última sync bem-sucedida > 10 min | Barra **dessaturada em cinza**, contadores substituídos por `—`, texto `Login expirado — clique para entrar` ou `Sem sincronizar há N min` |

Regras:

- `Offline` **substitui** toda a escala 0–3. Nenhum sinal é avaliado, porque os dados não são
  confiáveis. Nada pisca, nada fica vermelho.
- O visual é deliberadamente **um vocabulário diferente** de alerta: cinza apagado significa
  "o Tempus está fora do ar", não "você precisa agir". Confundir os dois seria o pior erro de
  design possível aqui — você olharia uma barra cinza e pensaria "tudo tranquilo", quando na
  verdade ela não sabe de nada.
- Contadores viram `—`, nunca o último número conhecido. Mostrar `☑ 4` velho é mentir com
  confiança.
- A barra inteira é clicável neste estado e dispara o re-consent (§6).

Isso importa muito porque operamos em OAuth *Testing mode* permanentemente, onde o Google
revoga o refresh token a cada 7 dias por design (ver `DECISIONS.md` D-003). O estado `Offline`
vai acontecer toda semana — é parte do funcionamento normal, não uma exceção.

| Sinal degradado | Condição | Nv |
|-----------------|----------|----|
| `SyncDegraded` | falhas intermitentes, mas última sync < 10 min | 1 |

## 0.5 Dois vocabulários de cor

A barra tem **duas** áreas coloridas, com papéis e gramáticas diferentes. Confundi-las é o erro
mais fácil de cometer aqui.

| | Slot de tempo (esquerda) | Chip de motivo (meio) |
|---|---|---|
| Responde | "como está meu tempo agora?" | "algo precisa de mim?" |
| Presença | **sempre** | **raro** — vazio na maior parte do dia |
| Forma | texto tingido; preenchido só quando pede antecipação | sempre preenchido |
| Escala | `TimeMood` (§1.5) | severidade 0–3 (§1) |

A distinção de **forma** é o que impede os dois vermelhos de se confundirem: o vermelho de
contagem regressiva preenche o slot de tempo e **se resolve sozinho** quando a reunião começa; o
vermelho de alarme preenche o chip e só sai com clique.

> **Emenda de 2026-08-18.** A frase acima foi escrita quando o slot de tempo só abrigava
> `Imminent`, que de fato se resolve sozinho. **`Overrun` quebrou essa premissa:** ele não se
> resolve — persiste 10 min, exige reconhecimento (§10) e agora **também pisca** após 5 min.
>
> Piscar deixou de ser exclusividade do chip. O critério passa a ser o **fato**, não o slot: o que
> escala é alarme que não se resolve sozinho, esteja ele onde estiver. Entre os humores
> preenchidos só `Overrun` alcança os 5 minutos — `Imminent` acaba quando a reunião começa e
> `EndingSoon` quando ela termina — então a regra seleciona o caso certo sem caso especial.

## 1.5 Humor temporal — o sinal ambiente

Este é o estado que fica visível o dia inteiro, então a cor tem que informar sem cansar.

**Todo humor lidera com um rótulo de estado** (D-015). O rótulo é a resposta que se lê de relance,
e ela fica sempre na mesma posição — o olho não precisa interpretar a frase para saber se está
livre ou ocupado. O título da reunião vem depois, como detalhe.

| Humor | Quando | Rótulo | Cor | Forma | Exemplo |
|-------|--------|--------|-----|-------|---------|
| `Free` | livre, próxima reunião a mais de 15 min | `Livre` | verde dessaturado | texto | `Livre · Daily em 2h15` |
| `Free` | livre, nada mais hoje | `Livre` | verde dessaturado | texto | `Livre` |
| `Approaching` | próxima em ≤15 min | `Em breve` | âmbar | texto | `Em breve · Daily em 12 min` |
| `Imminent` | próxima em ≤5 min | `Começando` | vermelho | **preenchido** | `Começando · Daily em 4 min` |
| `InMeeting` | em reunião, dentro do horário | `Ocupado` | azul | texto | `Ocupado · Refino, faltam 25 min → Review` |
| `EndingSoon` | reunião atual acaba em ≤5 min | `Encerrando` | âmbar | **preenchido** | `Encerrando · Refino, faltam 4 min` |
| `Overrun` | passou do fim marcado, até 10 min depois | `Estourou` | vermelho | **preenchido** | `Estourou · Weekly, passou 22 min` |
| `OffHours` | fora do expediente, sem nada agendado | `Dia Encerrado` · `Almoço` · `Folga` | laranja | texto **negrito** | `Dia Encerrado` |
| `Unknown` | `Offline` | `—` | cinza | texto | `—` |

Notas de design:

- **Verde é dessaturado de propósito.** É o estado mais frequente do dia; saturado, a barra
  gritaria o tempo todo e a cor perderia função.
- **`OffHours` já era só rótulo** e continua sendo — ele não tem título de reunião para exibir,
  porque só fala quando não há nada agendado.
- **A cor de provedor do painel S3 não é deste vocabulário.** O ponto verde/azul que identifica
  Meet e Zoom (D-017) é marca de identidade, não estado: não escala, não pisca, não se resolve.
  Ele vive só no S3, onde a I1 não se aplica, e **não** deve ser encaixado nesta tabela nem na
  escala de severidade do §1.
- **`EndingSoon` dispara mesmo sem nada em seguida** (corrigido em D-013). O custo de estourar
  não é seu — é do tempo das outras pessoas, comprometido pela duração marcada. Ter a tarde livre
  não devolve os 22 minutos a quem estava na reunião.
- **`Overrun` tem prioridade sobre "próxima reunião"**, porque estourar é o problema mais caro que
  a barra conhece. Ele acusa por 10 min após o fim marcado e depois some sozinho: sem detecção de
  presença (D-006), o app não sabe se você saiu, e insistir para sempre viraria ruído.
- Reuniões de dia inteiro não entram; recusadas, "Livre" e `outOfOffice` também não (§2.1).
- Limiares configuráveis em `appsettings.json` → `Time`.

## 1.6 Contagem regressiva do expediente

Indicador pequeno e independente, no canto esquerdo da barra. Mostra só os **minutos restantes**
até a próxima fronteira do dia (12:00 ou 17:00), num **gradiente contínuo** de verde a vermelho.

| Distância da fronteira | Cor |
|---|---|
| 60 min | verde |
| 30 min | âmbar |
| 0 min | vermelho |

- Aparece só dentro da janela (padrão 60 min) e **some por completo** fora dela. Ausência, não um
  estado "calmo".
- Só em dia útil: seg–sex menos feriados nacionais, do Paraná e de Pato Branco, calculados
  localmente (D-008), mais as emendas em `WorkDay.ExtraHolidays`.
- Fica **fora dos dois vocabulários** do §0.5: é um gradiente, não uma escala de estados. Por isso
  ocupa espaço próprio em vez de disputar o slot de tempo ou o chip.
- Cores fixas nos dois temas do Windows: é contagem regressiva, e o que ela comunica não deveria
  mudar de intensidade porque o sistema está em modo claro.

**Custo assumido no orçamento de vermelho:** `Imminent` acende uma vez por reunião, então um dia
com 6 reuniões tem 6 vermelhos — acima do teto de ~3 da §1. Aceito conscientemente em D-012, com
a mitigação de que este vermelho é curto (≤5 min), se resolve sozinho e nunca pisca.

## 1. Níveis

Existem exatamente quatro níveis. Nunca adicionar um quinto sem revisar este doc.

| Nv | Nome | Cor | Significado | Frequência esperada |
|----|------|-----|-------------|---------------------|
| 0 | `Calm` | Neutro (fundo da taskbar) | Nada requer ação. Números continuam visíveis. | Estado padrão, maior parte do dia |
| 1 | `Info` | Accent discreto (azul/ciano) | Algo se aproxima, mas ainda há folga. Não interrompe. | Algumas vezes por dia |
| 2 | `Attention` | Âmbar | Precisa agir em breve. Custo ainda é baixo. | Poucas vezes por dia |
| 3 | `Critical` | Vermelho → **pisca âmbar↔vermelho após 5 min** | **Está custando algo agora.** Alguém espera, ou você passou de um limite. | Idealmente 0–2× por dia |

### 1.1 Escalada e reconhecimento do nível 3

O nível 3 não decai com o tempo — ele **escala** até você reconhecer:

| Tempo desde que virou nível 3 | Visual |
|---|---|
| 0–5 min | Vermelho sólido |
| 5 min em diante | Pisca alternando âmbar ↔ vermelho, período ~1,2s |
| após reconhecimento | Volta imediatamente ao normal |

**Reconhecer** = clicar na barra. O gesto significa "eu vi". Efeito: aquela **ocorrência** do
sinal é suprimida e a barra volta ao que os outros sinais ditarem — na prática, `Calm`.

Regras de reconhecimento:

- A supressão é por **ocorrência**, não por tipo de sinal. Reconhecer que a Daily estourou não
  suprime o estouro da próxima reunião, nem o fim de jornada.
- Se a condição de base mudar materialmente, o sinal **volta a disparar**. Exemplos: uma nova
  tarefa vence, um novo dia começa, uma nova reunião estoura.
- Supressões não sobrevivem ao reinício do app, **exceto** `DayEnded` — reconhecer o fim do dia
  vale até a virada do dia seguinte, senão reabrir o app às 19:00 traria o vermelho de volta.
- Se as animações do sistema estiverem desligadas (`SPI_GETCLIENTAREAANIMATION`), usar vermelho
  sólido em vez de piscar, e nunca piscar mais rápido que 1s de período.

**Regra do orçamento de vermelho:** se o vermelho aparecer mais de ~3× num dia normal, o
modelo está errado — corrigir o modelo, não a sua tolerância.

## 2. Sinais

Cada sinal é uma função pura de (dados sincronizados, hora atual, supressões ativas) →
severidade + motivo. Sinais são independentes; a arbitragem (§4) escolhe o vencedor.

### 2.1 Ciclo de vida de call (fonte: Google Calendar)

**Decisão fundadora: o Tempus não tenta saber se você está numa call.** Nenhuma detecção de
microfone, câmera ou janela. Ele conhece o *cronograma* e avisa sobre ele; quem sabe se você
está na reunião é você, e você comunica isso clicando (ver D-006).

| Sinal | Condição | Nv | Motivo (exemplo) |
|-------|----------|----|------------------|
| `MeetingUpcoming` | T−10min a T−2min | 1 | `Daily em 6 min` |
| `MeetingImminent` | T−2min a T−0 | 2 | `Daily começa em 1 min` |
| `MeetingStarted` | T+0 até o fim do evento | 2 | `Daily começou às 14:00` |
| `MeetingBackToBack` | `MeetingStarted` e o próximo evento começa ≤5 min após este terminar | 1 | `Sem intervalo: Review às 15:00` |
| `MeetingEnded` | do fim do evento até fim+3min, **sem próximo evento em curso** | 2 | `Daily acabou às 15:00` |
| `MeetingRanIntoNext` | passou do fim **e o próximo evento já começou** | **3** | `Daily acabou — Review já começou` |

Notas de design:

- **`MeetingStarted` é âmbar, não vermelho.** É informação de que a reunião está em curso, e
  serve tanto para "entra" quanto para "você está nela". Reconhecer rebaixa para `Calm`, então
  numa reunião longa você clica uma vez e a barra fica quieta.
- **`MeetingEnded` auto-limpa em 3 min.** Reunião isolada que termina não exige clique nenhum —
  âmbar breve e passa. Isso mantém o custo de interação baixo no caso comum.
- **`MeetingRanIntoNext` é o vermelho principal do produto** e **não** auto-limpa: fica vermelho,
  começa a piscar em 5 min, e só sai com clique. É o caso "estou invadindo outra call segurando
  a atual", que era um requisito explícito. Sem detecção de mic, o clique é o que diz "já saí".
- Reuniões **de dia inteiro** são ignoradas por completo neste ciclo — são marcadores, não calls.
- Eventos com `transparency=transparent` (Livre), recusados (`responseStatus=declined`), ou com
  `eventType` = `outOfOffice` / `focusTime` **não geram sinal algum**.

### 2.2 Fronteiras do dia (fonte: relógio local + Tasks)

Decisão do produto: 12:00 é **checkpoint de meio de jornada**; 17:00 é **fim de jornada**.
Semânticas diferentes, severidades diferentes.

**"Tarefas abertas" = todas as tarefas não concluídas**, independente de vencimento
(`DECISIONS.md` D-007).

| Sinal | Condição | Nv | Motivo (exemplo) |
|-------|----------|----|------------------|
| `MiddayCheckpoint` | 12:00 até 13:00 ou reconhecimento, com ≥1 tarefa aberta | 2 | `Metade do dia: 4 tarefas abertas` |
| `MiddayCheckpoint` | 12:00–12:15, com 0 tarefas abertas | 1 | `Metade do dia — em dia` |
| `DayEnded` | 17:00 até reconhecimento, com ≥1 tarefa aberta | **3** | `Jornada encerrada: 3 tarefas abertas` |
| `DayEnded` | 17:00 em diante, com 0 tarefas abertas | 1 | `Dia fechado ✓` |

Notas de design:

- **Meio-dia nunca é vermelho.** Metade do dia com trabalho pendente é o estado *esperado* às
  12:00. Âmbar cobra atenção sem gritar. Persiste até 13:00 ou clique, para não ser perdido se
  você estiver em reunião às 12:00 em ponto.
- **17:00 é vermelho apenas se sobrou coisa aberta**, e escala para piscante em 5 min até você
  reconhecer. Fechar o dia limpo não merece alarme nenhum.
- Dia útil = **seg–sex menos feriados nacionais brasileiros**, computados localmente sem rede
  (D-008). Fora de dia útil, ambos os sinais ficam desligados.
- Horários configuráveis em `appsettings.json` → `WorkDay`.

### 2.3 Tarefas (fonte: Google Tasks)

| Sinal | Condição | Nv | Motivo (exemplo) |
|-------|----------|----|------------------|
| `TaskOverdue` | ≥1 tarefa com vencimento passado | 2 | `2 tarefas vencidas` |
| — | contador de tarefas abertas | 0 | sempre visível, sem cor |

**O contador nunca colore a barra.** Um número não é um alarme. Ter 12 tarefas abertas é
uma terça-feira, não uma emergência.

### 2.4 E-mail (fonte: Gmail)

| Sinal | Condição | Nv |
|-------|----------|----|
| — | contador de não-lidos | **0, sempre** |

**Decisão explícita: e-mail não lido nunca colore a barra, em nenhum nível.** Caixa de
entrada com não-lidos é o estado permanente de qualquer pessoa; se isso pudesse acender
cor, a cor estaria sempre acesa. O contador informa, não alarma.

## 3. Resumo visual

```
Offline    ▓▓▓▓▓  cinza dessaturado, contadores em —, tudo clicável
Calm       ░░░░░  neutro
Info       ▒▒▒▒▒  accent discreto
Attention  ▓▓▓▓▓  âmbar
Critical   █████  vermelho  →  ▓█▓█▓  pisca após 5 min  →  clique  →  ░░░░░
```

## 4. Arbitragem

```
se estadoOffline:  exibir Offline, ignorar todos os sinais
senão:             severidadeEfetiva = max(sinaisAtivos não suprimidos)
                   motivoEfetivo     = motivo do sinal vencedor
```

**Desempate**, quando dois sinais têm a mesma severidade — vence a categoria mais alta:

1. Ciclo de call — porque é irreversível no tempo
2. Fronteira do dia
3. Tarefas
4. E-mail (nunca compete; é sempre 0)

*(Saúde do sistema não aparece aqui: ela virou o estado `Offline` do §0, que precede a escala.)*

**Invariantes** (candidatos naturais a teste automatizado):

- **I1.** *(revista em D-012)* A barra tem no máximo **duas** áreas coloridas, com vocabulários
  distintos: o slot de tempo (ambiente, sempre presente) e o chip de motivo (alarme, raro).
  Nunca **dois alarmes** competindo — o chip continua exibindo uma severidade por vez.
- **I2.** Toda severidade ≥ 1 tem motivo legível não vazio. Nunca colorir sem explicar.
- **I3.** Todo sinal de nível 3 é **reconhecível por clique**, e reconhecer sempre o remove
  imediatamente. Nenhum nível 3 pode ser inescapável. Esta é a invariante que faz o requisito
  "só quando realmente precisa" ser estrutural em vez de aspiracional: o vermelho persiste e
  escala, mas você sempre tem um gesto de um clique para dizer "eu vi".
- **I4.** Histerese: mínimo de 20s num nível antes de rebaixar, para não piscar em torno de uma
  fronteira. Não se aplica a reconhecimento, que é sempre imediato.
- **I5.** Subida de nível é imediata — histerese só atrasa a descida, nunca a subida.
- **I6.** Contadores (tarefas, e-mail) são renderizados no nível `Calm` mesmo quando a barra
  está vermelha por outro motivo. A cor pertence ao estado, não aos números.
- **I7.** No estado `Offline`, nenhum contador exibe número e nenhum sinal é avaliado.
- **I8.** A escalada para piscante nunca ocorre antes de 5 min no nível 3, e nunca com período
  menor que 1s.

## 5. Toasts vs. cor

Cor é **ambiente** (você olha quando quer). Toast é **interrupção** (ele te acha). Regras
diferentes:

| Nível | Cor | Toast |
|-------|-----|-------|
| `Offline` | sim (cinza) | uma vez, quando entra no estado |
| 0 `Calm` | sim (neutro) | nunca |
| 1 `Info` | sim | nunca |
| 2 `Attention` | sim | só em transições escolhidas: `MeetingImminent`, `MeetingStarted`, `MiddayCheckpoint` |
| 3 `Critical` | sim | duas vezes: ao entrar, e ao escalar para piscante em 5 min |

Supressões:

- Nunca emitir toast durante compartilhamento de tela ou app em tela cheia
  (`SHQueryUserNotificationState` → `QUNS_PRESENTATION` / `QUNS_BUSY` / `QUNS_RUNNING_D3D_FULL_SCREEN`).
  A cor continua mudando; só a interrupção é retida.
- Respeitar Focus Assist / Não Perturbe do Windows para níveis ≤ 2. Nível 3 ignora, porque
  o custo de perder é maior que o custo de interromper.
- Deduplicação por `(idDoSinal, idDaOcorrência)` — estourar a mesma reunião não gera toast
  a cada tick.

## 6. Fluxo de re-consent

Acontece toda semana por design (D-003), então precisa ser barato:

1. Barra entra em `Offline` cinza com `Login expirado — clique para entrar`
2. Clique abre o navegador padrão, onde a conta TOTVS **já está logada**
3. `login_hint` pré-seleciona a conta — você só clica em **Permitir**
4. Loopback local captura o código, a aba fecha, a barra volta ao normal sem reiniciar o app

Custo real: dois cliques, sem digitar senha, sem 2FA. Ver D-003 para por que não é possível
reaproveitar a sessão do Chrome diretamente.

## 7. Identidade de ocorrência e supressão

Reconhecer suprime uma **ocorrência**, e a identidade dela é:

```
occurrenceId = (eventId, início, fim)
```

Consequência direta: se a reunião for **prorrogada** no calendário depois de você reconhecer,
o `fim` muda, a identidade muda, e o sinal **volta a disparar**. É o comportamento desejado —
prorrogar é uma mudança material, e o reconhecimento anterior se referia a outra coisa.

Para sinais não ligados a evento, a identidade é `(nomeDoSinal, dataLocal)`:
`DayEnded` reconhecido vale até a virada do dia; `MiddayCheckpoint`, o mesmo.

## 8. Reuniões sobrepostas — o evento ativo

Quando duas ou mais reuniões candidatas se sobrepõem no instante atual (você aceitou as duas),
o Tempus não tem como saber em qual você está, e adivinhar produz o pior tipo de ruído: um
`MeetingRanIntoNext` falso no fim da primeira.

Solução: **perguntar**, o que é coerente com D-006 — o usuário declara, o app não infere.

| Sinal | Condição | Nv | Motivo |
|-------|----------|----|--------|
| `MeetingAmbiguous` | ≥2 eventos candidatos sobrepondo o instante atual, sem escolha feita | 1 | `2 reuniões agora — qual?` |

Mecânica:

- Clicar abre um seletor curto com os eventos concorrentes. O escolhido passa a ser o
  **evento ativo** e é o **único** que alimenta os sinais do §2.1; os demais são ignorados até
  terminarem.
- **Antes da escolha**, um padrão determinístico assume, para a barra nunca ficar sem estado:
  `accepted` > `tentative` > `needsAction`; empate resolve pelo início mais cedo; depois pelo
  fim mais cedo.
- O horário de **fim do evento ativo** é o que define `MeetingEnded` e `MeetingRanIntoNext`.
- "Próximo evento" para efeito de `MeetingRanIntoNext` é o primeiro que começa **após o fim do
  evento ativo e não se sobrepõe a ele**. É essa cláusula que elimina o falso positivo: uma
  reunião que você aceitou em paralelo nunca conta como "próxima invadida".
- A escolha vale pela janela de sobreposição. Quando o evento ativo termina, reavaliar.
- `MeetingAmbiguous` é nível 1: é uma pergunta, não um alarme.

## 9. Questões abertas

### Q-01 — A barra da Fase 1 alarma sem oferecer saída  ✅ FECHADA em 2026-08-18

**Aberta em 2026-08-17, encontrada em uso.** O usuário procurou "um botão para marcar a reunião
como concluída" e não achou — porque não existe no caminho de dados reais. `Acknowledged` só está
ligado no modo demo, e `BuildState` nunca liga `CanAcknowledge`: sem a flag, não há clique nem
item de menu.

Isso é consequência esperada de a máquina de severidade ser da Fase 3, mas produz um estado que
**contradiz a regra 2 do projeto** — "todo nível 3 é reconhecível; nenhum é inescapável". A barra
já exibe texto de alarme com dados reais ("Daily acabou — Review já começou", em vermelho) vindo
do humor temporal (§1.5), que não passa pela escala 0–3 e por isso escapou da regra.

Decidido em 2026-08-17 **deixar para a Fase 3**, que é a dona do assunto e vai tratá-lo por
inteiro. Registrado aqui para que não seja redescoberto como bug.

A questão de projeto que a Fase 3 precisa responder: **o que exatamente o clique silencia, e até
quando?** Só aquela ocorrência (§7 já define identidade de ocorrência), ou o humor temporal
inteiro até a próxima transição? Reconhecer "Daily acabou" deve calar também "Review já começou",
que é um fato diferente sobre outro evento?

---

As questões das duas primeiras rodadas foram fechadas em D-006, D-007, D-008, D-009, D-010 e nas
seções §2.1, §7 e §8.

Ao abrir uma nova, registrar aqui em vez de assumir em silêncio no código.

---

## 10. Reconhecimento — resposta ao Q-01

**Fechado em 2026-08-18**, antes da Fase 3, porque a lacuna apareceu no uso: uma call estourada
deixava a barra vermelha por 10 minutos sem saída, exatamente durante a reunião seguinte.

### O que o clique silencia

A identidade do §7 — `(eventId, início, fim)` — ganha o **sinal**:

```
occurrenceId = (sinal, eventId, início, fim)
```

Sem o sinal, reconhecer `Encerrando` às 14:56 calaria o `Estourou` das 15:01. E estourar é um fato
**novo e pior**, não a continuação do anterior: é o fato que o produto existe para acusar. Quem
reconhece "está acabando" não está perdoando de antemão o estouro que ainda não aconteceu.

A consequência do §7 continua valendo: prorrogar a reunião muda o `fim`, muda a identidade, e o
sinal volta a disparar.

### Reconhecer não apaga o fato, apaga o alarme

O estado desce ao equivalente calmo, em vez de sumir:

| Reconhecido | Vira | Por quê |
|-------------|------|---------|
| `Estourou` | o estado que existiria sem o estouro | livre, ou a próxima reunião |
| `Encerrando` | `Ocupado` | você continua na reunião |
| `Começando` | `Em breve` | a reunião continua chegando |

A informação permanece legível; o que sai é o bloco aceso. É o que a invariante I3 pede — "volta
ao normal imediatamente" — sem mentir dizendo que o compromisso deixou de existir.

### Onde se clica

O bloco de estado (D-023). Reconhecer **ganha de entrar na call**, porque em `Estourou` e
`Encerrando` você já está ou esteve na reunião. Entrar continua a um clique no texto do
compromisso, ao lado, e no menu de contexto (D-016).

Persistido por dia em `acknowledged.json`. Reconhecer um alarme e vê-lo voltar após um restart
ensinaria a ignorar o vermelho — o único ativo que este modelo não pode perder.
