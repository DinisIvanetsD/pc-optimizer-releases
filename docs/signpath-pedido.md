# Pedido à SignPath Foundation

Respostas prontas para copiar para o formulário em https://signpath.org/apply (em inglês, que é o que a SignPath lê).

| Campo | Resposta |
|---|---|
| Project name | Rigwise |
| Repository | https://github.com/DinisIvanetsD/rigwise |
| Homepage / download page | https://rigwise-vercel.vercel.app |
| Code signing policy | https://rigwise-vercel.vercel.app/codesigning.html |
| License | MIT (OSI approved), no proprietary components |
| Maintainer | DinisIvanetsD (one-person project, 2FA enabled on GitHub) |
| What is signed | `PCOptimizer.exe` and the app's own DLLs in the release ZIP `PCOptimizer-win-x64.zip` |
| Build | GitHub Actions, `.github/workflows/release.yml`, runs on every `v*` tag on the public repository: `dotnet test`, then `dotnet publish -c Release -r win-x64 --self-contained` |

**Short description:**
Rigwise is a free, open-source Windows 10/11 app that diagnoses why a PC is slow, crashes or lags (blue screens,
unstable RAM, old drivers, full disks, network lag, game FPS) and explains it in plain words. Every change it makes
can be undone from its History tab. It has no telemetry.

**Why signing matters:**
Users download the ZIP from GitHub Releases and Windows SmartScreen shows "Windows protected your PC", which stops
most people from running it.

## Depois de aprovado

1. No GitHub, em `rigwise` › Settings › Secrets and variables › Actions:
   - segredo `SIGNPATH_API_TOKEN` (da conta SignPath);
   - variáveis `SIGNPATH_ORGANIZATION_ID` (da SignPath) e `SIGNPATH_ENABLED` = `true`.
2. Na SignPath, o projeto deve ter o slug `rigwise` e a política de assinatura `release-signing`, os nomes que o
   `release.yml` usa.
3. A versão seguinte sai assinada. Se a assinatura falhar, o workflow para e não publica uma versão sem assinatura.

Nota: o PresentMon incluído (`tools\PresentMon-2.6.0-x64.exe`) já vem assinado pela Intel; a SignPath só assina os
ficheiros da Rigwise.
