using PCOptimizer.Core.Settings;

namespace PCOptimizer.Core.Gaming;

/// <summary>Vê de 3 em 3 segundos se abriu ou fechou um jogo (conhecido ou em ecrã inteiro). Só lê processos e a janela da frente.</summary>
public sealed class GameMonitor : IDisposable
{
    readonly Func<AppSettings> _settings;
    readonly Timer _timer;
    int _busy;

    public GameMonitor(Func<AppSettings> settings)
    {
        _settings = settings;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public RunningGame? Current { get; private set; }

    /// <summary>Chamados numa thread de fundo.</summary>
    public event Action<RunningGame>? Started;
    public event Action<RunningGame>? Stopped;

    public void Start() => _timer.Change(1000, 3000);
    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var now = GameCatalog.FindRunning(_settings(), Current);
            var before = Current;
            if (before != null && (now is null || now.Pid != before.Pid))
            {
                Current = null;
                Stopped?.Invoke(before);
            }
            if (now != null && (before is null || now.Pid != before.Pid))
            {
                Current = now;
                Started?.Invoke(now);
            }
        }
        catch { }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public void Dispose() => _timer.Dispose();
}
