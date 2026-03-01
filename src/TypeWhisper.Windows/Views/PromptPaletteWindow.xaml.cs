using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Views;

public partial class PromptPaletteWindow : Window
{
    private IReadOnlyList<PromptAction> _allActions = [];
    private List<PromptAction> _filteredActions = [];

    public PromptAction? SelectedAction { get; private set; }

    public string SourceText { get; set; } = "";

    public PromptPaletteWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
    }

    public void SetActions(IReadOnlyList<PromptAction> actions)
    {
        _allActions = actions;
        ApplyFilter(string.Empty);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CenterOnActiveMonitor();

        // Show source text preview if available
        if (!string.IsNullOrWhiteSpace(SourceText))
        {
            SourcePreviewText.Text = SourceText.Length > 120
                ? SourceText[..120] + "..."
                : SourceText;
            SourcePreviewBorder.Visibility = Visibility.Visible;
        }

        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (SelectedAction == null)
            Close();
    }

    private void CenterOnActiveMonitor()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;

        var dpi = source.CompositionTarget.TransformToDevice.M11;

        GetCursorPos(out var cursor);
        var hMonitor = MonitorFromPoint(cursor, 2 /* MONITOR_DEFAULTTONEAREST */);

        var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(hMonitor, ref mi)) return;

        var screenWidth = (mi.rcWork.Right - mi.rcWork.Left) / dpi;
        var screenHeight = (mi.rcWork.Bottom - mi.rcWork.Top) / dpi;
        var screenLeft = mi.rcWork.Left / dpi;
        var screenTop = mi.rcWork.Top / dpi;

        Left = screenLeft + (screenWidth - Width) / 2;
        Top = screenTop + (screenHeight - Height) / 3;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter(SearchBox.Text);
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (ActionListBox.SelectedIndex < _filteredActions.Count - 1)
                    ActionListBox.SelectedIndex++;
                else if (ActionListBox.SelectedIndex == -1 && _filteredActions.Count > 0)
                    ActionListBox.SelectedIndex = 0;
                ActionListBox.ScrollIntoView(ActionListBox.SelectedItem);
                e.Handled = true;
                break;

            case Key.Up:
                if (ActionListBox.SelectedIndex > 0)
                    ActionListBox.SelectedIndex--;
                ActionListBox.ScrollIntoView(ActionListBox.SelectedItem);
                e.Handled = true;
                break;

            case Key.Enter:
                SelectAndClose();
                e.Handled = true;
                break;

            case Key.Escape:
                SelectedAction = null;
                Close();
                e.Handled = true;
                break;
        }
    }

    private void ActionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SearchBox.Focus();
    }

    private void ActionListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        SelectAndClose();
    }

    private void ApplyFilter(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _filteredActions = _allActions.ToList();
        }
        else
        {
            _filteredActions = _allActions
                .Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           a.SystemPrompt.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        ActionListBox.ItemsSource = _filteredActions;
        EmptyText.Visibility = _filteredActions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_filteredActions.Count > 0)
            ActionListBox.SelectedIndex = 0;
    }

    private void SelectAndClose()
    {
        if (ActionListBox.SelectedItem is PromptAction action)
        {
            SelectedAction = action;
            DialogResult = true;
            Close();
        }
    }

    public void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusBorder.Visibility = Visibility.Visible;
        ActionListBox.IsEnabled = false;
        SearchBox.IsEnabled = false;
    }

    // P/Invoke for monitor positioning
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
