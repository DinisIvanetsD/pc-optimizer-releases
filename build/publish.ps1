# Gera a pasta da app e o ZIP que o atualizador descarrega das Releases do GitHub.
#   .\build\publish.ps1                 -> publish\PCOptimizer-win-x64.zip
#   .\build\publish.ps1 -Release        -> e cria a Release no GitHub (precisa do gh com login)
param([switch]$Release)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'publish'
# a pasta tem o nome que o utilizador vê quando extrai o ZIP
$app = Join-Path $out 'PC Optimizer'

dotnet test (Join-Path $root 'tests\PCOptimizer.Tests') -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Os testes falharam: não publico.' }

if (Test-Path $app) { Remove-Item $app -Recurse -Force }
# com o .NET incluído: abre em qualquer Windows 10/11 de 64 bits sem instalar nada
dotnet publish (Join-Path $root 'src\PCOptimizer.App') -c Release -r win-x64 --self-contained true -o $app --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'O publish falhou.' }
Get-ChildItem $app -Recurse -File -Exclude *.pdb | ForEach-Object { $_.FullName.Substring($app.Length + 1) } | Set-Content (Join-Path $app 'files.txt') -Encoding utf8
Get-ChildItem $app -Recurse -Filter *.pdb | Remove-Item

$zip = Join-Path $out 'PCOptimizer-win-x64.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $app -DestinationPath $zip  # a pasta inteira: o ZIP extrai para "PC Optimizer\"
$version = (Get-Item (Join-Path $app 'PCOptimizer.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
Write-Host "Versão $version -> $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"

if ($Release) {
    # as versões vão para um repositório público à parte; o código fica no privado
    $notes = Join-Path $out 'notas.md'
    $log = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw -Encoding UTF8
    $m = [regex]::Match($log, "(?ms)^## $([regex]::Escape($version))\b[^\r\n]*\r?\n(.*?)(?=^## |\z)")
    [IO.File]::WriteAllText($notes, $(if ($m.Success) { $m.Groups[1].Value.Trim() } else { "Rigwise $version" })) # sem BOM
    gh release create "v$version" $zip --repo DinisIvanetsD/rigwise --title "Rigwise $version" --notes-file $notes
    if ($LASTEXITCODE -ne 0) { throw 'Não foi possível criar a Release.' }
}
