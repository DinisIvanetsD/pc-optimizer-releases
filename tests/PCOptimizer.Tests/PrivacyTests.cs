using Microsoft.Win32;
using PCOptimizer.Core.Optimization;
using PCOptimizer.Core.Util;
using Xunit;

namespace PCOptimizer.Tests;

/// <summary>
/// Perfil Privacidade: os ajustes de registo aplicam-se e revertem-se sem deixar rasto.
/// O teste a sério usa uma chave própria em HKCU, nunca as chaves do Windows.
/// </summary>
public class PrivacyTests : IDisposable
{
    readonly string _sub = @"Software\PCOptimizerTests\" + Guid.NewGuid().ToString("N")[..8];
    string Key => @"HKCU\" + _sub;

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_sub, throwOnMissingSubKey: false);

    RegistryTweak Tweak() => new()
    {
        Id = "test.priv", Name = "t", Description = "t", RequiresAdmin = false,
        Values = new[]
        {
            new RegValue(Key, "Novo", 0, RegistryValueKind.DWord),     // não existia antes
            new RegValue(Key, "Existente", 0, RegistryValueKind.DWord), // existia com outro valor
        },
    };

    [Fact]
    public async Task Apply_then_revert_restores_the_original_registry()
    {
        Reg.Set(Key, "Existente", 1, RegistryValueKind.DWord);
        var t = Tweak();

        var state = await t.CaptureAsync();
        Assert.False(await t.IsAppliedAsync());
        await t.ApplyAsync();
        Assert.True(await t.IsAppliedAsync());
        Assert.Equal(0, Reg.Get(Key, "Novo"));

        await t.RevertAsync(state);
        Assert.Null(Reg.Get(Key, "Novo"));          // apagado, como estava
        Assert.Equal(1, Reg.Get(Key, "Existente")); // valor antigo de volta
        Assert.Equal(RegistryValueKind.DWord, Reg.GetKind(Key, "Existente"));
    }

    public static TheoryData<string> PrivacyTweakIds()
    {
        var d = new TheoryData<string>();
        foreach (var id in TweakCatalog.Profiles.Single(p => p.Id == "privacy").TweakIds) d.Add(id);
        return d;
    }

    [Theory]
    [MemberData(nameof(PrivacyTweakIds))]
    public void Privacy_tweaks_are_reversible_registry_values_and_ask_for_admin_when_needed(string id)
    {
        var t = Assert.IsType<RegistryTweak>(TweakCatalog.Get(id));
        Assert.NotEmpty(t.Values);
        Assert.All(t.Values, v => Assert.Equal(RegistryValueKind.DWord, v.Kind));
        // sem administrador não se escreve em HKLM nem nas políticas: o ajuste tem de pedir permissão
        if (t.Values.Any(v => v.Key.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) || v.Key.Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase)))
            Assert.True(t.RequiresAdmin, $"{id} escreve em chaves protegidas mas não pede administrador");
    }
}
