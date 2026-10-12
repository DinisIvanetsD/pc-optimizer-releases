using PCOptimizer.Core.Gaming;
using Xunit;

namespace PCOptimizer.Tests;

[Collection("loc")]
public class GameCrashTests
{
    static GameCrash C(string module, uint code = 0xC0000005) => new(DateTime.Now, "cs2.exe", module, code, "Steam");

    [Fact]
    public void Crashes_in_many_different_places_point_to_memory()
    {
        var crashes = new[] { C("rendersystemdx11.dll"), C("meshsystem.dll"), C("nvgpucomp64.dll"), C("tier0.dll", 0x80000003), C("KERNELBASE.dll", 0x80000003) };
        Assert.Equal(CrashCause.Memory, GameCrashes.Verdict(crashes).Cause);
    }

    [Fact]
    public void Same_graphics_driver_module_points_to_the_driver()
    {
        var crashes = new[] { C("nvwgf2umx.dll"), C("nvwgf2umx.dll"), C("nvwgf2umx.dll"), C("client.dll") };
        Assert.Equal(CrashCause.GraphicsDriver, GameCrashes.Verdict(crashes).Cause);
    }

    [Fact]
    public void Same_game_module_points_to_the_game_files()
    {
        Assert.Equal(CrashCause.GameFiles, GameCrashes.Verdict(new[] { C("client.dll"), C("client.dll") }).Cause);
    }

    [Fact]
    public void Overlay_modules_are_recognised()
    {
        Assert.Equal(CrashCause.Overlay, GameCrashes.Cause("GameOverlayRenderer64.dll", 0xC0000005));
    }

    /// <summary>Um minidump mínimo: cabeçalho, 2 streams (exceção e módulos) e o nome do módulo.</summary>
    [Fact]
    public void ReadDump_finds_the_code_and_the_faulting_module()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pcopt-test-{Guid.NewGuid():N}.dmp");
        using (var w = new BinaryWriter(File.Create(path)))
        {
            const uint dir = 32, ex = 64, mods = 128, name = 256;
            w.Write(0x504D444Du); w.Write(0u); w.Write(2u); w.Write(dir);       // cabeçalho
            w.BaseStream.Position = dir;
            w.Write(6u); w.Write(168u); w.Write(ex);                             // ExceptionStream
            w.Write(4u); w.Write(112u); w.Write(mods);                           // ModuleListStream
            w.BaseStream.Position = ex;
            w.Write(1u); w.Write(0u); w.Write(0xC0000005u); w.Write(0u); w.Write(0ul); w.Write(0x7FF0_0000_1234ul);
            w.BaseStream.Position = mods;
            w.Write(1u); w.Write(0x7FF0_0000_0000ul); w.Write(0x10000u); w.Write(0u); w.Write(0u); w.Write(name);
            w.BaseStream.Position = name;
            var bytes = System.Text.Encoding.Unicode.GetBytes(@"C:\Games\cs2\client.dll");
            w.Write(bytes.Length); w.Write(bytes);
        }
        try
        {
            var r = GameCrashes.ReadDump(path);
            Assert.NotNull(r);
            Assert.Equal(0xC0000005u, r!.Value.Code);
            Assert.Equal("client.dll", r.Value.Module);
        }
        finally { File.Delete(path); }
    }
}
