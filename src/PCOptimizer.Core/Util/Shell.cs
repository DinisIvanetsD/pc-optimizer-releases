using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PCOptimizer.Core.Util;

public sealed record ShellResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Executa programas e scripts PowerShell sem janela.</summary>
public static class Shell
{
    public static async Task<ShellResult> RunAsync(string file, string args, int timeoutMs = 60_000, Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (encoding != null)
        {
            psi.StandardOutputEncoding = encoding;
            psi.StandardErrorEncoding = encoding;
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException(Loc.T($"Não foi possível iniciar {file}"));
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return new ShellResult(-1, await outTask, Loc.T("Tempo limite excedido."));
        }
        return new ShellResult(p.ExitCode, await outTask, await errTask);
    }

    public static Task<ShellResult> PowerShellAsync(string script, int timeoutMs = 120_000)
    {
        var full = "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return RunAsync("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", timeoutMs, Encoding.UTF8);
    }

    /// <summary>Coloca uma string entre aspas simples para PowerShell.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Extrai uma mensagem legível do stderr do PowerShell (que pode vir em CLIXML).</summary>
    public static string CleanError(ShellResult r)
    {
        var err = r.StdErr;
        if (err.Contains("#< CLIXML"))
        {
            var parts = Regex.Matches(err, "<S S=\"Error\">(.*?)</S>").Select(m => m.Groups[1].Value);
            err = string.Concat(parts).Replace("_x000D_", "").Replace("_x000A_", "\n");
        }
        err = err.Trim();
        if (err.Length == 0) err = r.StdOut.Trim();
        if (err.Length == 0) err = Loc.T($"Falhou (código {r.ExitCode}).");
        var firstLine = err.Split('\n')[0].Trim();
        return firstLine.Length > 300 ? firstLine[..300] + "…" : firstLine;
    }

    /// <summary>Abre um programa/documento/URL. Devolve false se o utilizador cancelou o pedido de administrador.</summary>
    public static bool Open(string target, string args = "", bool elevated = false)
    {
        var psi = new ProcessStartInfo(target, args) { UseShellExecute = true };
        if (elevated) psi.Verb = "runas";
        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
