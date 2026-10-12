using PCOptimizer.Core.Diagnostics;
using PCOptimizer.Core.Settings;
using Xunit;

namespace PCOptimizer.Tests;

/// <summary>"Contar só a partir de hoje": muda só a janela de tempo, nunca grava nada (não chama Save).</summary>
public class StabilitySinceTests
{
    [Fact]
    public void Start_date_moves_the_window_but_never_beyond_30_days()
    {
        var s = AppSettings.Current;
        var saved = s.StabilitySince;
        try
        {
            s.StabilitySince = null;
            Assert.InRange((DateTime.Now - EventLogCheck.Since).TotalDays, 29.9, 30.1);

            var today = DateTime.Now.AddHours(-1);
            s.StabilitySince = today;
            Assert.Equal(today, EventLogCheck.Since);
            Assert.Contains(today.ToString("dd/MM"), EventLogCheck.PeriodText);

            s.StabilitySince = DateTime.Now.AddDays(-90); // mais antigo que 30 dias: ficam os 30 dias
            Assert.InRange((DateTime.Now - EventLogCheck.Since).TotalDays, 29.9, 30.1);
        }
        finally { s.StabilitySince = saved; }
    }
}
