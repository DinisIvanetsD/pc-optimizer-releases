# Plano 0.6.0

Por esta ordem. Cada passo: código, testes, capturas (PT e EN), e só depois o seguinte.

## 1. Aba Apps (instalar apps com o winget)
- Core `Apps/AppCatalog.cs`: ~60 apps populares por categoria, cada uma com o ID do winget.
- Core `Apps/Winget.cs`:
  - `ListInstalledAsync` lê `winget list` e devolve os IDs instalados.
  - `InstallAsync` corre `winget install -e --id <id> --silent --accept-*-agreements`.
  - `UpgradeAllAsync` corre `winget upgrade --all`.
  - Se o winget não existir, explica como o obter (App Installer da Microsoft Store).
- App: nova aba "Apps" com uma lista de caixas por categoria, marcadas "Instalada" quando já existem.
  Os botões são "Instalar selecionados" (com progresso app a app) e "Atualizar tudo".
- Histórico: cada instalação fica registada (desfazer = desinstalar pelo winget).
- Testes: o leitor da saída do `winget list` e o catálogo (IDs únicos, categorias).
- Não instalar nada no PC do utilizador sem pedir; o teste real usa só `winget list`.

## 2. Privacidade (perfil novo na Otimização)
- Ajustes no registo, reversíveis como os outros:
  - telemetria no mínimo;
  - ID de publicidade desligado;
  - histórico de atividade desligado;
  - Copilot desligado;
  - dicas e sugestões desligadas;
  - pesquisa do Bing desligada.
- Perfil "Privacidade" que os agrupa.

## 3. Remover apps pré-instaladas do Windows
- Lista das apps da Microsoft que se podem tirar sem partir o Windows
  (Xbox, Clipchamp, Notícias, Meteorologia, Solitaire, Teams pessoal, Dicas, Feedback Hub…).
- Só aparecem as que estão instaladas. Remover pede confirmação e fica no Histórico.
  Para voltar a ter uma, instala-se da Microsoft Store.
- Fica no Disco › Programas e jogos como filtro "Apps do Windows", reutilizando a desinstalação que já existe.

## 4. Bloqueador de anúncios (ficheiro hosts)
- Na aba Rede: "Bloquear anúncios e rastreadores".
  Ao ligar, descarrega a lista StevenBlack (unified hosts) e junta-a ao hosts entre marcas próprias.
  O hosts original fica guardado e desligar repõe-no.
- Precisa de administrador. Fica no Histórico com Desfazer.
- Testes: juntar e retirar o bloco entre as marcas sem estragar o resto do ficheiro.

## Fim
- Traduções EN, CHANGELOG, versão 0.6.0, capturas, publicar a Release.
