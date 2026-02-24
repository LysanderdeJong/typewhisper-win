using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

public partial class DictionarySection : UserControl
{
    public DictionarySection() => InitializeComponent();

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tagStr && int.TryParse(tagStr, out var tab))
        {
            if (DataContext is SettingsWindowViewModel vm)
                vm.Dictionary.SelectedTab = tab;
        }
    }

    private void EditOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            vm.Dictionary.CancelEditCommand.Execute(null);
    }

    private void EditOverlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is SettingsWindowViewModel vm)
        {
            vm.Dictionary.CancelEditCommand.Execute(null);
            e.Handled = true;
        }
    }
}
