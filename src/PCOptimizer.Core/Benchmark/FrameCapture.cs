using System.Diagnostics;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Benchmark;

/// <summary>
/// Corre o PresentMon só para o processo de um jogo e devolve o tempo de cada frame.
/// Não depende de o PresentMon terminar sozinho (já ficou vivo sem escrever): o Dispose mata-o e fecha a sessão ETW.
/// Os eventos chegam em threads de fundo.
/// </summary>
public sealed class FrameCapture : IDisposable
{
    const string SessionName = "Rigwise";

    public static string ExePath { get; } = Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon-2.6.0-x64.exe");

    public static string Arguments(int pid) =>
        $"--process_id {pid} --output_stdout --no_console_stats --session_name {SessionName} --stop_existing_session --terminate_on_proc_exit";

    readonly int _pid;
    readonly object _gate = new();
    readonly CancellationTokenSource _stop = new();   // o Dispose cancela a espera pela saída
    Process? _process;
    bool _started;
    volatile bool _disposed;
    FrameCsvReader? _reader;      // só a thread do stdout lhe toca
    string? _firstError;          // 1.ª linha do stderr

    public FrameCapture(int pid) => _pid = pid;

    /// <summary>Tempo de um frame, em milissegundos.</summary>
    public event Action<double>? Frame;

    /// <summary>O PresentMon não arrancou ou terminou sem ser pedido, a não ser no fim normal (ver <see cref="IsNormalEnd"/>). Mensagem para o log.</summary>
    public event Action<string>? Failed;

    public void Start()
    {
        string? error = null;
        lock (_gate)
        {
            if (_disposed || _started) return;
            if (!File.Exists(ExePath))
                error = Loc.T($"Não encontrei o PresentMon em {ExePath}.");
            else
                try
                {
                    var p = new Process
                    {
                        StartInfo = new ProcessStartInfo(ExePath, Arguments(_pid))
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                        },
                    };
                    p.OutputDataReceived += (_, e) => OnOutput(e.Data);
                    p.ErrorDataReceived += (_, e) => OnError(e.Data);
                    p.Start();
                    _process = p;
                    _started = true;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    var ct = _stop.Token;
                    Task.Run(() => WatchAsync(p, ct));
                }
                catch (Exception ex)
                {
                    error = Loc.T($"Não foi possível iniciar o PresentMon: {ex.Message}");
                }
        }
        if (error != null) Raise(Failed, error);
    }

    void OnOutput(string? line)
    {
        if (line == null || _disposed) return;
        // as linhas antes do cabeçalho (se as houver) ignoram-se
        if (_reader == null) { _reader = FrameCsvReader.TryCreate(line); return; }
        if (_reader.ReadFrameMs(line) is double ms) Raise(Frame, ms);
    }

    void OnError(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line)) Interlocked.CompareExchange(ref _firstError, line.Trim(), null);
    }

    // Não usa o evento Process.Exited: o .NET segura o lock do Process enquanto o evento corre, e o Dispose precisa
    // desse lock. Aqui os assinantes correm numa thread do pool, sem lock nenhum do Process.
    async Task WatchAsync(Process p, CancellationToken ct)
    {
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);   // espera também pelo fim do stderr
            if (_disposed) return;
            var code = p.ExitCode;
            if (!IsNormalEnd(code, IsRunning(_pid))) Raise(Failed, ExitMessage(code, _firstError));
        }
        catch { }                     // o Dispose cancela a espera e pode largar o processo a meio
    }

    /// <summary>Código 0 com o jogo já fechado: o PresentMon acabou porque o jogo acabou. Não é falha.</summary>
    public static bool IsNormalEnd(int exitCode, bool targetAlive) => exitCode == 0 && !targetAlive;

    static bool IsRunning(int pid)
    {
        try { using var t = Process.GetProcessById(pid); return !t.HasExited; }
        catch (ArgumentException) { return false; }   // já não existe
        catch { return true; }                        // não deu para ver: melhor avisar do que esconder
    }

    /// <summary>A mensagem para o log quando o PresentMon sai sem ter sido pedido. Código 6 = acesso negado.</summary>
    public static string ExitMessage(int exitCode, string? firstStderrLine)
    {
        if (exitCode == 6)
            return Loc.T("Acesso negado ao medir FPS (o PresentMon precisa de administrador ou do grupo \"Performance Log Users\").");
        var err = firstStderrLine?.Trim();
        return string.IsNullOrEmpty(err)
            ? Loc.T($"O PresentMon terminou (código {exitCode}).")
            : Loc.T($"O PresentMon terminou (código {exitCode}): {err}");
    }

    // Um assinante que falhe não pode derrubar a thread do stdout nem a do Exited.
    static void Raise<T>(Action<T>? handler, T arg)
    {
        try { handler?.Invoke(arg); } catch { }
    }

    /// <summary>Fecha a sessão ETW "Rigwise". Uma sessão ETW sobrevive ao processo que a abriu à força.</summary>
    public static async Task StopSessionAsync()
    {
        try { await Shell.RunAsync("logman", $"stop {SessionName} -ets", 15_000); } catch { }
    }

    public void Dispose()
    {
        Process? p;
        bool started;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            p = _process;
            started = _started;
            _process = null;
        }
        Frame = null;
        Failed = null;

        try { _stop.Cancel(); } catch { }
        try { if (p != null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p?.Dispose(); } catch { }
        // sem processo lançado não há sessão nossa para fechar (e não se mata a de outra captura)
        // Task.Run: sem o contexto da interface, o Wait não bloqueia a continuação do logman
        if (started) try { Task.Run(StopSessionAsync).Wait(TimeSpan.FromSeconds(5)); } catch { }
        _stop.Dispose();
    }
}
