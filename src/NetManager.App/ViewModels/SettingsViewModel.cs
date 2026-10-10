using CommunityToolkit.Mvvm.ComponentModel;
using NetManager.Core.Localization;
using NetManager.Core.Services;

namespace NetManager.App.ViewModels;

/// <summary>Language names are shown in their own language, so they are not part of the translation tables.</summary>
public sealed record LanguageOption(string Code, string Name);

public sealed record ThemeOption(string Code)
{
    public string Label => Loc.T($"settings.theme.{Code}");
}

/// <summary>Everything under "Settings". Add new settings here; they are persisted to settings.json as they change.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;

    public static IReadOnlyList<LanguageOption> Languages { get; } = [new("en", "English"), new("ru", "Русский")];
    public static IReadOnlyList<ThemeOption> Themes { get; } = ThemeService.Modes.Select(m => new ThemeOption(m)).ToList();

    [ObservableProperty] private string _selectedLanguage;
    [ObservableProperty] private string _selectedTheme;

    /// <summary>Raised after the language changed; the shell rebuilds the pages that were rendered in the old one.</summary>
    public event Action? LanguageChanged;

    public SettingsViewModel(SettingsStore store)
    {
        _store = store;
        _settings = store.Load();
        _selectedLanguage = Loc.Language;
        _selectedTheme = _settings.Theme is { } t && ThemeService.Modes.Contains(t) ? t : "system";
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        // A ComboBox that is being (re)built can push null/blank into the binding; never treat that as a choice.
        if (string.IsNullOrEmpty(value) || value == Loc.Language) return;
        Loc.SetLanguage(value);
        _settings.Language = Loc.Language;
        Save();
        LanguageChanged?.Invoke();
    }

    partial void OnSelectedThemeChanged(string value)
    {
        if (string.IsNullOrEmpty(value) || !ThemeService.Modes.Contains(value)) return;
        _settings.Theme = value;
        Save();
        ThemeService.Apply(value);
    }

    private void Save()
    {
        try { _store.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a setting that doesn't persist isn't worth an error dialog */ }
    }
}
