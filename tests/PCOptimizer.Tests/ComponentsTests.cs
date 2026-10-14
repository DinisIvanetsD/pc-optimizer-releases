using PCOptimizer.Core.Hardware;
using Xunit;

namespace PCOptimizer.Tests;

public class ComponentsTests
{
    [Theory]
    [InlineData("F4-3600C18-16GVK", 3600)]     // G.Skill DDR4
    [InlineData("F5-6000J3038F16G", 6000)]     // G.Skill DDR5
    [InlineData("CMK16GX4M2B3200C16", 3200)]   // Corsair Vengeance
    [InlineData("KF436C18BBK2/16", 3600)]      // Kingston Fury
    [InlineData("M378A1K43CB2-CTD", null)]     // Samsung JEDEC: sem XMP
    [InlineData("", null)]
    public void RatedSpeed_reads_common_part_numbers(string part, int? expected) =>
        Assert.Equal(expected, Components.RatedSpeed(part));

    [Fact]
    public void Warns_only_when_ram_runs_well_below_its_rating()
    {
        Assert.NotNull(Components.XmpWarning(2133, 3600)); // XMP desligado
        Assert.Null(Components.XmpWarning(3600, 3600));
        Assert.Null(Components.XmpWarning(3533, 3600));    // pequenas diferenças de arredondamento
        Assert.Null(Components.XmpWarning(0, 3600));       // não se sabe
    }

    [Fact]
    public async Task Lists_at_least_the_cpu_on_this_pc()
    {
        var list = await Components.ListAsync();
        Assert.Contains(list, c => c.Icon == "🧠" && c.Name.Length > 0);
    }
}

public class BatteryTests
{
    static BatteryInfo B(long designed, long full) => new("b", "", designed, full, 90, 0, true, false);

    [Fact]
    public void Health_is_full_capacity_over_design()
    {
        Assert.Equal(101, Battery.HealthPercent(84000, 84470)); // nova pode passar um pouco dos 100%
        Assert.Equal(75, Battery.HealthPercent(80000, 60000));
        Assert.Null(Battery.HealthPercent(0, 60000));            // o portátil não diz a de fábrica
    }

    [Fact]
    public void Warns_only_when_the_battery_is_worn()
    {
        Assert.Null(Battery.Warning(B(84000, 84470)));
        Assert.Null(Battery.Warning(B(80000, 68000)));  // 85%
        Assert.NotNull(Battery.Warning(B(80000, 60000))); // 75%
        Assert.NotNull(Battery.Warning(B(80000, 40000))); // 50%
        Assert.Null(Battery.Warning(B(0, 0)));
    }

    [Fact]
    public void Lists_batteries_without_failing() => Assert.NotNull(Battery.List()); // num PC de secretária a lista vem vazia
}

public class CpuThermalTests
{
    [Fact]
    public void Warns_on_heat_and_on_limits()
    {
        Assert.Null(CpuThermal.Warning(new(2.5, 100, 100, false, 60)));
        Assert.Contains("98", CpuThermal.Warning(new(1.2, 100, 70, true, 98))!);
        Assert.NotNull(CpuThermal.Warning(new(1.2, 100, 70, true, null)));
        Assert.NotNull(CpuThermal.Warning(new(1.0, 50, 100, false, null)));
    }

    [Fact]
    public void Reads_this_pc_without_failing()
    {
        var s = CpuThermal.Read(2.0);
        Assert.InRange(s.LimitPercent, 0, 100);
    }
}

public class SensorsTests
{
    [Fact]
    public void ParseNvidiaSmi_reads_values_and_missing_ones()
    {
        var list = Sensors.ParseNvidiaSmi("NVIDIA GeForce RTX 3060 Ti, 85, 99, 149.18, 58\r\nNVIDIA T400, 40, [N/A], [N/A], 0\r\n");
        Assert.Equal(new GpuSensor("NVIDIA GeForce RTX 3060 Ti", 85, 99, 149.18, 58), list[0]);
        Assert.Null(list[1].FanPercent);
        Assert.Null(list[1].PowerW);
    }

    [Fact]
    public void Warns_only_when_the_gpu_is_hot()
    {
        Assert.NotNull(Sensors.GpuWarning(new("g", 85, 99, null, 50)));
        Assert.Null(Sensors.GpuWarning(new("g", 70, 40, null, 50)));
        Assert.Null(Sensors.GpuWarning(new("g", null, null, null, null)));
    }
}
