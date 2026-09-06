// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Foundry.Deploy.Services.Http;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.Autopilot;

public interface IAutopilotGraphTokenService
{
    Task<string> AcquireAccessTokenAsync(
        string tenantId,
        string clientId,
        X509Certificate2 certificate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Acquires app-only Graph tokens with a certificate client assertion; no client secrets or interactive flows are supported.
/// </summary>
public sealed class AutopilotGraphTokenService(
    HttpClient httpClient,
    ILogger<AutopilotGraphTokenService> logger,
    AutopilotGraphTokenServiceOptions? options = null) : IAutopilotGraphTokenService
{
    private const string Scope = "https://graph.microsoft.com/.default";
    private const string ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    private readonly HttpClient httpClient = httpClient;
    private readonly ILogger logger = logger;
    private readonly AutopilotGraphTokenServiceOptions options = options ?? new AutopilotGraphTokenServiceOptions();

    public async Task<string> AcquireAccessTokenAsync(
        string tenantId,
        string clientId,
        X509Certificate2 certificate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(certificate);

        string tokenEndpoint = $"https://login.microsoftonline.com/{tenantId.Trim()}/oauth2/v2.0/token";
        string clientAssertion = CreateClientAssertion(tokenEndpoint, clientId.Trim(), certificate);

        return await HttpRetryPolicy.ExecuteAsync(
            async ct =>
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId.Trim(),
                    ["scope"] = Scope,
                    ["grant_type"] = "client_credentials",
                    ["client_assertion_type"] = ClientAssertionType,
                    ["client_assertion"] = clientAssertion
                });
                using HttpResponseMessage response = await httpClient.PostAsync(tokenEndpoint, content, ct)
                    .ConfigureAwait(false);
                string responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Microsoft Entra token request failed with status code {(int)response.StatusCode}: {ReadOAuthError(responseBody)}.",
                        null,
                        response.StatusCode);
                }

                using JsonDocument document = JsonDocument.Parse(responseBody);
                if (!document.RootElement.TryGetProperty("access_token", out JsonElement tokenElement) ||
                    string.IsNullOrWhiteSpace(tokenElement.GetString()))
                {
                    throw new InvalidOperationException("Microsoft Entra token response did not contain an access token.");
                }

                return tokenElement.GetString()!;
            },
            logger,
            "Autopilot Graph token acquisition",
            cancellationToken,
            HttpOperationOptions.Metadata with
            {
                MaximumAttempts = checked(options.RetryCount + 1),
                InitialRetryDelay = options.RetryDelay,
                MaximumRetryDelay = options.RetryDelay > HttpOperationOptions.Metadata.MaximumRetryDelay
                    ? options.RetryDelay : HttpOperationOptions.Metadata.MaximumRetryDelay
            }).ConfigureAwait(false);
    }

    internal static string CreateClientAssertion(
        string tokenEndpoint,
        string clientId,
        X509Certificate2 certificate)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string header = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["alg"] = "PS256",
            ["typ"] = "JWT",
            ["x5t#S256"] = Base64UrlEncode(SHA256.HashData(certificate.RawData))
        }, AutopilotGraphJson.Options);
        string payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["aud"] = tokenEndpoint,
            ["exp"] = now + 600,
            ["iss"] = clientId,
            ["jti"] = Guid.NewGuid().ToString("D"),
            ["nbf"] = now,
            ["iat"] = now,
            ["sub"] = clientId
        }, AutopilotGraphJson.Options);

        string unsignedToken = $"{Base64UrlEncode(Encoding.UTF8.GetBytes(header))}.{Base64UrlEncode(Encoding.UTF8.GetBytes(payload))}";
        using RSA? rsa = certificate.GetRSAPrivateKey();
        if (rsa is null)
        {
            throw new InvalidOperationException("The embedded PFX certificate does not expose an RSA private key.");
        }

        byte[] signature = rsa.SignData(
            Encoding.ASCII.GetBytes(unsignedToken),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
        return $"{unsignedToken}.{Base64UrlEncode(signature)}";
    }

    private static string ReadOAuthError(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "No error body was returned";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            string? code = document.RootElement.TryGetProperty("error", out JsonElement error)
                ? error.GetString()
                : null;
            string? description = document.RootElement.TryGetProperty("error_description", out JsonElement descriptionElement)
                ? descriptionElement.GetString()
                : null;
            return string.Join(
                ": ",
                new[] { code, description }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!.Trim()));
        }
        catch (JsonException)
        {
            return responseBody.Length <= 500 ? responseBody : responseBody[..500];
        }
    }

    private static string Base64UrlEncode(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

public sealed record AutopilotGraphTokenServiceOptions
{
    public int RetryCount { get; init; } = HttpRetryPolicy.DefaultRetryCount;
    public TimeSpan RetryDelay { get; init; } = HttpRetryPolicy.DefaultRetryDelay;
}
