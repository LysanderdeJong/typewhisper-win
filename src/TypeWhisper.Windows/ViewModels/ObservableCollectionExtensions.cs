using System.Collections.ObjectModel;

namespace TypeWhisper.Windows.ViewModels;

internal static class ObservableCollectionExtensions
{
    public static void Replace<T>(this ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }
}
