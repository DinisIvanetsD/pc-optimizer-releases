using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Updates;

/// <param name="Sha256">o hash que o GitHub calcula para o ficheiro (null nas versões antigas sem ele)</param>
public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string ZipUrl, long Size, string PageUrl, string? Sha256 = null);

public enum UpdateState { UpToDate, Available, NoReleases, Unreachable }

public sealed record UpdateCheck(UpdateState State, ReleaseInfo? Release, string Message);

/// <summary>
/// Atualizações a partir das Releases do GitHub: compara a versão, descarrega o ZIP,
/// e substitui os ficheiros depois de a app fechar (com um pequeno script).
/// </summary>
public static class Updater
{
    /// <summary>Repositório público só com as versões publicadas (o código fica no repositório privado).</summary>
    public const string Repo = "DinisIvanetsD/rigwise";
    public const string AssetName = "PCOptimizer-win-x64.zip";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("PCOptimizer-Updater");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    public static Version Current =>
        typeof(Updater).Assembly.GetName().Version is Version v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>A versão da app é a do executável (PCOptimizer.exe), não a da biblioteca.</summary>
    public static Version AppVersion { get; set; } = Current;

    public static async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
            if (res.StatusCode == HttpStatusCode.NotFound)
                return new(UpdateState.NoReleases, null,
                    Loc.T("Ainda não há versões publicadas online."));
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v))
                return new(UpdateState.NoReleases, null, Loc.T($"Versão publicada com nome inválido: {tag}"));

            string? zip = null, sha = null;
            long size = 0;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                zip = a.GetProperty("browser_download_url").GetString();
                size = a.GetProperty("size").GetInt64();
                if (a.TryGetProperty("digest", out var dg) && dg.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = d[7..];
            }
            var info = new ReleaseInfo(v, tag, r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", zip ?? "", size,
                r.GetProperty("html_url").GetString() ?? "", sha);

            if (v <= AppVersion) return new(UpdateState.UpToDate, info, Loc.T($"Tens a versão mais recente ({AppVersion.ToString(3)})."));
            if (zip is null) return new(UpdateState.NoReleases, info, Loc.T($"Há a versão {v.ToString(3)}, mas sem o ficheiro {AssetName}."));
            return new(UpdateState.Available, info, Loc.T($"Há uma versão nova: {v.ToString(3)} (tens a {AppVersion.ToString(3)})."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(UpdateState.Unreachable, null, Loc.T("Não foi possível ver se há atualizações (sem internet?)."));
        }
    }

    /// <summary>
    /// Descarrega e prepara a atualização. Devolve o script que a aplica: corre-o e fecha a app logo a seguir.
    /// </summary>
    public static async Task<string> PrepareAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct = default)
    {
        Simulation.Guard(Loc.T($"atualizar a app para a versão {release.Version.ToString(3)}"));
        var uri = new Uri(release.ZipUrl);
        if (uri.Scheme != "https" || !(uri.Host.EndsWith("github.com") || uri.Host.EndsWith("githubusercontent.com")))
            throw new InvalidOperationException(Loc.T("Endereço de atualização não oficial recusado."));

        var root = Path.Combine(Path.GetTempPath(), "PCOptimizer-update-" + release.Version.ToString(3));
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, AssetName);
        using (var res = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? release.Size;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(100.0 * done / total);
            }
        }

        VerifyDownload(zipPath, release.Size, release.Sha256);
        var script = PrepareFromZip(zipPath, root, AppContext.BaseDirectory, Environment.ProcessId);
        // o ZIP tem de trazer mesmo a versão anunciada (não uma antiga com o nome da nova)
        var exe = Directory.EnumerateFiles(Path.Combine(root, "files"), "PCOptimizer.exe", SearchOption.AllDirectories).First();
        var fi = FileVersionInfo.GetVersionInfo(exe);
        if (new Version(fi.FileMajorPart, fi.FileMinorPart, fi.FileBuildPart) != release.Version)
            throw new InvalidOperationException(Loc.T($"O ficheiro descarregado não é a versão {release.Version.ToString(3)}. Atualização cancelada."));
        return script;
    }

    /// <summary>Recusa um download cortado ou alterado: compara o tamanho e o SHA-256 que o GitHub publica.</summary>
    public static void VerifyDownload(string path, long expectedSize, string? sha256)
    {
        var len = new FileInfo(path).Length;
        string? problem = null;
        if (expectedSize > 0 && len != expectedSize) problem = Loc.T("o download ficou incompleto");
        else if (sha256 != null)
        {
            using var f = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(f)), sha256, StringComparison.OrdinalIgnoreCase))
                problem = Loc.T("o ficheiro não é igual ao publicado (hash diferente)");
        }
        if (problem is null) return;
        try { File.Delete(path); } catch { }
        throw new InvalidOperationException(Loc.T($"Atualização cancelada: {problem}. Tenta outra vez."));
    }

    /// <summary>
    /// Extrai o ZIP e escreve o script que espera que a app (<paramref name="pid"/>) feche,
    /// copia os ficheiros novos para <paramref name="targetDir"/> e volta a abrir a app.
    /// </summary>
    public static string PrepareFromZip(string zipPath, string root, string targetDir, int pid, bool restart = true)
    {
        var extracted = Path.Combine(root, "files");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
        ZipFile.ExtractToDirectory(zipPath, extracted);
        // o ZIP pode ter uma pasta lá dentro
        var exe = Directory.EnumerateFiles(extracted, "PCOptimizer.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidOperationException(Loc.T("O ZIP da atualização não tem o PCOptimizer.exe."));
        var newDir = Path.GetDirectoryName(exe)!;

        var target = targetDir.TrimEnd('\\');
        var script = Path.Combine(root, "aplicar.cmd");
        File.WriteAllText(script, $"""
            @echo off
            rem espera que a app feche
            :wait
            tasklist /fi "PID eq {pid}" | find "{pid}" >nul && (timeout /t 1 /nobreak >nul & goto wait)
            robocopy "{newDir}" "{target}" /E /R:3 /W:1 /NFL /NDL /NJH /NJS >nul
            {(restart ? $"start \"\" \"{Path.Combine(target, "PCOptimizer.exe")}\" --updated" : "")}
            """);
        return script;
    }

    /// <summary>
    /// Atualização a partir de um ZIP já descarregado (ex.: dos artefactos do GitHub Actions, com o repositório privado).
    /// Devolve a versão que vem no ZIP (se se conseguir ler) e o script que a aplica.
    /// </summary>
    public static (Version? Version, string Script) PrepareFromLocalZip(string zipPath)
    {
        Simulation.Guard(Loc.T("atualizar a app a partir de um ZIP"));
        var root = Path.Combine(Path.GetTempPath(), "PCOptimizer-update-zip");
        Directory.CreateDirectory(root);
        var script = PrepareFromZip(zipPath, root, AppContext.BaseDirectory, Environment.ProcessId);
        var exe = Directory.EnumerateFiles(Path.Combine(root, "files"), "PCOptimizer.exe", SearchOption.AllDirectories).First();
        var fi = FileVersionInfo.GetVersionInfo(exe);
        Version? v = fi.FileMajorPart + fi.FileMinorPart + fi.FileBuildPart + fi.FilePrivatePart > 0
            ? new Version(fi.FileMajorPart, fi.FileMinorPart, fi.FileBuildPart, fi.FilePrivatePart)
            : null;
        return (v, script);
    }

    /// <summary>Corre o script de atualização escondido (a app deve fechar a seguir).</summary>
    public static void Apply(string script) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false });
}
