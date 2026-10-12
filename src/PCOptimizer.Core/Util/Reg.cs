using Microsoft.Win32;

namespace PCOptimizer.Core.Util;

/// <summary>Acesso ao registo com caminhos do tipo "HKLM\SOFTWARE\..." (sempre vista 64-bit).</summary>
public static class Reg
{
    public static RegistryKey OpenRoot(string hive) => hive.ToUpperInvariant() switch
    {
        "HKLM" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        "HKCU" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        _ => throw new ArgumentException($"Hive desconhecida: {hive}"),
    };

    public static (string Hive, string SubKey) Split(string key)
    {
        var i = key.IndexOf('\\');
        return (key[..i], key[(i + 1)..]);
    }

    public static object? Get(string key, string name)
    {
        var (hive, sub) = Split(key);
        using var root = OpenRoot(hive);
        using var k = root.OpenSubKey(sub);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public static RegistryValueKind? GetKind(string key, string name)
    {
        var (hive, sub) = Split(key);
        using var root = OpenRoot(hive);
        using var k = root.OpenSubKey(sub);
        if (k is null || k.GetValue(name) is null) return null;
        return k.GetValueKind(name);
    }

    public static void Set(string key, string name, object value, RegistryValueKind kind)
    {
        var (hive, sub) = Split(key);
        using var root = OpenRoot(hive);
        using var k = root.CreateSubKey(sub, true);
        k.SetValue(name, value, kind);
    }

    public static void Delete(string key, string name)
    {
        var (hive, sub) = Split(key);
        using var root = OpenRoot(hive);
        using var k = root.OpenSubKey(sub, true);
        k?.DeleteValue(name, false);
    }

    public static bool KeyExists(string key)
    {
        var (hive, sub) = Split(key);
        using var root = OpenRoot(hive);
        using var k = root.OpenSubKey(sub);
        return k != null;
    }
}
