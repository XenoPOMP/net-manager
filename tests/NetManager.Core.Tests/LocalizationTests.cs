using System.Text.RegularExpressions;
using NetManager.Core.Localization;
using NetManager.Core.Models;
using NetManager.Core.Services;

namespace NetManager.Core.Tests;

[Collection("Loc")] // these tests switch the process-wide language
public class LocalizationTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)[^}]*\}");

    [Fact]
    public void RussianAndEnglishHaveTheSameKeys()
    {
        var en = EnglishStrings.Table.Keys.ToHashSet();
        var ru = RussianStrings.Table.Keys.ToHashSet();
        Assert.Empty(en.Except(ru));   // untranslated
        Assert.Empty(ru.Except(en));   // stale / typo
    }

    [Fact]
    public void TranslationsUseTheSamePlaceholders()
    {
        foreach (var (key, en) in EnglishStrings.Table)
        {
            var ru = RussianStrings.Table[key];
            Assert.False(string.IsNullOrWhiteSpace(ru), key);
            var a = Placeholder.Matches(en).Select(m => m.Groups[1].Value).OrderBy(x => x);
            var b = Placeholder.Matches(ru).Select(m => m.Groups[1].Value).OrderBy(x => x);
            Assert.True(a.SequenceEqual(b), $"placeholder mismatch in '{key}'");
        }
    }

    [Fact]
    public void LanguageSwitchChangesModelText()
    {
        try
        {
            Loc.SetLanguage("ru");
            var a = new AdapterInfo { Id = "{x}", InterfaceIndex = 1, Name = "Ethernet", Status = AdapterStatus.Disabled, Ipv4 = new Ipv4Config() };
            Assert.Equal("Отключено", a.StatusText);
            Assert.Equal("Нет подключения", Loc.T("adapter.row.notConnected"));
            Assert.Contains("IPv4", Ipv4Util.Validate(new Ipv4Config { Method = Ipv4Method.Manual, Addresses = [new("1.2.3", 24)] })[0]);

            Loc.SetLanguage("en");
            Assert.Equal("Disabled", a.StatusText);
        }
        finally { Loc.SetLanguage("en"); }
    }

    [Fact]
    public void SpeedUsesTheLanguagesDecimalSeparator()
    {
        try
        {
            var a = new AdapterInfo { Id = "{x}", InterfaceIndex = 1, Name = "x", SpeedBitsPerSecond = 2_500_000_000, Ipv4 = new Ipv4Config() };
            Loc.SetLanguage("ru");
            Assert.Equal("2,5 Гбит/с", a.SpeedText);
            Loc.SetLanguage("en");
            Assert.Equal("2.5 Gbit/s", a.SpeedText);
        }
        finally { Loc.SetLanguage("en"); }
    }

    [Fact]
    public void UnknownKeyFallsBackToTheKey() => Assert.Equal("no.such.key", Loc.T("no.such.key"));

    [Fact]
    public void UnsupportedLanguageFallsBackToEnglish()
    {
        Loc.SetLanguage("de");
        Assert.Equal("en", Loc.Language);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new SettingsStore(path);
            Assert.Null(store.Load().Language);
            store.Save(new AppSettings { Language = "ru" });
            Assert.Equal("ru", store.Load().Language);
        }
        finally { File.Delete(path); }
    }
}
