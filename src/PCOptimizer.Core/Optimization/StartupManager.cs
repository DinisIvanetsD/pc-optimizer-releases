using System.Diagnostics;
using Microsoft.Win32;
using PCOptimizer.Core.Util;
using PCOptimizer.Core.History;

namespace PCOptimizer.Core.Optimization;

public sealed class StartupItem
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required string Location { get; init; }
    public string? Description { get; init; }
    public bool RequiresAdmin { get; init; }
    public bool Enabled { get; internal set; }
    internal string ApprovedKey { get; init; } = "";
    internal string ValueName { get; init; } = "";
}

/// <summary>
/// Programas que arrancam com o Windows. Ativa/desativa da mesma forma que o Gestor de Tarefas
/// (chaves StartupApproved), por isso nada é apagado e tudo é reversível.
/// </summary>
public static class StartupManager
{
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupItem> List()
    {
        var items = new List<StartupItem>();
        AddRegistry(items, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", @"HKCU\" + Approved + "Run", Loc.T("Registo (este utilizador)"), false);
        AddRegistry(items, @"HKLM\Software\Microsoft\Windows\CurrentVersion\Run", @"HKLM\" + Approved + "Run", Loc.T("Registo (todos os utilizadores)"), true);
        AddRegistry(items, @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", @"HKLM\" + Approved + "Run32", Loc.T("Registo 32-bit (todos os utilizadores)"), true);
        AddFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), @"HKCU\" + Approved + "StartupFolder", Loc.T("Pasta Arranque (este utilizador)"), false);
        AddFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), @"HKLM\" + Approved + "StartupFolder", Loc.T("Pasta Arranque (todos os utilizadores)"), true);
        return items.OrderByDescending(i => i.Enabled).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static void SetEnabled(StartupItem item, bool enabled, bool record = true)
    {
        Simulation.Guard(Loc.T($"{(enabled ? "ativar" : "desativar")} \"{item.Name}\" no arranque do Windows"));
        if (item.RequiresAdmin) Admin.Require();
        var before = item.Enabled;
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        Reg.Set(item.ApprovedKey, item.ValueName, data, RegistryValueKind.Binary);
        item.Enabled = enabled;
        if (record && before != enabled)
            ChangeLog.Record(ChangeKind.Startup, Loc.T($"{(enabled ? Loc.T("Ativado") : Loc.T("Desativado"))} no arranque: {item.Name}"), item.Location, UndoKind.Startup,
                new()
                {
                    ["name"] = item.Name, ["key"] = item.ApprovedKey, ["value"] = item.ValueName,
                    ["admin"] = item.RequiresAdmin ? "1" : "0", ["before"] = before ? "1" : "0",
                });
    }

    static void AddRegistry(List<StartupItem> items, string runKey, string approvedKey, string label, bool admin)
    {
        var (hive, sub) = Reg.Split(runKey);
        using var root = Reg.OpenRoot(hive);
        using var k = root.OpenSubKey(sub);
        if (k is null) return;
        foreach (var name in k.GetValueNames())
        {
            if (string.IsNullOrEmpty(name)) continue;
            var cmd = k.GetValue(name)?.ToString() ?? "";
            items.Add(new StartupItem
            {
                Name = name, Command = cmd, Location = label, RequiresAdmin = admin,
                ApprovedKey = approvedKey, ValueName = name,
                Enabled = IsEnabled(approvedKey, name), Description = Describe(cmd),
            });
        }
    }

    static void AddFolder(List<StartupItem> items, string folder, string approvedKey, string label, bool admin)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var file = Path.GetFileName(path);
            if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            items.Add(new StartupItem
            {
                Name = Path.GetFileNameWithoutExtension(path), Command = path, Location = label, RequiresAdmin = admin,
                ApprovedKey = approvedKey, ValueName = file,
                Enabled = IsEnabled(approvedKey, file), Description = Describe(path),
            });
        }
    }

    static bool IsEnabled(string approvedKey, string valueName) =>
        Reg.Get(approvedKey, valueName) is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;

    static string? ExePath(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        string path;
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            path = end > 0 ? cmd[1..end] : cmd.Trim('"');
        }
        else
        {
            var i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = i > 0 ? cmd[..(i + 4)] : cmd.Split(' ')[0];
        }
        return File.Exists(path) ? path : null;
    }

    static string? Describe(string cmd)
    {
        var path = ExePath(cmd);
        if (path is null || path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            var parts = new[] { v.FileDescription, v.CompanyName }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim());
            var text = string.Join(" · ", parts);
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }
}
