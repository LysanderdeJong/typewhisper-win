using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TypeWhisper.Plugin.OpenAiCompatible;

public partial class OpenAiCompatibleSettingsView : UserControl
{
    private readonly OpenAiCompatiblePlugin _plugin;

    public OpenAiCompatibleSettingsView(OpenAiCompatiblePlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UrlBox.Text = _plugin.BaseUrl ?? "http://localhost:11434";

        if (!string.IsNullOrEmpty(_plugin.ApiKey))
            ApiKeyBox.Password = _plugin.ApiKey;

        ManualTranscriptionBox.Text = _plugin.SelectedTranscriptionModelId ?? "";
        ManualLlmBox.Text = _plugin.SelectedLlmModelId ?? "";

        if (_plugin.IsConfigured)
        {
            ModelsSection.Visibility = Visibility.Visible;
            PopulateModels(_plugin.FetchedModels.ToList());
        }
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url))
            return;

        _plugin.SetBaseUrl(url);

        var key = ApiKeyBox.Password.Trim();
        if (!string.IsNullOrEmpty(key))
            await _plugin.SetApiKeyAsync(key);

        ConnectButton.IsEnabled = false;
        ConnectionStatus.Text = "Verbinde...";
        ConnectionStatus.Foreground = Brushes.Gray;

        try
        {
            var models = await _plugin.FetchModelsAsync();
            var connected = models.Count > 0 || await _plugin.ValidateConnectionAsync();

            if (connected)
            {
                ConnectionStatus.Text = $"Verbunden ({models.Count} Modelle)";
                ConnectionStatus.Foreground = Brushes.Green;
                _plugin.SetFetchedModels(models);
                ModelsSection.Visibility = Visibility.Visible;
                PopulateModels(models);
            }
            else
            {
                ConnectionStatus.Text = "Verbindung fehlgeschlagen";
                ConnectionStatus.Foreground = Brushes.Red;
            }
        }
        catch (Exception ex)
        {
            ConnectionStatus.Text = $"Fehler: {ex.Message}";
            ConnectionStatus.Foreground = Brushes.Red;
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        _ = _plugin.SetApiKeyAsync(ApiKeyBox.Password);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            var models = await _plugin.FetchModelsAsync();
            _plugin.SetFetchedModels(models);
            PopulateModels(models);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void PopulateModels(List<FetchedModel> models)
    {
        if (models.Count > 0)
        {
            PickerSection.Visibility = Visibility.Visible;
            ManualSection.Visibility = Visibility.Collapsed;

            TranscriptionModelPicker.ItemsSource = models;
            LlmModelPicker.ItemsSource = models;

            var selectedTranscription = models.FirstOrDefault(m => m.Id == _plugin.SelectedTranscriptionModelId);
            TranscriptionModelPicker.SelectedItem = selectedTranscription ?? models.FirstOrDefault();

            var selectedLlm = models.FirstOrDefault(m => m.Id == _plugin.SelectedLlmModelId);
            LlmModelPicker.SelectedItem = selectedLlm ?? models.FirstOrDefault();
        }
        else
        {
            PickerSection.Visibility = Visibility.Collapsed;
            ManualSection.Visibility = Visibility.Visible;
        }
    }

    private void OnTranscriptionModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TranscriptionModelPicker.SelectedItem is FetchedModel model)
            _plugin.SelectModel(model.Id);
    }

    private void OnLlmModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LlmModelPicker.SelectedItem is FetchedModel model)
            _plugin.SelectLlmModel(model.Id);
    }

    private void OnSaveManualTranscription(object sender, RoutedEventArgs e)
    {
        var id = ManualTranscriptionBox.Text.Trim();
        if (!string.IsNullOrEmpty(id))
            _plugin.SelectModel(id);
    }

    private void OnSaveManualLlm(object sender, RoutedEventArgs e)
    {
        var id = ManualLlmBox.Text.Trim();
        if (!string.IsNullOrEmpty(id))
            _plugin.SelectLlmModel(id);
    }
}
