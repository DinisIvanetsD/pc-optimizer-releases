using System.Runtime.CompilerServices;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Tests;

static class TestSetup
{
    /// <summary>Os testes verificam os textos em português, seja qual for a língua do Windows (o GitHub Actions corre em inglês).</summary>
    [ModuleInitializer]
    internal static void Init() => Loc.Language = "pt";
}
