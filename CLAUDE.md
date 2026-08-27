# Tempus

Barra sempre visÃ­vel sobre a taskbar do Windows 11 que responde a uma pergunta: **"algo
precisa de mim agora?"** Agrega Google Calendar, Tasks e a contagem de nÃ£o-lidos do Gmail.
Ferramenta pessoal de um Ãºnico usuÃ¡rio â€” nÃ£o Ã© produto para distribuir.

## Leia primeiro

| Arquivo | Quando |
|---------|--------|
| [docs/SEVERITY.md](docs/SEVERITY.md) | **Sempre**, antes de tocar em qualquer coisa relacionada a cor, alerta, estado ou notificaÃ§Ã£o. Ã‰ o coraÃ§Ã£o do produto. |
| [docs/SPEC.md](docs/SPEC.md) | Antes de mexer em UI, superfÃ­cies ou integraÃ§Ãµes. ContÃ©m a lista de fora-de-escopo. |
| [docs/DECISIONS.md](docs/DECISIONS.md) | Antes de propor mudanÃ§a de stack, de abordagem de janela ou de OAuth. As alternativas jÃ¡ foram avaliadas. |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Para saber em que fase estamos e o que a fase atual exige. |
| [docs/DEPLOY.md](docs/DEPLOY.md) | Para publicar ou instalar em outra mÃ¡quina. Roda em duas: home office e notebook da empresa. |

## Stack

- **WPF sobre .NET 8** (`net8.0-windows10.0.19041.0`), app nÃ£o-empacotado. SDK instalado: 8.0.303.
  A versÃ£o do Windows no TFM veio com os toasts (D-028), que precisam das projeÃ§Ãµes WinRT.
- Interop Win32 pesado: posicionamento de janela, DPI, toasts, sessÃµes de Ã¡udio.
- `Google.Apis.Calendar.v3`, `Google.Apis.Tasks.v1`, `Google.Apis.Gmail.v1`.
- Sem framework de UI de terceiros, sem MVVM toolkit â€” o app Ã© pequeno demais para justificar.

## Estado atual

**Todas as fases concluÃ­das** (2026-08-25). A barra roda em uso real com dados do Google: onze
sinais do Â§2 avaliados como funÃ§Ãµes puras, arbitragem com desempate por categoria, histerese de
20 s na descida, nÃ­vel 3 que escala e sai com um clique, `Offline` que anula a escala, pausas de
descanso, tela de configuraÃ§Ã£o e toasts com botÃ£o â€” nÃ­vel 3 mais o aviso de reuniÃ£o em dois tempos,
passageiro aos 10 min e fixo aos 2 atÃ© o clique (D-039).

Tasks Ã© **bidirecional**: criar, concluir, reabrir, excluir, datar e renomear, tudo pela
`WriteQueue` com escrita otimista, reversÃ£o e "nÃ£o salvou" visÃ­vel (D-029, D-042, D-045). O painel
de agenda mostra **os prÃ³ximos 15 dias** com as janelas livres de cada um (D-043).

**NÃ£o sobra cÃ³digo de fase nenhuma.** O que resta estÃ¡ em `ROADMAP.md`, e Ã© de trÃªs tipos:
verificaÃ§Ã£o de ambiente que nunca rodou (`TaskbarCreated` com restart do explorer), decisÃµes
adiadas de propÃ³sito Ã  espera de convÃ­vio (Q-02 do `SEVERITY.md`, toast de `Offline`, nÃ­vel 3 furar
o NÃ£o Perturbe) e o backlog sem compromisso.

`Tempus.Tests` (xUnit) tem 253 testes e roda em ~110 ms. As oito invariantes I1â€“I8 tÃªm teste e
nenhum estÃ¡ com `Skip`. **Feche o app antes de compilar** â€” o exe em execuÃ§Ã£o trava o build.

## Regras deste projeto

1. **Cor Ã© recurso escasso.** Nunca introduza um estado colorido novo sem adicionÃ¡-lo Ã  tabela
   de `SEVERITY.md` e verificar as invariantes I1â€“I8. Nunca colore sem motivo legÃ­vel.
   Se o vermelho aparecer mais de ~3Ã— num dia normal, o modelo estÃ¡ errado.
2. **NÃ­vel 3 escala, nÃ£o decai â€” e sai com um clique.** Vermelho sÃ³lido, piscando Ã¢mbarâ†”vermelho
   apÃ³s 5 min, atÃ© o usuÃ¡rio clicar ("eu vi"). Todo nÃ­vel 3 Ã© reconhecÃ­vel; nenhum Ã© inescapÃ¡vel.
3. **Estado `Offline` precede a escala.** Sem login vÃ¡lido ou com sync > 10 min, a barra fica
   cinza dessaturada, contadores viram `â€”`, e nenhum sinal Ã© avaliado. Cinza Ã© um vocabulÃ¡rio
   diferente de alerta: significa "o Tempus estÃ¡ fora do ar", nÃ£o "estÃ¡ tudo bem".
4. **NÃ£o inferir presenÃ§a em call.** Sem detecÃ§Ã£o de microfone, cÃ¢mera ou janela. O Tempus
   conhece o cronograma; o usuÃ¡rio comunica que viu, clicando (D-006).
5. **Nada de segredo no repo.** `client_secret*.json`, `token.json` e `appsettings.Local.json`
   estÃ£o no `.gitignore`. Token em disco usa DPAPI vinculado ao usuÃ¡rio.
6. **Nunca `SetParent` dentro de `Shell_TrayWnd`.** Posicionar por coordenada. O motivo estÃ¡
   em D-002; Ã© a diferenÃ§a entre funcionar e quebrar em todo update do Windows.
7. **Toda renderizaÃ§Ã£o atrÃ¡s de `IShellSurface`.** LÃ³gica de domÃ­nio nÃ£o conhece a superfÃ­cie,
   para que o fallback de tray seja troca de implementaÃ§Ã£o e nÃ£o de arquitetura.
8. **Sinais sÃ£o funÃ§Ãµes puras** de (dados sincronizados, hora, supressÃµes ativas) â†’ severidade +
   motivo. TestÃ¡veis sem UI e sem rede. NÃ£o misture I/O dentro deles.
9. **Sem polling em loop apertado.** Reagir a mensagens de janela; sync do Google a 60s
   (120s para Gmail). Consumo em repouso deve ser indistinguÃ­vel de zero.
10. **Nunca mostrar dado velho como se fosse atual.** Contador obsoleto vira `â€”`, nÃ£o o Ãºltimo
    nÃºmero conhecido. O re-consent semanal Ã© normal (D-003), mentir com confianÃ§a nÃ£o Ã©.
11. **Respeite a lista de fora-de-escopo** de `SPEC.md`. Keep e Chat foram cortados por motivos
    tÃ©cnicos documentados em D-004, e a detecÃ§Ã£o de microfone por D-006 â€” nenhum por falta de
    tempo.
12. **Escrita nunca falha em silÃªncio.** Todo gesto que muda dado no Google passa pela
    `WriteQueue`: aparece na tela na hora, repete sozinho, e vira "nÃ£o salvou" visÃ­vel se desistir
    (D-029). Nada de `catch { return false; }` com o resultado descartado por quem chamou â€” foi
    exatamente isso que fazia um clique sumir sem deixar rastro.

## Como trabalhamos

- Uma fatia vertical por sessÃ£o, terminando em build que roda.
- DecisÃ£o de arquitetura nova â†’ entrada em `DECISIONS.md` no mesmo commit que a implementa.
- QuestÃ£o em aberto vira item em `SEVERITY.md` Â§5 em vez de virar suposiÃ§Ã£o no cÃ³digo.
- Plan mode para design; modo normal para implementaÃ§Ã£o.

## Comandos

```bash
dotnet build Tempus/Tempus.csproj              # deve terminar com 0 avisos
./Tempus/bin/Debug/net8.0-windows10.0.19041.0/Tempus.exe          # dados reais do Google
./Tempus/bin/Debug/net8.0-windows10.0.19041.0/Tempus.exe --demo   # dados falsos, sem rede
./Tempus/bin/Debug/net8.0-windows10.0.19041.0/Tempus.exe --demo --fail-writes  # escrita sempre falha
./Tempus/bin/Debug/net8.0-windows10.0.19041.0/Tempus.exe --toast-probe  # notificaÃ§Ã£o: entrega E clique de volta
dotnet test Tempus.Tests/Tempus.Tests.csproj   # domÃ­nio e invariantes; feche o app antes
```

Publicar para outra mÃ¡quina â€” exe Ãºnico autocontido, ver [docs/DEPLOY.md](docs/DEPLOY.md):

```bash
dotnet publish Tempus/Tempus.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o publish
```

Arquivos de dados (fora do repo, por mÃ¡quina): `%APPDATA%\Tempus\client_secret.json` e
`%APPDATA%\Tempus\tokens\`. O token Ã© DPAPI vinculado a usuÃ¡rio **e** mÃ¡quina â€” nÃ£o copiar entre
mÃ¡quinas.
