using System.Globalization;
using System.Windows.Data;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.Converters;

public sealed class ProfileSubtitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Profile profile) return Loc.Instance["Profiles.NoAssignment"];

        var parts = new List<string>();
        if (profile.ProcessNames.Count > 0)
            parts.Add(string.Join(", ", profile.ProcessNames));
        if (profile.UrlPatterns.Count > 0)
            parts.Add(string.Join(", ", profile.UrlPatterns));
        if (!string.IsNullOrEmpty(profile.InputLanguage))
            parts.Add(profile.InputLanguage);

        return parts.Count > 0 ? string.Join(" \u00B7 ", parts) : Loc.Instance["Profiles.NoAssignment"];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
