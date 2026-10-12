namespace PCOptimizer.Core;

/// <summary>Gravidade de um problema. A ordem numérica é a ordem de apresentação.</summary>
public enum Severity { Critical = 0, Warning = 1, Info = 2, Ok = 3 }

/// <summary>Uma ação que o utilizador pode executar para resolver um problema.</summary>
public sealed class FixAction
{
    public FixAction(string label, Func<Task<string>> run)
    {
        Label = label;
        Run = run;
    }

    public string Label { get; }
    public Func<Task<string>> Run { get; }

    /// <summary>Se definido, a interface pede confirmação com este texto antes de executar.</summary>
    public string? ConfirmText { get; init; }

    public bool RequiresAdmin { get; init; }
}

/// <summary>Um problema (ou confirmação de que está tudo bem) encontrado pelo diagnóstico.</summary>
public sealed class Finding
{
    public required string Category { get; init; }
    public required Severity Severity { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public List<FixAction> Actions { get; init; } = new();

    /// <summary>A app já avisa este problema por outro caminho: a manutenção não repete a notificação.</summary>
    public bool NotifiedElsewhere { get; init; }
}

/// <summary>Ganchos para o Core pedir coisas à interface (ex.: mudar de aba).</summary>
public static class UiHooks
{
    public static Action<string>? Navigate { get; set; }
    /// <summary>Reabre a app como administrador, já no Diagnóstico.</summary>
    public static Action? RestartAsAdmin { get; set; }
}
