# Tempus

Barra sempre visível sobre a taskbar do Windows 11 que responde a uma pergunta: **"algo
precisa de mim agora?"** Agrega Google Calendar, Tasks e a contagem de não-lidos do Gmail.
Ferramenta pessoal de um único usuário — não é produto para distribuir.

## Leia primeiro

| Arquivo | Quando |
|---------|--------|
| [docs/SEVERITY.md](docs/SEVERITY.md) | **Sempre**, antes de tocar em qualquer coisa relacionada a cor, alerta, estado ou notificação. É o coração do produto. |
| [docs/SPEC.md](docs/SPEC.md) | Antes de mexer em UI, superfícies ou integrações. Contém a lista de fora-de-escopo. |
| [docs/DECISIONS.md](docs/DECISIONS.md) | Antes de propor mudança de stack, de abordagem de janela ou de OAuth. As alternativas já foram avaliadas. |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Para saber em que fase estamos e o que a fase atual exige. |
| [docs/DEPLOY.md](docs/DEPLOY.md) | Para publicar ou instalar em outra máquina. Roda em duas: home office e notebook da empresa. |

## Stack

- **WPF sobre .NET 8** (`net8.0-windows`), app não-empacotado. SDK instalado: 8.0.303.
- Interop Win32 pesado: posicionamento de janela, DPI, toasts, sessões de áudio.
- `Google.Apis.Calendar.v3`, `Google.Apis.Tasks.v1`, `Google.Apis.Gmail.v1`.
- Sem framework de UI de terceiros, sem MVVM toolkit — o app é pequeno demais para justificar.

## Estado atual

**Fase 0 concluída** (fundação documental). Fase 1 (spike de OAuth) e Fase 2 (spike visual da
barra) são as próximas e podem andar em paralelo.

⚠️ `Tempus/Tempus.csproj` ainda é um scaffold `Microsoft.NET.Sdk.Web` com um "Hello World" de
minimal API. Foi criado por engano, **não representa nenhuma decisão** e é descartado na Fase 2.
Não construa nada em cima dele.

## Regras deste projeto

1. **Cor é recurso escasso.** Nunca introduza um estado colorido novo sem adicioná-lo à tabela
   de `SEVERITY.md` e verificar as invariantes I1–I8. Nunca colore sem motivo legível.
   Se o vermelho aparecer mais de ~3× num dia normal, o modelo está errado.
2. **Nível 3 escala, não decai — e sai com um clique.** Vermelho sólido, piscando âmbar↔vermelho
   após 5 min, até o usuário clicar ("eu vi"). Todo nível 3 é reconhecível; nenhum é inescapável.
3. **Estado `Offline` precede a escala.** Sem login válido ou com sync > 10 min, a barra fica
   cinza dessaturada, contadores viram `—`, e nenhum sinal é avaliado. Cinza é um vocabulário
   diferente de alerta: significa "o Tempus está fora do ar", não "está tudo bem".
4. **Não inferir presença em call.** Sem detecção de microfone, câmera ou janela. O Tempus
   conhece o cronograma; o usuário comunica que viu, clicando (D-006).
5. **Nada de segredo no repo.** `client_secret*.json`, `token.json` e `appsettings.Local.json`
   estão no `.gitignore`. Token em disco usa DPAPI vinculado ao usuário.
6. **Nunca `SetParent` dentro de `Shell_TrayWnd`.** Posicionar por coordenada. O motivo está
   em D-002; é a diferença entre funcionar e quebrar em todo update do Windows.
7. **Toda renderização atrás de `IShellSurface`.** Lógica de domínio não conhece a superfície,
   para que o fallback de tray seja troca de implementação e não de arquitetura.
8. **Sinais são funções puras** de (dados sincronizados, hora, supressões ativas) → severidade +
   motivo. Testáveis sem UI e sem rede. Não misture I/O dentro deles.
9. **Sem polling em loop apertado.** Reagir a mensagens de janela; sync do Google a 60s
   (120s para Gmail). Consumo em repouso deve ser indistinguível de zero.
10. **Nunca mostrar dado velho como se fosse atual.** Contador obsoleto vira `—`, não o último
    número conhecido. O re-consent semanal é normal (D-003), mentir com confiança não é.
11. **Respeite a lista de fora-de-escopo** de `SPEC.md`. Keep e Chat foram cortados por motivos
    técnicos documentados em D-004, e a detecção de microfone por D-006 — nenhum por falta de
    tempo.

## Como trabalhamos

- Uma fatia vertical por sessão, terminando em build que roda.
- Decisão de arquitetura nova → entrada em `DECISIONS.md` no mesmo commit que a implementa.
- Questão em aberto vira item em `SEVERITY.md` §5 em vez de virar suposição no código.
- Plan mode para design; modo normal para implementação.

## Comandos

```bash
dotnet build Tempus/Tempus.csproj              # deve terminar com 0 avisos
./Tempus/bin/Debug/net8.0-windows/Tempus.exe   # dados reais do Google
./Tempus/bin/Debug/net8.0-windows/Tempus.exe --demo   # dados falsos, sem rede
dotnet test                                    # a definir (testes das invariantes)
```

Publicar para outra máquina — exe único autocontido, ver [docs/DEPLOY.md](docs/DEPLOY.md):

```bash
dotnet publish Tempus/Tempus.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o publish
```

Arquivos de dados (fora do repo, por máquina): `%APPDATA%\Tempus\client_secret.json` e
`%APPDATA%\Tempus\tokens\`. O token é DPAPI vinculado a usuário **e** máquina — não copiar entre
máquinas.
