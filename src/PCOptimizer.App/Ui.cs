using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

public static class Ui
{
    /// <summary>Texto de erro para mostrar; em modo simulação mostra só o que teria sido feito.</summary>
    public static string Error(Exception ex, string? prefix = null) =>
        ex is SimulatedException ? ex.Message : (prefix ?? Loc.T("Erro: ")) + ex.Message;
}
