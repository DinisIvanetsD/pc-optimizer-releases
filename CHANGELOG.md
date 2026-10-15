# Novidades

## 0.10.0
- **Menu mais simples**: 6 áreas em vez de 14 abas. Início, Corrigir (Diagnóstico, Otimização, Drivers), Jogos
  (Rede, FPS), Limpar (Disco, Arranque, Apps), Sistema (Componentes, Monitor, RAM) e Histórico. Os separadores de
  cada área aparecem ao lado do menu. As Definições passam para o botão no canto.
- **O que se passa?** (Início): escolhe o problema (lag, poucos FPS, PC lento, ecrãs azuis, falta de espaço) e a
  app leva-te ao sítio certo e começa a verificação.

## 0.9.7
- **FPS**: se a Rigwise for fechada à força com um jogo aberto, o PresentMon já não fica a correr. Quando o jogo
  para de desenhar (minimizado ou pendurado), os FPS mostram -- em vez de ficarem congelados.
- **Site e README** a preto e branco, com capturas novas. O site fica mais legível no telemóvel e ganha a página
  "Code signing policy".
- Preparação da assinatura de código pela SignPath Foundation (ainda desligada).

## 0.9.6
- **Visual novo a preto e branco**: sem roxo. Fundo quase preto, cartões em cinzento neutro e botões principais
  brancos. Só o verde, o amarelo e o vermelho de estado têm cor.
- **Logótipo e ícone** a condizer: branco com o raio preto.

## 0.9.5
- **FPS de qualquer jogo** (aba nova **FPS**): FPS médios, 1% low e engasgos ao vivo no Modo jogo e no overlay, e um resumo
  quando o jogo fecha. Usa o PresentMon da Intel (incluído, licença MIT), sem injetar nada no jogo.
- **Testar FPS** (Ctrl+Shift+F ou na aba **FPS**): 60 s antes e depois de otimizar, com o veredito
  Melhorou, Sem diferença, Piorou ou Misto. Se não ajudou, **Desfazer esses ajustes** num clique. Avisa quando os
  FPS parecem limitados (V-Sync) e quando falta reiniciar para os ajustes funcionarem.
- **Medir FPS sem administrador**: um botão junta o teu utilizador ao grupo "Performance Log Users" do Windows
  (uma vez, reversível no Histórico).
- **Modo jogo deteta qualquer jogo** em ecrã inteiro, não só os da lista, e mede-o sem perguntar.
- **Otimizar para este jogo** passa a fazer efeito na ligação: torrents e gestores de downloads ficam em pausa
  enquanto jogas, o jogo fica com prioridade alta e o plano Desempenho Máximo já não é trocado. Tudo volta ao
  normal quando o jogo fecha.

## 0.9.4
- **Bateria** (Componentes): saúde em relação à capacidade de fábrica, ciclos de carga e carga atual, com aviso
  quando já está gasta. Lê o driver de bateria do Windows, sem administrador.
- **Processador** (Componentes): velocidade real agora e se está a abrandar por calor (zonas térmicas do Windows),
  com conselhos. A temperatura aparece com a app como administrador e o driver PawnIO instalado.
- **Ventoinhas**: o cartão já não repete o título e explica numa nota onde as ver.
- Versões publicadas sozinhas pelo GitHub Actions ao enviar uma tag (vX.Y.Z).

## 0.9.3
- Corrige um erro ao abrir a janela pelo ícone da barra de tarefas depois de ela ter fechado.
- Código-fonte público em github.com/DinisIvanetsD/rigwise, com licença MIT.

## 0.9.2
- **Ecrãs azuis** (Diagnóstico): o título diz a hora do crash ("hoje às 19:02") e o código com uma explicação
  (ex.: PFN_LIST_CORRUPT = quase sempre RAM instável). Aviso na hora, logo que o PC volta a arrancar.
  Novo botão **Descobrir o driver**, que reabre como administrador para ler os relatórios.
- **Bloqueio de anúncios mais leve**: passa para ~10 mil domínios (AdAway + Peter Lowe). A lista antiga, de 70 mil,
  deixava o Windows lento a ligar apps à internet (Discord, browsers). Quem a tem ligada é avisado e atualiza num clique.
- **Visual**: botões e menu em forma de pílula, com animações suaves; Otimização com muito menos texto;
  cartões do Diagnóstico com **Ver detalhes**.

## 0.9.1
- **Crashes dos jogos** (Diagnóstico): encontra os jogos da Steam e da Epic, lê os ficheiros de crash (.dmp) e os
  registos do Windows, diz onde o jogo rebenta e a causa provável (driver gráfico, overlay, ficheiros do jogo ou
  RAM instável), com os botões **Verificar ficheiros** (Steam/Epic), Testar a RAM ou Ver drivers.

## 0.9.0
- **Gravar um problema** (Diagnóstico): grava CPU, disco, RAM, placa gráfica, ping e as apps que mais gastam.
  Quando o PC engasgar, carrega em **Ctrl+Shift+P** (também dentro do jogo): guarda 30 s antes e 15 s depois,
  mostra um gráfico e explica o que mudou nesse momento e o que testar a seguir.
- **O que mudou?** (Histórico): linha do tempo com ecrãs azuis, crashes, drivers instalados, atualizações do Windows,
  programas instalados e alterações do Rigwise, e quais mudanças aconteceram pouco antes de os problemas começarem.
- **Relatório** com as gravações e a linha do tempo, para mostrar a um técnico.

## 0.8.3
- As versões e o site passam para github.com/DinisIvanetsD/rigwise (o endereço antigo continua a funcionar).

## 0.8.2
- Cores em roxo, a condizer com o logótipo.

## 0.8.1
- Logótipo novo do Rigwise (ícone da app, barra de tarefas e site).

## 0.8.0
- **A app chama-se agora Rigwise** (antes PC Optimizer). Os atalhos e o nome em "Apps instaladas" mudam sozinhos
  ao atualizar; as tuas definições, o histórico e os ajustes ficam como estavam.

## 0.7.2
- **Tutorial** na primeira vez: um balão por baixo de cada aba explica para que serve (Definições › Ver o tutorial).
- Sem assistente IA e sem modo simulação: a app ficou mais simples.
- **Apps**: o Brave, o Opera GX e outras apps instaladas pelo site do fabricante já aparecem como instaladas.
- **Componentes**: cartões redesenhados, em 3 colunas que ocupam a largura toda.
- **Definições**: sem o espaço vazio à direita em janelas médias.
- Animação suave ao mudar de página e destaque nos cartões ao passar o rato.
- **Arranque**: mostra quanto demorou o último arranque do Windows.
- Erro inesperado › "Reportar": abre uma página no GitHub já preenchida (sem o teu nome de utilizador); revês e envias tu.

## 0.7.1
- **Início › Contar só a partir de hoje**: depois de resolveres um problema, os ecrãs azuis, desligares e crashes
  antigos deixam de contar para a saúde. Os registos do Windows não são apagados e podes voltar aos 30 dias.
- **Definições** em ecrã cheio: os cartões passam para duas colunas.

## 0.7.0
- **Componentes** (aba nova): processador, placa gráfica (memória, driver, temperatura), RAM (avisa se o XMP/EXPO
  estiver desligado), placa-mãe e BIOS, discos (saúde), placas de rede e ventoinhas. "Copiar" para pedir ajuda.
- **Apps desatualizadas**: a aba Apps mostra as apps com versão nova e atualiza só as que escolheres.
  "Atualizar tudo" mostra a lista e pede confirmação antes de mexer.
- **Disco**: "Otimizar (TRIM)" nos SSD e "Desfragmentar" nos HDD, e se o TRIM está ligado.
- **Atualizações mais seguras**: a app confirma o tamanho, o hash SHA-256 e a versão do ficheiro antes de se atualizar.

## 0.6.2
- **Apps**: logótipo de cada app (37 das 53; as restantes mostram a inicial).

## 0.6.1
- **Apps** com novo visual: mosaicos por categoria, procura e filtros (Todas, Por instalar, Instaladas).
- **Otimização**: a explicação de cada perfil fica à vista e a descrição de cada ajuste aparece inteira.
- **Disco**: a página inteira faz scroll, não só a lista de baixo.

## 0.6.0
- **Apps**: aba nova para instalar de uma vez 53 apps populares (browsers, jogos, comunicação, multimédia,
  ferramentas, programação) pelo winget, o instalador oficial da Microsoft. Mostra as que já tens e
  "Atualizar tudo" põe todas as apps na versão mais recente. Desfazer uma instalação = desinstalar.
- **Apps do Windows que podes remover**: Notícias, Clipchamp, Xbox, Solitaire, Dicas, Teams pessoal… de uma vez.
- **Privacidade**: perfil novo na Otimização (telemetria, ID de publicidade, histórico de atividade, dicas,
  feedback, sugestões, Bing na pesquisa, Copilot). Tudo reversível.
- **Bloquear anúncios e rastreadores** (aba Rede): lista StevenBlack no ficheiro hosts, em todo o PC.
  Desligar repõe o ficheiro como estava.
- Linha de comandos: `app-install`, `app-uninstall` e `adblock on|off`.

## 0.5.3
- **Visual mais simples**: abas em cima (em vez da barra lateral) e um cabeçalho compacto com saúde, CPU e RAM.
- Explicações de cada ajuste, driver e opção de rede numa linha (o resto ao passar o rato).
- Menos texto: explicações encurtadas em todas as páginas.
- Correção: ao abrir, a página ficava vazia até mudares de aba (nenhuma aba ficava escolhida).
- Rede: ping, bufferbloat, DNS e QoS em "Mais ferramentas" (fechado).
- Disco: menos filtros (procurar, disco, ordenar e Mostrar) e uma linha a menos por programa.
- Otimização: sem a nota longa por cima dos ajustes.
- Atalhos do Início centrados.
- Diagnóstico: cada problema mostra 2 linhas (o resto ao passar o rato).
- Drivers: sem números repetidos nem explicações longas (o conselho aparece ao passar o rato).

## 0.5.2
- Modo jogo: o Windows Update é posto em pausa pela pausa oficial do Windows (como em Definições › Pausar),
  com fim automático em 4 h, e reposto quando o jogo fecha. Antes parava o serviço, que se reiniciava sozinho.

## 0.5.1
- Placa de rede: depois de afinar, espera que a placa volte antes de mostrar o resultado (antes dizia "não encontrei a placa").
- Inglês: categorias do diagnóstico e o aviso "jogo detetado".
- Linha de comandos: `nic-apply` e `nic-revert`.

## 0.5.0
- **Modo jogo** (aba Rede): deteta 35 jogos online (e os que acrescentares), descobre o servidor do jogo,
  mede ping/perda/jitter ao vivo e, se quiseres, otimiza o PC só enquanto jogas (energia máxima,
  prioridade de rede, Windows Update e sincronização em pausa). Tudo é reposto no fim, também depois de um crash.
- **Onde está o lag?**: segue o caminho até ao servidor salto a salto (como o MTR) e diz se o problema
  está na tua casa, no operador, na internet ou no servidor.
- **Overlay** por cima do jogo (Ctrl+Shift+O), sem injetar nada: seguro com anti-cheats.
- **Placa de rede afinada para jogos**: moderação de interrupções, poupança de energia e Nagle. Reversível.
- **English**: the app is now available in English (Definições › Geral › Idioma).

## 0.4.2
- **Atualizações automáticas** outra vez: as versões são publicadas num repositório público só de versões
  (o código continua privado).
- `PCOptimizer.exe --update`: procura e instala a versão nova sem janelas.
- Instalar com a app instalada aberta explica o que fazer (em vez de um erro do Windows).

## 0.4.1
- **Atualizar a partir de um ZIP** (Definições): para o repositório privado, com o ZIP dos artefactos do GitHub Actions.
- Instalação sem janelas: `PCOptimizer.exe --install [--desktop]`.
- Drivers: espera que o Windows acabe outras instalações, tenta 2 vezes e, se a instalação silenciosa falhar,
  abre o instalador oficial do fabricante (já verificado) para acabar com cliques.
- Linha de comandos: `maintenance` e `driver-install <id>`.
- Correção: a regra da LocalCache (Espaço em disco) dependia das apps instaladas no PC.

## 0.4.0 — acabamento
- Janela de boas-vindas na primeira vez (modo simulação, arrancar com o Windows).
- Atalhos de teclado: Ctrl+1…9/0 muda de página, F5 atualiza, Ctrl+F procura programas.
- A janela lembra-se do tamanho e da posição.
- Diagnóstico › **Exportar relatório** (HTML, sem o nome de utilizador, pronto a imprimir).
- Quebras de rede por falta de portas: depois da correção só contam as novas.
- Erros mais robustos: registo também de tarefas de fundo, "Copiar detalhes", rotação do registo.
- Nítida em ecrãs com escalas diferentes (DPI por monitor).
- Integração contínua no GitHub: compila e corre os testes a cada envio.

## 0.3.0 — melhorias essenciais
- **Início**: saúde do PC explicada, evolução, "o que fazer agora", **antes/depois**.
- **Ecrãs azuis**: leitura dos minidumps (driver culpado ou RAM).
- **Histórico** com **Desfazer** (ajustes, arranque, DNS, QoS, Reciclagem, drivers pelo ponto de restauro).
- **Definições**: arrancar com o Windows, manutenção automática, **modo simulação**, atualizações, instalar no PC.
- Manutenção automática: procura problemas, drivers novos e discos cheios e avisa.
- Testes automáticos (xUnit).
- Correção: "Tratar de tudo" nos drivers rebentava se um driver fosse instalado à parte entretanto.

## 0.2.0
- **Drivers**: dispositivos sem driver ou com erro, Windows Update, descarregar e instalar das fontes oficiais
  (NVIDIA, AMD, Catálogo do Microsoft Update), limpeza de versões antigas.
- **Espaço em disco**: programas úteis/inúteis, o que ocupa espaço com "é seguro apagar?", confirmações por risco.
- Vigia de quebras de ligação e correção da falta de portas de rede.
- Teste de RAM sem reiniciar e estabilizador de RAM.
- Assistente IA: Claude no browser, Ollama local ou API do Claude.

## 0.1.0
- Diagnóstico, perfis de otimização reversíveis, arranque, monitor de memory leaks, rede.
