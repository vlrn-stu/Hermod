using System.Text.Json.Serialization;

namespace Hermod.Coordinator.Models;

/// <summary>Body POSTed to Vault42 <c>/auth/login</c>.</summary>
public class Vault42LoginRequest
{
    /// <summary>User email (login identifier).</summary>
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    /// <summary>Plaintext password.</summary>
    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    /// <summary>Whether to request a long-lived (remember-me) refresh token.</summary>
    [JsonPropertyName("remember_me")]
    public bool RememberMe { get; set; }
}

/// <summary>Response from Vault42 <c>/auth/login</c> and <c>/auth/2fa/verify</c>.</summary>
public class Vault42LoginResponse
{
    /// <summary>Short-lived bearer token, or null when a 2FA challenge is required.</summary>
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    /// <summary>Token type (typically <c>Bearer</c>).</summary>
    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    /// <summary>Access token lifetime in seconds.</summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    /// <summary>True when Vault demands a 2FA code before issuing tokens.</summary>
    [JsonPropertyName("requires_2fa")]
    public bool Requires2fa { get; set; }

    /// <summary>Opaque challenge token carried back to <c>/auth/2fa/verify</c> when <see cref="Requires2fa"/> is true.</summary>
    [JsonPropertyName("challenge_token")]
    public string? ChallengeToken { get; set; }
}

/// <summary>Error envelope returned by Vault42 for non-2xx responses.</summary>
public class Vault42ErrorResponse
{
    /// <summary>Human-readable error message.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>Body POSTed to Vault42 <c>/auth/change-password</c>.</summary>
public class Vault42ChangePasswordRequest
{
    /// <summary>Current (known) password.</summary>
    [JsonPropertyName("current_password")]
    public string CurrentPassword { get; set; } = string.Empty;

    /// <summary>Desired new password.</summary>
    [JsonPropertyName("new_password")]
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>Body POSTed to Vault42 <c>/auth/2fa/verify</c>.</summary>
public class Vault42VerifyTwoFactorRequest
{
    /// <summary>Challenge token echoed back from the prior login response.</summary>
    [JsonPropertyName("challenge_token")]
    public string ChallengeToken { get; set; } = string.Empty;

    /// <summary>6-digit TOTP code from the authenticator app.</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;
}
