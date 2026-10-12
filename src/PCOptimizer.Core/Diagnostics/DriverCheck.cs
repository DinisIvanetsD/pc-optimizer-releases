using System.Management;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Diagnostics;

/// <summary>Dispositivos com erro no Gestor de Dispositivos e o estado do driver da gráfica.</summary>
public sealed class DriverCheck : IDiagnosticCheck
{
    const string Cat = "Drivers";

    public string Name => "Drivers";

    public Task<IEnumerable<Finding>> RunAsync(CancellationToken ct)
    {
        var findings = new List<Finding>();

        var broken = new List<string>();
        using (var s = new ManagementObjectSearcher("SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
        {
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    var code = Convert.ToUInt32(o["ConfigManagerErrorCode"]);
                    if (code is 22 or 45) continue; // 22 = desativado pelo utilizador, 45 = não está ligado
                    var reason = code switch
                    {
                        28 => Loc.T("sem driver instalado"),
                        10 => Loc.T("não consegue arrancar"),
                        43 => Loc.T("o Windows parou-o porque reportou problemas"),
                        31 => Loc.T("o driver não carrega"),
                        _ => Loc.T($"erro {code}"),
                    };
                    broken.Add($"• {o["Name"] ?? Loc.T("Dispositivo desconhecido")} — {reason}");
                }
            }
        }
        if (broken.Count > 0)
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Warning,
                Title = Loc.T($"{broken.Count} dispositivo(s) com problemas de driver"),
                Description = string.Join("\n", broken.Take(8)) +
                              Loc.T("\n\nNa aba Drivers podes reinstalar o driver ou instalar o que falta pelo Windows Update."),
                Actions =
                {
                    new FixAction(Loc.T("Abrir aba Drivers"), () => { UiHooks.Navigate?.Invoke("drivers"); return Task.FromResult(""); }),
                    new FixAction(Loc.T("Gestor de Dispositivos"), Tools.DeviceManager),
                    new FixAction("Windows Update", Tools.WindowsUpdate),
                },
            });
        }

        using (var s = new ManagementObjectSearcher("SELECT Name, DriverDate FROM Win32_VideoController"))
        {
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    var name = o["Name"]?.ToString() ?? "";
                    if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(new Finding
                        {
                            Category = Cat, Severity = Severity.Critical,
                            Title = Loc.T("A placa gráfica não tem driver"),
                            Description = Loc.T("O Windows está a usar o driver básico da Microsoft: sem aceleração 3D, jogos e vídeo ficam muito lentos. ") +
                                          Loc.T("Instala o driver da NVIDIA, AMD ou Intel."),
                        });
                        continue;
                    }
                    if (o["DriverDate"] is not string raw) continue;
                    var date = ManagementDateTimeConverter.ToDateTime(raw);
                    var months = (int)((DateTime.Now - date).TotalDays / 30.4);
                    var isGamingGpu = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                      name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                                      name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                                      name.Contains("Arc", StringComparison.OrdinalIgnoreCase);
                    if (isGamingGpu && months >= 12)
                    {
                        findings.Add(new Finding
                        {
                            Category = Cat, Severity = Severity.Info,
                            Title = Loc.T($"O driver da gráfica tem {months} meses"),
                            Description = Loc.T($"{name}: drivers recentes trazem mais desempenho em jogos novos e corrigem crashes. ") +
                                          Loc.T("Atualiza pela NVIDIA App, AMD Adrenalin ou Intel Arc Control."),
                        });
                    }
                }
            }
        }

        if (findings.Count == 0)
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Ok,
                Title = Loc.T("Drivers OK"),
                Description = Loc.T("Nenhum dispositivo com erro, e o driver da gráfica está instalado."),
            });
        }
        return Task.FromResult<IEnumerable<Finding>>(findings);
    }
}
