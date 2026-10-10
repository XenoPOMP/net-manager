using Avalonia.Markup.Xaml;
using NetManager.Core.Localization;

namespace NetManager.App.Localization;

/// <summary>
/// <c>Text="{loc:T conn.title}"</c> — looks the key up in the current language when the XAML is loaded.
/// Pages are re-created when the language changes (see MainViewModel), so a plain lookup is enough there.
/// </summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
