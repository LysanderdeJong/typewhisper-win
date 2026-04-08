using System.Windows;
using System.Windows.Controls;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

public partial class DashboardSection : UserControl
{
    public DashboardSection() => InitializeComponent();

    private void WeekChecked(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            vm.Dashboard.SelectedPeriod = 0;
    }

    private void MonthChecked(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            vm.Dashboard.SelectedPeriod = 1;
    }

    private void AllTimeChecked(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            vm.Dashboard.SelectedPeriod = 2;
    }
}
