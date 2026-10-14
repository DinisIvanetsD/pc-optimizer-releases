using System.Globalization;

namespace PCOptimizer.Core.Benchmark;

/// <summary>Lê as linhas do CSV do PresentMon e devolve o tempo de cada frame em milissegundos.</summary>
public sealed class FrameCsvReader
{
    readonly int _column;

    FrameCsvReader(int column) => _column = column;

    /// <summary>Procura a coluna do tempo do frame no cabeçalho (FrameTime; nas versões novas, MsBetweenPresents). Sem ela, null.</summary>
    public static FrameCsvReader? TryCreate(string headerLine)
    {
        var cols = headerLine.Split(',');
        var i = Array.IndexOf(cols, "FrameTime");
        if (i < 0) i = Array.IndexOf(cols, "MsBetweenPresents");
        return i < 0 ? null : new FrameCsvReader(i);
    }

    /// <summary>O tempo do frame, ou null se a linha for o cabeçalho, estiver cortada, tiver NA ou um valor inválido.</summary>
    public double? ReadFrameMs(string line)
    {
        // shortcut: divide por vírgulas sem aspas; nomes de exe com vírgula partem a linha (nunca vistos), mudar para um leitor CSV se aparecerem
        var fields = line.Split(',');
        return _column < fields.Length
            && double.TryParse(fields[_column], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
            && double.IsFinite(ms) && ms > 0
            ? ms : null;
    }
}

/// <summary>FPS de uma série de frames: média, 1% low, 0,1% low e engasgos.</summary>
public sealed record FrameStats(int Frames, double Seconds, double AvgFps, double Low1Fps, double Low01Fps, int Stutters)
{
    /// <summary>Um engasgo é um frame que demora mais do que isto vezes a mediana.</summary>
    const double StutterFactor = 2.5;

    public static FrameStats? From(IReadOnlyList<double> frameMs)
    {
        var n = frameMs.Count;
        if (n == 0) return null;

        var sorted = frameMs.OrderBy(x => x).ToArray();
        var seconds = frameMs.Sum() / 1000;
        var median = n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
        // nearest-rank: o percentil p dos tempos é o p-ésimo mais lento; o "low" é o FPS desse frame
        double LowFps(double p) => 1000 / sorted[(int)Math.Ceiling(p * n) - 1];

        return new FrameStats(n, seconds, n / seconds, LowFps(0.99), LowFps(0.999),
            frameMs.Count(x => x > StutterFactor * median));
    }
}

/// <summary>Guarda os últimos N segundos de frames (para a janela ao vivo). Pode ser usada por várias threads.</summary>
public sealed class FrameWindow
{
    readonly double _limitMs;
    readonly Queue<double> _frames = new();
    readonly object _gate = new();
    double _sumMs;

    public FrameWindow(double seconds) => _limitMs = seconds * 1000;

    public void Add(double ms)
    {
        lock (_gate)
        {
            _frames.Enqueue(ms);
            _sumMs += ms;
            while (_frames.Count > 0 && _sumMs > _limitMs) _sumMs -= _frames.Dequeue();
            if (_frames.Count == 0) _sumMs = 0;   // não deixa os arredondamentos acumularem
        }
    }

    public FrameStats? Stats()
    {
        lock (_gate) return FrameStats.From(_frames.ToArray());
    }
}
