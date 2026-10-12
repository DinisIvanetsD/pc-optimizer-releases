namespace PCOptimizer.Core.Util;

/// <summary>Lançada em modo simulação no lugar de mudar o PC; a mensagem diz o que teria sido feito.</summary>
public sealed class SimulatedException(string action)
    : Exception(Loc.T($"🧪 Simulação: nada foi alterado. A app ia {action}."))
{
    public string Action { get; } = action;
}

/// <summary>
/// Modo simulação: todas as ações que mudam o PC passam por <see cref="Guard"/>.
/// Ligado, a app mostra o que faria em vez de o fazer — útil para experimentar sem riscos.
/// </summary>
// Só para os testes automáticos: confirmam que cada ação que muda o PC passa por Guard.
// A app já não tem modo simulação (Enabled fica sempre false).
public static class Simulation
{
    public static bool Enabled { get; set; }

    /// <summary>Chamar no início de qualquer ação que muda o PC. Em simulação, lança <see cref="SimulatedException"/>.</summary>
    public static void Guard(string action)
    {
        if (Enabled) throw new SimulatedException(action);
    }
}
