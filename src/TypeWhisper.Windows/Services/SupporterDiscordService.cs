using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core;

namespace TypeWhisper.Windows.Services;

public enum SupporterDiscordClaimState
{
    Unavailable,
    Unlinked,
    Pending,
    Linked,
    Failed,
}

/// <summary>
/// Lightweight Windows port of the supporter Discord claim flow.
/// Uses the same local claim service endpoints as macOS, but relies on manual refresh instead of callback handling.
/// </summary>
public sealed partial class SupporterDiscordService : ObservableObject
{
    private const string DefaultBaseUrl = "http://127.0.0.1:8787";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _statusPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkedRoles))]
    [NotifyPropertyChangedFor(nameof(LinkedRolesText))]
    private SupporterDiscordClaimState _claimState = SupporterDiscordClaimState.Unavailable;

    [ObservableProperty]
    private string? _discordUsername;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkedRoles))]
    [NotifyPropertyChangedFor(nameof(LinkedRolesText))]
    private string[] _linkedRoles = Array.Empty<string>();

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _sessionId;

    [ObservableProperty]
    private bool _isWorking;

    public SupporterDiscordService()
    {
        _statusPath = Path.Combine(TypeWhisperEnvironment.DataPath, "supporter-discord.json");
        LoadPersistedStatus();
    }

    public bool HasLinkedRoles => LinkedRoles.Length > 0;
    public string LinkedRolesText => string.Join(", ", LinkedRoles);
    public string GitHubSponsorsUrl => $"{BaseUrl}/claims/github";

    private string BaseUrl =>
        Environment.GetEnvironmentVariable("TYPEWHISPER_DISCORD_CLAIM_BASE_URL")
        ?? DefaultBaseUrl;

    public async Task<Uri?> CreateClaimSessionAsync(LicenseService license, CancellationToken ct = default)
    {
        var proof = license.SupporterClaimProof;
        if (proof is null)
        {
            HandleSupporterEntitlementRemoved();
            ClaimState = SupporterDiscordClaimState.Failed;
            ErrorMessage = "An active supporter license is required before you can claim Discord status.";
            PersistStatus();
            return null;
        }

        IsWorking = true;
        ErrorMessage = null;

        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"{BaseUrl}/claims/polar/start",
                new SupporterDiscordStartRequest(
                    proof.Key,
                    proof.ActivationId,
                    proof.Tier.ToString().ToLowerInvariant(),
                    GetAppVersion()),
                ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(ParseDiscordError(json, $"Discord claim start failed (HTTP {(int)response.StatusCode})"));

            var payload = JsonSerializer.Deserialize<SupporterDiscordStartResponse>(json)
                ?? throw new InvalidOperationException("Discord claim start failed: empty response.");

            SessionId = payload.SessionId;
            ClaimState = SupporterDiscordClaimState.Pending;
            DiscordUsername = null;
            LinkedRoles = Array.Empty<string>();
            ErrorMessage = null;
            PersistStatus();
            return Uri.TryCreate(payload.ClaimUrl, UriKind.Absolute, out var claimUrl) ? claimUrl : null;
        }
        catch (Exception ex)
        {
            ClaimState = SupporterDiscordClaimState.Failed;
            ErrorMessage = ex.Message;
            PersistStatus();
            return null;
        }
        finally
        {
            IsWorking = false;
        }
    }

    public async Task<Uri?> ReconnectAsync(LicenseService license, CancellationToken ct = default)
    {
        ClaimState = SupporterDiscordClaimState.Unlinked;
        DiscordUsername = null;
        LinkedRoles = Array.Empty<string>();
        ErrorMessage = null;
        SessionId = null;
        PersistStatus();
        return await CreateClaimSessionAsync(license, ct);
    }

    public async Task RefreshStatusIfNeededAsync(LicenseService license, CancellationToken ct = default)
    {
        if (license.SupporterClaimProof is null)
        {
            HandleSupporterEntitlementRemoved();
            return;
        }

        if (ClaimState is SupporterDiscordClaimState.Pending or SupporterDiscordClaimState.Linked || !string.IsNullOrWhiteSpace(SessionId))
            await RefreshClaimStatusAsync(license, ct);
    }

    public async Task RefreshClaimStatusAsync(LicenseService license, CancellationToken ct = default)
    {
        var proof = license.SupporterClaimProof;
        if (proof is null)
        {
            HandleSupporterEntitlementRemoved();
            return;
        }

        IsWorking = true;

        try
        {
            var url = $"{BaseUrl}/claims/polar/status?activation_id={Uri.EscapeDataString(proof.ActivationId)}";
            if (!string.IsNullOrWhiteSpace(SessionId))
                url += $"&session_id={Uri.EscapeDataString(SessionId)}";

            using var response = await _http.GetAsync(url, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(ParseDiscordError(json, $"Discord status refresh failed (HTTP {(int)response.StatusCode})"));

            var payload = JsonSerializer.Deserialize<SupporterDiscordStatusResponse>(json)
                ?? throw new InvalidOperationException("Discord status refresh failed: empty response.");

            ClaimState = payload.Status switch
            {
                "unlinked" => SupporterDiscordClaimState.Unlinked,
                "pending" => SupporterDiscordClaimState.Pending,
                "linked" => SupporterDiscordClaimState.Linked,
                "failed" => SupporterDiscordClaimState.Failed,
                _ => SupporterDiscordClaimState.Failed,
            };
            DiscordUsername = payload.DiscordUsername;
            LinkedRoles = payload.LinkedRoles ?? Array.Empty<string>();
            ErrorMessage = payload.ErrorMessage;
            SessionId = string.IsNullOrWhiteSpace(payload.SessionId) ? SessionId : payload.SessionId;
            PersistStatus();
        }
        catch (Exception ex)
        {
            if (ClaimState == SupporterDiscordClaimState.Linked)
            {
                ErrorMessage = ex.Message;
            }
            else
            {
                ClaimState = SupporterDiscordClaimState.Failed;
                ErrorMessage = ex.Message;
            }

            PersistStatus();
            Debug.WriteLine($"Supporter Discord refresh failed: {ex.Message}");
        }
        finally
        {
            IsWorking = false;
        }
    }

    public void HandleSupporterEntitlementRemoved()
    {
        ClaimState = SupporterDiscordClaimState.Unavailable;
        DiscordUsername = null;
        LinkedRoles = Array.Empty<string>();
        ErrorMessage = null;
        SessionId = null;
        PersistStatus();
    }

    private void PersistStatus()
    {
        try
        {
            var payload = new SupporterDiscordPersistedState
            {
                ClaimState = ClaimState.ToString(),
                DiscordUsername = DiscordUsername,
                LinkedRoles = LinkedRoles,
                ErrorMessage = ErrorMessage,
                SessionId = SessionId,
            };

            File.WriteAllText(_statusPath, JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Persisting supporter Discord state failed: {ex.Message}");
        }
    }

    private void LoadPersistedStatus()
    {
        try
        {
            if (!File.Exists(_statusPath))
                return;

            var json = File.ReadAllText(_statusPath, System.Text.Encoding.UTF8);
            var payload = JsonSerializer.Deserialize<SupporterDiscordPersistedState>(json);
            if (payload is null)
                return;

            ClaimState = Enum.TryParse<SupporterDiscordClaimState>(payload.ClaimState, out var state)
                ? state
                : SupporterDiscordClaimState.Unavailable;
            DiscordUsername = payload.DiscordUsername;
            LinkedRoles = payload.LinkedRoles ?? Array.Empty<string>();
            ErrorMessage = payload.ErrorMessage;
            SessionId = payload.SessionId;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loading supporter Discord state failed: {ex.Message}");
        }
    }

    private static string GetAppVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    private static string ParseDiscordError(string? json, string fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
            return fallback;

        try
        {
            var payload = JsonSerializer.Deserialize<SupporterDiscordErrorResponse>(json);
            if (!string.IsNullOrWhiteSpace(payload?.Error))
                return payload.Error;
        }
        catch
        {
            // Ignore malformed error payloads.
        }

        return fallback;
    }

    private sealed record SupporterDiscordPersistedState
    {
        [JsonPropertyName("claimState")] public string? ClaimState { get; init; }
        [JsonPropertyName("discordUsername")] public string? DiscordUsername { get; init; }
        [JsonPropertyName("linkedRoles")] public string[]? LinkedRoles { get; init; }
        [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
        [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    }

    private sealed record SupporterDiscordStartRequest(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("activationId")] string ActivationId,
        [property: JsonPropertyName("tier")] string Tier,
        [property: JsonPropertyName("appVersion")] string AppVersion);

    private sealed record SupporterDiscordStartResponse
    {
        [JsonPropertyName("session_id")] public string? SessionId { get; init; }
        [JsonPropertyName("claim_url")] public string? ClaimUrl { get; init; }
    }

    private sealed record SupporterDiscordStatusResponse
    {
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("discord_username")] public string? DiscordUsername { get; init; }
        [JsonPropertyName("linked_roles")] public string[]? LinkedRoles { get; init; }
        [JsonPropertyName("error")] public string? ErrorMessage { get; init; }
        [JsonPropertyName("session_id")] public string? SessionId { get; init; }
    }

    private sealed record SupporterDiscordErrorResponse
    {
        [JsonPropertyName("error")] public string? Error { get; init; }
    }
}
