# PC Optimizer (Windows)

App para diagnosticar, otimizar e vigiar um PC com Windows 10/11. Feita em C# / .NET 8 / WPF.

## Como correr

```bash
dotnet run --project src/PCOptimizer.App
```

A app abre sem permissões de administrador. Quando uma ação precisa delas, oferece **"Abrir como administrador"**.
Em **Definições › Instalar no PC** instala-se para o utilizador (Menu Iniciar, Definições › Aplicações), sem administrador.

## As abas

| Aba | O que faz |
|---|---|
| 🏠 **Início** | Saúde do PC com o porquê de cada ponto, gráfico da evolução, "o que fazer agora" e a tabela **antes/depois** (arranque, RAM, programas no arranque, ping, espaço). |
| 🔍 **Diagnóstico** | RAM, CPU, discos (SMART), **ecrãs azuis com análise dos minidumps** (o driver que estava a correr, ou se aponta para a RAM), desligares súbitos, erros WHEA, apps que crasham, drivers, quebras de rede por falta de portas. |
| ⚡ **Otimização** | Perfis Gaming, Rede, Vídeo, Música, Desktop. Cada ajuste guarda o estado original e pode ser revertido. |
| 🔧 **Drivers** | Dispositivos sem driver ou com erro, drivers do Windows Update, **descarregar e instalar** versões novas das fontes oficiais (NVIDIA, AMD, Catálogo do Microsoft Update) com verificação da assinatura, limpeza de versões antigas. |
| 💾 **Espaço em disco** | Programas e jogos (úteis/inúteis, usados/não usados), o que ocupa espaço com o conselho "é seguro apagar?", limpeza rápida. Confirmações por nível de risco. |
| 🚀 **Arranque** · 📊 **Monitor** · 🧠 **Teste de RAM** · 🌐 **Rede** | Programas de arranque, memory leaks, teste de RAM sem reiniciar + estabilizador, diagnóstico de lag e quebras de ligação. |
| 🕘 **Histórico** | Tudo o que a app mudou, com **Desfazer** (ajustes, arranque, DNS, QoS, pastas da Reciclagem; drivers pelo ponto de restauro). |
| ⚙ **Definições** | Arrancar com o Windows, manutenção automática, **modo simulação**, atualizações, instalação. |

### Modo simulação

Em Definições › Modo simulação, todas as ações que mudam o PC passam por `Simulation.Guard` e só dizem o que fariam.
Serve para experimentar a app sem riscos (e não pede administrador).

### Manutenção automática

De N em N dias (com a app aberta ou na bandeja) procura problemas graves, drivers novos e discos cheios, e avisa por notificação.
Só lê: nunca muda nada sozinha. Só avisa coisas novas.

## Estrutura

```
src/
  PCOptimizer.Core/   lógica (sem interface)
    Ai/               assistente (Claude, Ollama local, Claude no browser)
    Benchmark/        medições antes/depois e histórico da saúde
    Diagnostics/      verificações, ferramentas, leitura de minidumps (CrashDumps)
    Drivers/          DriverManager (análise), DriverDownloads (fontes oficiais)
    History/          ChangeLog (histórico + desfazer), RecycleBin (recuperar da Reciclagem)
    Maintenance/      verificação periódica
    Memory/           teste de RAM, estabilizador, isolamento de páginas
    Monitoring/       deteção de memory leaks
    Network/          ping, diagnóstico, DNS, QoS, vigia de quebras de ligação
    Optimization/     ajustes reversíveis e perfis
    Settings/         definições, arrancar com o Windows
    Storage/          discos, programas, pastas, limpeza
    Updates/          atualizações (GitHub Releases) e instalador por utilizador
  PCOptimizer.App/    interface WPF
  PCOptimizer.Cli/    linha de comandos para testar o Core (só leituras)
tests/
  PCOptimizer.Tests/  xUnit: regras de segurança, minidumps, Reciclagem, simulação, drivers
build/
  publish.ps1         testes + ZIP para as Releases (-Release cria a Release no GitHub)
```

Os dados ficam em `%LOCALAPPDATA%\PCOptimizer` (histórico, definições, backups dos ajustes, medições).

## Testes

```bash
dotnet test tests/PCOptimizer.Tests
```

Os testes da Reciclagem enviam ficheiros de teste para a Reciclagem e confirmam que o "Desfazer" os devolve.

## Linha de comandos (só leituras)

```bash
dotnet run --project src/PCOptimizer.Cli -- diag
```

Outros comandos: `tweaks`, `startup`, `net [host]`, `dns`, `bloat`, `leak`, `monitor 30`, `drivers`, `driver-offers`, `crashes [pasta]`, `metrics`, `disk`.

## Desenvolvimento

- Capturas de ecrã: `PCOptimizer.exe --screenshots <pasta> [--only <NomeDaAba>]`.
- Para testar sem mexer nos dados reais: `PCOPT_HISTORY_FILE`, `PCOPT_SETTINGS_FILE`, `PCOPT_METRICS_FILE`.
- Versões: subir `<Version>` em `PCOptimizer.App.csproj`, escrever as novidades no `CHANGELOG.md` e enviar a tag
  (`git tag v0.9.4` e `git push origin v0.9.4`): o GitHub Actions testa, faz o ZIP e cria a Release.
  Sem a tag, `build\publish.ps1 -Release` faz o mesmo no PC (precisa do gh com login).
  O atualizador só vê as Releases se o repositório for público.
