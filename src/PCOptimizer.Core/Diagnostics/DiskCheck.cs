using System.Text.Json;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Diagnostics;

/// <summary>Espaço livre, saúde SMART/desgaste dos discos e se o Windows está num HDD.</summary>
public sealed class DiskCheck : IDiagnosticCheck
{
    const string Cat = "Disco";

    public string Name => "Discos";

    public async Task<IEnumerable<Finding>> RunAsync(CancellationToken ct)
    {
        var findings = new List<Finding>();
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

        // ignorar partições pequenas (recuperação, partições de fabricantes)
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.TotalSize >= 4L << 30))
        {
            var isSystem = d.Name.Equals(systemDrive, StringComparison.OrdinalIgnoreCase);
            var freePct = 100.0 * d.AvailableFreeSpace / d.TotalSize;
            var freeGb = d.AvailableFreeSpace / 1073741824.0;
            Severity? sev = null;
            if (isSystem && (freePct < 10 || freeGb < 10)) sev = Severity.Critical;
            else if (freePct < 10 || (isSystem && freePct < 15)) sev = Severity.Warning;
            if (sev is null) continue;

            var f = new Finding
            {
                Category = Cat, Severity = sev.Value,
                Title = Loc.T($"Pouco espaço no disco {d.Name.TrimEnd('\\')} ({freeGb:N1} GB livres, {freePct:N0}%)"),
                Description = isSystem
                    ? Loc.T("Com o disco do Windows quase cheio, as atualizações falham, o ficheiro de paginação não cresce e tudo abranda. Tenta manter pelo menos 15% livre.")
                    : Loc.T("Este disco está quase cheio. Discos muito cheios ficam mais lentos, sobretudo os HDD."),
            };
            if (isSystem)
            {
                f.Actions.Add(new FixAction(Loc.T("Limpar ficheiros temporários"), Tools.CleanTemp));
                f.Actions.Add(new FixAction(Loc.T("Limpeza de Disco do Windows"), Tools.DiskCleanup));
            }
            f.Actions.Add(new FixAction(Loc.T("Ver o que ocupa espaço"), Tools.StorageSettings));
            findings.Add(f);
        }

        findings.AddRange(await HealthAsync());

        if (!findings.Any(f => f.Severity is Severity.Critical or Severity.Warning))
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Ok,
                Title = Loc.T("Discos OK"),
                Description = Admin.IsElevated
                    ? Loc.T("Há espaço livre suficiente e os discos não reportam problemas de saúde.")
                    : Loc.T("Há espaço livre suficiente. (Para ler o desgaste e os erros dos discos, abre a app como administrador.)"),
            });
        }
        return findings;
    }

    static async Task<List<Finding>> HealthAsync()
    {
        var findings = new List<Finding>();
        const string script = """
            $sys = $null
            try { $sys = [string](Get-Partition -DriveLetter $env:SystemDrive[0] -ErrorAction Stop | Get-Disk).Number } catch {}
            $list = foreach ($d in Get-PhysicalDisk) {
              $r = $null
              try { $r = $d | Get-StorageReliabilityCounter -ErrorAction Stop } catch {}
              [pscustomobject]@{
                Name = $d.FriendlyName; Media = "$($d.MediaType)"; Health = "$($d.HealthStatus)"
                IsSystem = ([string]$d.DeviceId -eq $sys)
                Wear = $r.Wear; Temp = $r.Temperature; ReadErr = $r.ReadErrorsUncorrected
              }
            }
            ConvertTo-Json -InputObject @($list) -Compress
            """;
        var r = await Shell.PowerShellAsync(script, 60_000);
        if (!r.Ok || string.IsNullOrWhiteSpace(r.StdOut)) return findings;

        using var doc = JsonDocument.Parse(r.StdOut.Trim());
        foreach (var d in doc.RootElement.EnumerateArray())
        {
            var name = Str(d, "Name") ?? Loc.T("Disco");
            var health = Str(d, "Health") ?? "";
            var media = Str(d, "Media") ?? "";
            var isSystem = d.TryGetProperty("IsSystem", out var s) && s.ValueKind == JsonValueKind.True;

            if (health is "Unhealthy" or "2")
            {
                findings.Add(new Finding
                {
                    Category = Cat, Severity = Severity.Critical,
                    Title = Loc.T($"O disco \"{name}\" está a falhar"),
                    Description = Loc.T("O próprio disco reporta que não está saudável. Faz JÁ uma cópia de segurança dos teus ficheiros e planeia substituí-lo."),
                });
            }
            else if (health is "Warning" or "1")
            {
                findings.Add(new Finding
                {
                    Category = Cat, Severity = Severity.Warning,
                    Title = Loc.T($"O disco \"{name}\" reporta avisos de saúde"),
                    Description = Loc.T("O disco detetou problemas. Faz uma cópia de segurança dos ficheiros importantes."),
                });
            }

            if (Num(d, "ReadErr") is > 0 and var errs)
            {
                findings.Add(new Finding
                {
                    Category = Cat, Severity = Severity.Critical,
                    Title = Loc.T($"O disco \"{name}\" tem {errs:N0} erros de leitura"),
                    Description = Loc.T("Há dados que o disco não conseguiu ler. Isto costuma ser sinal de que vai avariar. Faz uma cópia de segurança."),
                });
            }

            if (Num(d, "Wear") is >= 80 and var wear)
            {
                findings.Add(new Finding
                {
                    Category = Cat, Severity = Severity.Warning,
                    Title = Loc.T($"O SSD \"{name}\" está {wear:N0}% gasto"),
                    Description = Loc.T("Os SSD têm um número limitado de escritas. Este está perto do fim da vida útil. Planeia substituí-lo."),
                });
            }

            if (Num(d, "Temp") is >= 60 and var temp)
            {
                findings.Add(new Finding
                {
                    Category = Cat, Severity = Severity.Warning,
                    Title = Loc.T($"O disco \"{name}\" está quente ({temp:N0} °C)"),
                    Description = Loc.T("Acima de ~60 °C os SSD reduzem a velocidade para se protegerem. Verifica a ventilação ou usa um dissipador no NVMe."),
                });
            }

            if (isSystem && media is "HDD" or "3")
            {
                findings.Add(new Finding
                {
                    Category = Cat, Severity = Severity.Warning,
                    Title = Loc.T("O Windows está instalado num disco mecânico (HDD)"),
                    Description = Loc.T("Esta é a maior causa de um PC lento. Passar o Windows para um SSD torna o arranque e a abertura de apps 3 a 10 vezes mais rápidos."),
                });
            }
        }
        return findings;
    }

    static string? Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    static double? Num(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
