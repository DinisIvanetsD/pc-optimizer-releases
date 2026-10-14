# Medir FPS a sério — desenho

Data: 2026-10-14 · Estado: aprovado em conversa (3 secções), à espera de revisão deste documento

## Objetivo

Hoje o Rigwise promete "mais FPS", mas não mede FPS. O "Antes e depois" do Início compara arranque, RAM, ping e
espaço, e o botão "Otimizar para este jogo" não mostrava efeito nenhum. O objetivo é a app medir os FPS de qualquer
jogo e provar, com números, se uma otimização ajudou. Quando não ajudou, a app diz isso e oferece desfazer.

Sucesso:
- Em cada sessão de jogo, a app grava FPS médios, 1% low e engasgos, sem o utilizador fazer nada.
- O overlay mostra os FPS ao vivo, também em jogos single-player.
- Um teste guiado de 60 s, antes e depois de otimizar, dá um veredito: melhorou, sem diferença ou piorou.
- Sem diferença ou pior: um clique desfaz os ajustes aplicados entre os dois testes.
- Depois de uma ativação única, funciona sem abrir a app como administrador.

## Decisões tomadas na conversa

| Pergunta | Decisão |
|---|---|
| Automático ou teste guiado? | Os dois. |
| Otimização sem ganho? | Medir o conjunto antes/depois; sem ganho ou pior, sugerir desfazer num clique. |
| Que jogos? | Qualquer jogo: a lista conhecida e qualquer programa em ecrã inteiro a desenhar frames. |
| Administrador? | Pedir uma vez e juntar o utilizador ao grupo "Performance Log Users" (reversível). |
| Como ler os FPS? | PresentMon da Intel incluído na app (opção A). |

## Restrições

- PresentMon 2.6.0 (21/09/2026), `PresentMon-2.6.0-x64.exe`, 0,9 MB, licença MIT (Intel), de
  https://github.com/GameTechDev/PresentMon/releases/tag/v2.6.0.
  SHA-256: `b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af`.
  A licença MIT obriga a incluir o aviso de copyright ao lado do executável.
- O PresentMon lê eventos do Windows (ETW). Precisa de administrador ou de o utilizador estar no grupo
  "Performance Log Users". Não injeta nada no jogo, por isso não incomoda os anti-cheats.
- A app abre sem administrador por defeito, e isso não muda.
- As sessões ETW não morrem com o processo: uma sessão esquecida tem de ser limpa no arranque seguinte.
- Bilingue PT/EN: todas as frases novas passam por `Loc.T` e entram no `en.json`.

## Desenho

### 1. Peças

**`Core/Benchmark/FrameCapture.cs`**: lança o PresentMon só para o processo do jogo e devolve os frames ao vivo.
- Comando: `PresentMon-2.6.0-x64.exe --process_id <pid> --output_stdout --no_console_stats
  --session_name Rigwise --stop_existing_session --terminate_on_proc_exit`.
- O nome de sessão fixo e o `--stop_existing_session` limpam uma sessão que tenha ficado pendurada.
- Lê o CSV linha a linha e encontra as colunas pelo nome do cabeçalho. O tempo entre frames vem de `FrameTime`
  (métricas 2.x) ou, se não existir, de `MsBetweenPresents` (1.x). Linhas com `NA` ou cortadas são ignoradas.
- Evento `Frame(double ms)` numa thread de fundo; `Dispose` mata o processo e espera que saia.
- Os nomes exatos das colunas confirmam-se na primeira corrida real (passo 1 do plano), antes de escrever o resto.

**`Core/Benchmark/FrameStats.cs`**: contas puras, sem Windows nem ficheiros.
- A partir de uma lista de tempos entre frames: FPS médio (frames ÷ tempo total), 1% low e 0,1% low
  (FPS do percentil 99 e 99,9 dos tempos), engasgos (frames com mais de 2,5× a mediana) e duração.
- Janela deslizante dos últimos 10 s, para o overlay e o cartão.

**`Core/Benchmark/FpsHistory.cs`**: guarda em `fps.json` na pasta de dados da app, como o `metrics.json`.
- Sessões automáticas: jogo, exe, início, duração, FPS médio, 1% low, engasgos (guarda as últimas 200).
- Testes guiados: os mesmos números mais o papel (Antes ou Depois) e a data. Um "Antes" pendente por jogo.
- Variável de ambiente `PCOPT_FPS_FILE` para os testes, como `PCOPT_METRICS_FILE`.

**`Core/Benchmark/FpsVerdict.cs`**: compara Antes e Depois.
- Ganho = (depois − antes) ÷ antes, no FPS médio e no 1% low.
- Melhorou: algum dos dois sobe 3% ou mais e nenhum desce mais de 3%.
- Piorou: algum dos dois desce mais de 3% e nenhum sobe 3% ou mais.
- Misto: um sobe 3% ou mais e o outro desce mais de 3%.
- Sem diferença: os dois ficam dentro de ±3% (a variação normal entre testes de 60 s).
- Os ajustes a desfazer são as entradas do Histórico do tipo ajuste (`UndoKind.Tweak`) aplicadas entre a data do
  Antes e a do Depois, e ainda não desfeitas.

**Deteção de qualquer jogo** (em `GameCatalog`/`GameMonitor`):
- Continua a procurar os jogos conhecidos primeiro.
- Senão, olha a janela em primeiro plano: se ocupa o monitor inteiro (ecrã inteiro ou sem bordas) e o processo não
  está na lista de exclusão (browsers, leitores de vídeo, Explorador, o próprio Rigwise, apps de escritório), é um
  jogo candidato.
- Um jogo assim detetado é medido em silêncio: não abre a janela "Otimizar este jogo?", para não incomodar com
  falsos positivos. Aparece no cartão e no histórico com o nome do executável.

**`Core/Benchmark/PerfLogAccess.cs`**: ativar FPS sem administrador.
- `IsMember`: o token do utilizador tem o SID `S-1-5-32-559` (o grupo tem nomes diferentes em cada língua).
- `CanCapture`: administrador ou `IsMember`.
- `EnableAsync`: corre como administrador `Add-LocalGroupMember -SID S-1-5-32-559 -Member <utilizador>`.
  Regista no Histórico com o `UndoKind` novo `PerfLogUsers`. Desfazer corre `Remove-LocalGroupMember`.
- O grupo só entra no token depois de terminar sessão no Windows. A app guarda que a ativação foi feita, para
  distinguir "falta ativar" de "falta terminar sessão".

**Distribuição**: `src/PCOptimizer.App/tools/PresentMon-2.6.0-x64.exe` e `tools/PresentMon-LICENSE.txt`,
copiados para a saída (Content, `CopyToOutputDirectory`). Um teste confirma o SHA-256 acima.

### 2. O que o utilizador vê

**Overlay** (`GameOverlay`): `144 FPS · 1% 97 │ 32 ms · perda 0% · jitter 2 ms`, atualizado a cada segundo com
a janela dos últimos 10 s. Em jogos sem servidor, só a parte dos FPS.

**Rede › Modo jogo**: o quadro de números ao vivo ganha "FPS" e "1% low". Quando o jogo fecha:
"Sessão de 52 min: 144 FPS médios, 1% low 97, 3 engasgos."

**Rede › 📈 FPS por jogo** (cartão novo, logo abaixo do Modo jogo):
- Uma linha por jogo: última sessão, média das últimas 5 sessões e, se houver, "Antes 142 → Depois 158 (+11%) ✓".
- Botão **Testar FPS** quando há um jogo aberto.
- Sem acesso: botão "🔒 Ativar medição de FPS" (ou "Termina sessão no Windows para ativar os FPS").

**Teste guiado**:
1. **Testar FPS** no cartão, ou **Ctrl+Shift+F** dentro do jogo (atalho global, como o Ctrl+Shift+P do gravador).
2. O overlay conta "3, 2, 1" e mostra "A medir 60 s · fica no mesmo sítio".
3. Primeiro teste de um jogo, ou sem ajustes novos desde o último: fica como **Antes**. Mensagem: "Guardado como
   Antes. Otimiza e repete o teste no mesmo sítio."
4. Com um Antes pendente e ajustes aplicados depois dele: fica como **Depois** e mostra o veredito.
5. Ao abrir um jogo com um Antes pendente e ajustes novos, o cartão lembra: "Repete o teste no mesmo sítio".

**Veredito** (no cartão "FPS por jogo"):
- ✓ "Melhorou: +11% FPS médios, 1% low +15%."
- = "Sem diferença (±3% é a variação normal entre testes). Os 6 ajustes aplicados desde 14/10 não deram FPS
  neste jogo." Botões **Desfazer esses ajustes** e **Manter**.
- ✗ "Piorou: −7% FPS médios." Os mesmos botões, com Desfazer em primeiro.
- ± "Resultado misto: +6% FPS médios, mas 1% low −8% (mais engasgos)." Os mesmos botões, com Manter em primeiro.

**Otimização**: ao aplicar o perfil Gaming sem nenhum teste Antes, a confirmação acrescenta a dica "Mede os FPS
antes (Rede › FPS por jogo) para provar o ganho." Não bloqueia.

**Início › Antes e depois**: uma linha "FPS (jogo)" com o último par Antes/Depois, se existir.

### 3. Quando algo corre mal

| Situação | Comportamento |
|---|---|
| Sem administrador e fora do grupo | Cartão mostra "🔒 Ativar medição de FPS"; o resto do modo jogo funciona como hoje. |
| Ativado mas sem terminar sessão | "Termina sessão no Windows para ativar os FPS." |
| O PresentMon falha, não arranca ou fecha | FPS desaparecem do overlay e do cartão; erro no log (`App.Log`); sem crash. |
| Ecrã inteiro exclusivo | O overlay não aparece (limite atual); os FPS continuam a ser gravados. |
| Teste interrompido (Alt+Tab, jogo fecha antes dos 60 s) | Descartado: "Teste interrompido, repete." |
| Menos de 20 s de frames válidos | Sessão gravada sem veredito; teste descartado. |
| A app vai abaixo a meio | Na próxima captura, `--stop_existing_session` limpa a sessão ETW. |

### 4. Testes

xUnit, junto aos existentes:
- `FrameStats`: listas conhecidas de tempos dão a média, 1% low, 0,1% low e engasgos certos.
- Leitor do CSV: cabeçalho 2.x (`FrameTime`), cabeçalho 1.x (`MsBetweenPresents`), linhas `NA`, linhas cortadas.
- `FpsVerdict`: os limites de ±3% nos quatro resultados; ajustes a desfazer só entre as duas datas.
- Deteção: browsers e leitores de vídeo excluídos; programa em ecrã inteiro aceite; jogo conhecido tem prioridade.
- `FpsHistory`: gravar e ler de volta; um Antes pendente por jogo.
- O ficheiro `tools/PresentMon-2.6.0-x64.exe` tem o SHA-256 fixado acima.

Verificação no PC do utilizador: um jogo real com e sem administrador; o teste guiado completo; o grupo entra e sai
pelo Histórico.

## Fora de âmbito

- Testar ajuste a ajuste.
- Dizer se o limite é a placa gráfica ou o processador (o PresentMon dá `MsGPUBusy`/`MsCPUBusy`; próximo passo).
- Retirar ajustes sem efeito comprovado: decide-se depois, com os dados destes testes.
- Overlay em ecrã inteiro exclusivo.
