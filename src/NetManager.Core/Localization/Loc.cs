using System.ComponentModel;
using System.Globalization;

namespace NetManager.Core.Localization;

/// <summary>
/// Tiny localization service shared by Core, the Windows backend and the UI, so error messages thrown deep in the
/// backend come out in the user's language too. Strings live in <see cref="EnglishStrings"/> / <see cref="RussianStrings"/>.
/// A missing Russian string falls back to English, a missing English one to the key itself (a unit test guards against both).
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public static IReadOnlyList<string> Supported { get; } = ["en", "ru"];

    public static string Language { get; private set; } = "en";
    public static CultureInfo Culture => _culture;
    private static CultureInfo _culture = new("en-US");
    private static IReadOnlyDictionary<string, string> _table = EnglishStrings.Table;

    /// <summary>Raised after <see cref="SetLanguage"/> changed the language.</summary>
    public static event Action? LanguageChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Russian when the OS UI language is Russian, English otherwise.</summary>
    public static string DetectSystemLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";

    public static void SetLanguage(string code)
    {
        if (!Supported.Contains(code)) code = "en";
        if (code == Language && ReferenceEquals(_table, Table(code))) return;
        Language = code;
        _table = Table(code);
        _culture = new CultureInfo(code == "ru" ? "ru-RU" : "en-US");
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke();
    }

    private static IReadOnlyDictionary<string, string> Table(string code) =>
        code == "ru" ? RussianStrings.Table : EnglishStrings.Table;

    public static string? TryT(string key) =>
        _table.TryGetValue(key, out var s) ? s : EnglishStrings.Table.GetValueOrDefault(key);

    public static string T(string key) => TryT(key) ?? key;

    public static string T(string key, params object?[] args) => string.Format(_culture, T(key), args);

    /// <summary>Indexer used by XAML bindings (<c>{Binding [key], Source=…}</c>); refreshed on language change.</summary>
    public string this[string key] => T(key);
}
