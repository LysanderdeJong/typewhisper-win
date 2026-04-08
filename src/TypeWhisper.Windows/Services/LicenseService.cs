using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.Core;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Manages software licensing via Polar.sh API.
/// Handles activation, validation, deactivation, and supporter tiers.
/// </summary>
public sealed class LicenseService
{
    private const string BaseUrl = "https://api.polar.sh/v1/customer-portal/license-keys";
    private const string OrganizationId = ""; // Set via AppConstants or config
    private static readonly TimeSpan LicenseValidationInterval = TimeSpan.FromDays(7);
    private static readonly TimeSpan SupporterValidationInterval = TimeSpan.FromDays(30);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _credentialPath;

    public LicenseStatus Status { get; private set; } = LicenseStatus.Unlicensed;
    public SupporterTier Tier { get; private set; } = SupporterTier.None;
    public bool IsLifetime { get; private set; }
    public string? LicenseKey { get; private set; }
    public string? ActivationId { get; private set; }
    public DateTime? LastValidated { get; private set; }

    public event Action? StatusChanged;

    public LicenseService()
    {
        _credentialPath = Path.Combine(TypeWhisperEnvironment.DataPath, "license.json");
        LoadCredentials();
    }

    public async Task ActivateAsync(string key, CancellationToken ct = default)
    {
        var body = new { key, organization_id = OrganizationId, label = Environment.MachineName };
        var response = await _http.PostAsJsonAsync($"{BaseUrl}/activate", body, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Activation failed: {json}");

        using var doc = JsonDocument.Parse(json);
        ActivationId = doc.RootElement.GetProperty("id").GetString();
        LicenseKey = key;
        Status = LicenseStatus.Active;

        // Check for lifetime
        IsLifetime = !doc.RootElement.TryGetProperty("expires_at", out var exp) || exp.ValueKind == JsonValueKind.Null;

        // Check supporter tier from benefits
        DetectSupporterTier(doc.RootElement);

        LastValidated = DateTime.UtcNow;
        SaveCredentials();
        StatusChanged?.Invoke();
    }

    public async Task ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(LicenseKey) || string.IsNullOrEmpty(ActivationId)) return;

        // Skip if recently validated
        if (LastValidated.HasValue && DateTime.UtcNow - LastValidated.Value < LicenseValidationInterval) return;

        try
        {
            var body = new { key = LicenseKey, organization_id = OrganizationId, activation_id = ActivationId };
            var response = await _http.PostAsJsonAsync($"{BaseUrl}/validate", body, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.GetProperty("status").GetString();

            Status = status == "granted" ? LicenseStatus.Active : LicenseStatus.Expired;
            DetectSupporterTier(doc.RootElement);
            LastValidated = DateTime.UtcNow;
            SaveCredentials();
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"License validation failed: {ex.Message}");
        }
    }

    public async Task DeactivateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(LicenseKey) || string.IsNullOrEmpty(ActivationId)) return;

        try
        {
            var body = new { key = LicenseKey, organization_id = OrganizationId, activation_id = ActivationId };
            await _http.PostAsJsonAsync($"{BaseUrl}/deactivate", body, ct);
        }
        catch { }

        LicenseKey = null;
        ActivationId = null;
        Status = LicenseStatus.Unlicensed;
        Tier = SupporterTier.None;
        IsLifetime = false;
        LastValidated = null;
        SaveCredentials();
        StatusChanged?.Invoke();
    }

    private void DetectSupporterTier(JsonElement root)
    {
        Tier = SupporterTier.None;
        if (!root.TryGetProperty("benefit", out var benefit)) return;
        if (!benefit.TryGetProperty("description", out var desc)) return;

        var description = desc.GetString()?.ToLowerInvariant() ?? "";
        if (description.Contains("gold")) Tier = SupporterTier.Gold;
        else if (description.Contains("silver")) Tier = SupporterTier.Silver;
        else if (description.Contains("bronze")) Tier = SupporterTier.Bronze;
    }

    private void SaveCredentials()
    {
        try
        {
            var data = new LicenseData
            {
                Key = LicenseKey,
                ActivationId = ActivationId,
                Status = Status.ToString(),
                Tier = Tier.ToString(),
                IsLifetime = IsLifetime,
                LastValidated = LastValidated?.ToString("o")
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_credentialPath, json);
        }
        catch { }
    }

    private void LoadCredentials()
    {
        try
        {
            if (!File.Exists(_credentialPath)) return;
            var json = File.ReadAllText(_credentialPath);
            var data = JsonSerializer.Deserialize<LicenseData>(json);
            if (data is null) return;

            LicenseKey = data.Key;
            ActivationId = data.ActivationId;
            IsLifetime = data.IsLifetime;
            if (Enum.TryParse<LicenseStatus>(data.Status, out var s)) Status = s;
            if (Enum.TryParse<SupporterTier>(data.Tier, out var t)) Tier = t;
            if (DateTime.TryParse(data.LastValidated, out var lv)) LastValidated = lv;
        }
        catch { }
    }

    private sealed record LicenseData
    {
        [JsonPropertyName("key")] public string? Key { get; init; }
        [JsonPropertyName("activationId")] public string? ActivationId { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("tier")] public string? Tier { get; init; }
        [JsonPropertyName("isLifetime")] public bool IsLifetime { get; init; }
        [JsonPropertyName("lastValidated")] public string? LastValidated { get; init; }
    }
}

public enum LicenseStatus { Unlicensed, Active, Expired }
public enum SupporterTier { None, Bronze, Silver, Gold }
