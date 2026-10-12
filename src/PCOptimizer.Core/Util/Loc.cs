using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace PCOptimizer.Core.Util;

/// <summary>
/// Tradução da app. O texto original (português) é a chave; a tradução inglesa está em
/// Util/en.json. Sem tradução, fica o original. Os textos com valores ($"… {x:N0} …") são
/// procurados pelo modelo ("… {0:N0} …"), por isso a mesma tradução serve para qualquer valor.
/// </summary>
public static class Loc
{
    static Dictionary<string, string>? _en;
    static string _language = Detect();

    /// <summary>"pt" ou "en".</summary>
    public static string Language
    {
        get => _language;
        set => _language = value is "pt" or "en" ? value : Detect();
    }

    public static bool IsEnglish => _language == "en";

    /// <summary>Língua do Windows: português fica em português, o resto em inglês.</summary>
    public static string Detect() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt" ? "pt" : "en";

    static Dictionary<string, string> English
    {
        get
        {
            if (_en != null) return _en;
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("PCOptimizer.Core.Util.en.json");
                _en = s is null ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
            }
            catch { _en = new(); }
            return _en;
        }
    }

    /// <summary>Todas as traduções (para os testes).</summary>
    public static IReadOnlyDictionary<string, string> EnglishTable => English;

    public static string T(string pt) =>
        !IsEnglish || string.IsNullOrEmpty(pt) ? pt : English.TryGetValue(pt, out var en) ? en : pt;

    public static string T(ref LocHandler text) => text.Resolve();

    /// <summary>Procura um texto qualquer (ex.: vindo do XAML) e devolve a tradução ou null.</summary>
    public static string? TryTranslate(string pt) =>
        IsEnglish && !string.IsNullOrEmpty(pt) && English.TryGetValue(pt, out var en) ? en : null;

    internal static string Format(string key, object?[] args)
    {
        var format = IsEnglish && English.TryGetValue(key, out var en) ? en : key;
        try { return string.Format(CultureInfo.CurrentCulture, format, args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, key, args); }
    }
}

/// <summary>Junta um texto $"…" como modelo ("{0}", "{1:N0}"…) + valores, para o traduzir.</summary>
[InterpolatedStringHandler]
public ref struct LocHandler
{
    readonly StringBuilder _key;
    readonly List<object?> _args;

    public LocHandler(int literalLength, int formattedCount)
    {
        _key = new StringBuilder(literalLength + formattedCount * 4);
        _args = new List<object?>(formattedCount);
    }

    public void AppendLiteral(string s) => _key.Append(s.Replace("{", "{{").Replace("}", "}}"));

    public void AppendFormatted<TValue>(TValue value) => AppendFormatted(value, 0, null);
    public void AppendFormatted<TValue>(TValue value, string? format) => AppendFormatted(value, 0, format);
    public void AppendFormatted<TValue>(TValue value, int alignment) => AppendFormatted(value, alignment, null);

    public void AppendFormatted<TValue>(TValue value, int alignment, string? format)
    {
        _key.Append('{').Append(_args.Count);
        if (alignment != 0) _key.Append(',').Append(alignment);
        if (format != null) _key.Append(':').Append(format);
        _key.Append('}');
        _args.Add(value);
    }

    public string Key => _key.ToString();

    internal string Resolve() => Loc.Format(_key.ToString(), _args.ToArray());
}
