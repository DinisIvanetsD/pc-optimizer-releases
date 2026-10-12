using PCOptimizer.Core.Util;
namespace PCOptimizer.Core.Diagnostics;

public interface IDiagnosticCheck
{
    string Name { get; }
    Task<IEnumerable<Finding>> RunAsync(CancellationToken ct);
}

public static class DiagnosticsEngine
{
    public static IReadOnlyList<IDiagnosticCheck> Checks { get; } = new IDiagnosticCheck[]
    {
        new ResourceCheck(),
        new DiskCheck(),
        new EventLogCheck(),
        new DriverCheck(),
        new SystemCheck(),
        new GameCheck(),
    };

    public static async Task<List<Finding>> RunAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var all = new List<Finding>();
        foreach (var check in Checks)
        {
            progress?.Report($"A verificar: {check.Name}…");
            try
            {
                all.AddRange(await Task.Run(() => check.RunAsync(ct), ct));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                all.Add(new Finding
                {
                    Category = check.Name,
                    Severity = Severity.Info,
                    Title = Loc.T($"Não foi possível verificar: {check.Name}"),
                    Description = ex.Message,
                });
            }
        }
        progress?.Report(Loc.T("Análise concluída."));
        return all.OrderBy(f => f.Severity).ToList();
    }
}
