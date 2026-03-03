using System.Windows.Data;
using System.Windows.Markup;

namespace TypeWhisper.Windows.Services.Localization;

/// <summary>
/// WPF markup extension: {loc:Str Key}
/// Creates a binding to Loc.Instance[Key] that updates on language change.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class StrExtension : MarkupExtension
{
    public string Key { get; set; }

    public StrExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
