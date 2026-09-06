// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Foundry.Deploy.Services.Deployment;
using Foundry.Deploy.Services.Http;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.Autopilot;

/// <summary>
/// Imports hardware hashes through Microsoft Graph and waits for Intune Autopilot visibility.
/// </summary>
public sealed class AutopilotGraphImportClient(
    HttpClient httpClient,
    ILogger<AutopilotGraphImportClient> logger,
    AutopilotGraphImportClientOptions? options = null)
{
    private const string ImportedIdentitiesImportPath = "v1.0/deviceManagement/importedWindowsAutopilotDeviceIdentities/import";
    private const string ImportedIdentitiesPath = "v1.0/deviceManagement/importedWindowsAutopilotDeviceIdentities";
    private const string WindowsAutopilotDevicesPath = "v1.0/deviceManagement/windowsAutopilotDeviceIdentities";
    private const string ODataType = "#microsoft.graph.importedWindowsAutopilotDeviceIdentity";

    private readonly HttpClient httpClient = httpClient;
    private readonly ILogger logger = logger;
    private readonly AutopilotGraphImportClientOptions options = options ?? new AutopilotGraphImportClientOptions();

    public Task<AutopilotHardwareHashUploadResult> ImportHardwareHashAsync(
        AutopilotGraphImportRequest request,
        CancellationToken cancellationToken = default)
    {
        return ImportHardwareHashAsync(request, progress: null, cancellationToken);
    }

    /// <summary>
    /// Lists distinct group tags currently visible on Windows Autopilot devices.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListGroupTagsAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        List<string> groupTags = [];
        string? path = WindowsAutopilotDevicesPath;
        while (!string.IsNullOrWhiteSpace(path))
        {
            GraphCollectionResponse<WindowsAutopilotDeviceIdentity>? response =
                await SendGraphAsync<object, GraphCollectionResponse<WindowsAutopilotDeviceIdentity>>(
                    HttpMethod.Get,
                    path,
                    accessToken,
                    body: null,
                    "Windows Autopilot group tag discovery",
                    cancellationToken).ConfigureAwait(false);

            if (response?.Value is not null)
            {
                foreach (string? groupTag in response.Value.Select(static device => device.GroupTag?.Trim()))
                {
                    if (!string.IsNullOrWhiteSpace(groupTag))
                    {
                        groupTags.Add(groupTag);
                    }
                }
            }

            path = response?.NextLink;
        }

        return groupTags
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<AutopilotHardwareHashUploadResult> ImportHardwareHashAsync(
        AutopilotGraphImportRequest request,
        IProgress<AutopilotHardwareHashUploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        progress?.Report(new AutopilotHardwareHashUploadProgress(
            "Uploading Autopilot hardware hash...",
            "Submitting import request to Microsoft Graph..."));
        ImportedWindowsAutopilotDeviceIdentity importedIdentity = await ImportAsync(request, cancellationToken)
            .ConfigureAwait(false);
        AutopilotImportWaitResult waitResult = await WaitForAutopilotDeviceReadinessAsync(
            request,
            importedIdentity,
            progress,
            cancellationToken)
            .ConfigureAwait(false);
        importedIdentity = waitResult.ImportedIdentity;
        if (waitResult.FailureState is not null)
        {
            return AutopilotHardwareHashUploadResult.Failed(
                waitResult.FailureState.Value,
                waitResult.FailureMessage ?? "Autopilot hardware hash upload did not complete before the timeout.",
                waitResult.FailureCode,
                request.ImportId,
                importedIdentity.Id,
                waitResult.AutopilotDevice?.Id);
        }

        if (waitResult.AutopilotDevice is not null)
        {
            return AutopilotHardwareHashUploadResult.Completed(
                "Autopilot hardware hash imported and visible in Windows Autopilot devices.",
                request.ImportId,
                importedIdentity.Id,
                waitResult.AutopilotDevice.Id);
        }

        string importStatus = importedIdentity.State?.DeviceImportStatus ?? "unknown";
        if (string.Equals(importStatus, "error", StringComparison.OrdinalIgnoreCase))
        {
            string failureCode = importedIdentity.State?.DeviceErrorCode?.ToString() ?? "ImportFailed";
            string deviceErrorName = importedIdentity.State?.DeviceErrorName ?? "ImportFailed";
            return AutopilotHardwareHashUploadResult.Failed(
                AutopilotHardwareHashUploadState.UploadFailed,
                $"Autopilot hardware hash import failed: {deviceErrorName}.",
                failureCode,
                request.ImportId,
                importedIdentity.Id);
        }

        return AutopilotHardwareHashUploadResult.Failed(
            AutopilotHardwareHashUploadState.UploadTimedOut,
            "Imported Autopilot device did not appear in Windows Autopilot devices before the timeout.",
            "AutopilotDeviceTimedOut",
            request.ImportId,
            importedIdentity.Id);
    }

    private async Task<ImportedWindowsAutopilotDeviceIdentity> ImportAsync(
        AutopilotGraphImportRequest request,
        CancellationToken cancellationToken)
    {
        ImportRequestBody body = new([
            new ImportRequestDevice(
                ODataType,
                request.SerialNumber,
                request.HardwareIdentifier,
                request.GroupTag,
                request.AssignedUserPrincipalName,
                request.ImportId)
        ]);

        GraphCollectionResponse<ImportedWindowsAutopilotDeviceIdentity>? response =
            await SendGraphAsync<ImportRequestBody, GraphCollectionResponse<ImportedWindowsAutopilotDeviceIdentity>>(
                HttpMethod.Post,
                ImportedIdentitiesImportPath,
                request.AccessToken,
                body,
                "Autopilot hardware hash import",
                cancellationToken).ConfigureAwait(false);

        return response?.Value?.FirstOrDefault()
            ?? throw new InvalidOperationException("Microsoft Graph did not return an imported Autopilot device identity.");
    }

    private async Task<WindowsAutopilotDeviceIdentity?> ReconcileAutopilotDeviceGroupTagAsync(
        AutopilotGraphImportRequest request,
        WindowsAutopilotDeviceIdentity existingDevice,
        IProgress<AutopilotHardwareHashUploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!ShouldUpdateGroupTag(existingDevice.GroupTag, request.GroupTag))
        {
            return existingDevice;
        }

        if (string.IsNullOrWhiteSpace(existingDevice.Id))
        {
            throw new InvalidOperationException("Microsoft Graph returned an existing Autopilot device without an ID.");
        }

        progress?.Report(new AutopilotHardwareHashUploadProgress(
            "Updating existing Autopilot device...",
            "Updating Windows Autopilot group tag in Microsoft Graph..."));
        await UpdateAutopilotDevicePropertiesAsync(
            request.AccessToken,
            existingDevice.Id,
            request.GroupTag,
            cancellationToken).ConfigureAwait(false);
        return await WaitForAutopilotDeviceGroupTagAsync(request, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateAutopilotDevicePropertiesAsync(
        string accessToken,
        string autopilotDeviceId,
        string? groupTag,
        CancellationToken cancellationToken)
    {
        string path = $"{WindowsAutopilotDevicesPath}/{Uri.EscapeDataString(autopilotDeviceId)}/updateDeviceProperties";
        await SendGraphNoContentAsync(
            HttpMethod.Post,
            path,
            accessToken,
            new UpdateDevicePropertiesRequest(NormalizeGroupTagForGraph(groupTag)),
            "Windows Autopilot device property update",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AutopilotImportWaitResult> WaitForAutopilotDeviceReadinessAsync(
        AutopilotGraphImportRequest request,
        ImportedWindowsAutopilotDeviceIdentity importedIdentity,
        IProgress<AutopilotHardwareHashUploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(options.VisibilityTimeout);
        logger.LogInformation(
            "Waiting up to {Timeout} for Windows Autopilot device visibility. SerialNumber={SerialNumber}, ImportId={ImportId}.",
            options.VisibilityTimeout,
            request.SerialNumber,
            request.ImportId);
        while (true)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            progress?.Report(new AutopilotHardwareHashUploadProgress(
                "Waiting for Autopilot device visibility...",
                $"Checking Windows Autopilot devices ({FormatRemaining(remaining)} remaining)..."));
            AutopilotDeviceLookupResult visibleDeviceLookup = await FindAutopilotDeviceAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (visibleDeviceLookup.FailureState is not null)
            {
                return new AutopilotImportWaitResult(
                    importedIdentity,
                    null,
                    visibleDeviceLookup.FailureState,
                    visibleDeviceLookup.FailureMessage,
                    visibleDeviceLookup.FailureCode);
            }

            if (visibleDeviceLookup.Device is not null)
            {
                logger.LogInformation(
                    "Windows Autopilot device became visible. SerialNumber={SerialNumber}, ImportId={ImportId}, AutopilotDeviceId={AutopilotDeviceId}.",
                    request.SerialNumber,
                    request.ImportId,
                    visibleDeviceLookup.Device.Id);
                WindowsAutopilotDeviceIdentity? reconciledDevice = await ReconcileAutopilotDeviceGroupTagAsync(
                    request,
                    visibleDeviceLookup.Device,
                    progress,
                    cancellationToken)
                    .ConfigureAwait(false);
                if (reconciledDevice is null)
                {
                    return new AutopilotImportWaitResult(
                        importedIdentity,
                        visibleDeviceLookup.Device,
                        AutopilotHardwareHashUploadState.UploadTimedOut,
                        "Windows Autopilot device group tag update was not confirmed before the timeout.",
                        "AutopilotGroupTagUpdateTimedOut");
                }

                return new AutopilotImportWaitResult(importedIdentity, reconciledDevice);
            }

            bool importError = IsImportError(importedIdentity.State?.DeviceImportStatus);
            if (importError && !ShouldContinueVisibilityWaitAfterImportError(importedIdentity.State))
            {
                logger.LogWarning(
                    "Autopilot import entered error state before device visibility. SerialNumber={SerialNumber}, ImportId={ImportId}, ImportedIdentityId={ImportedIdentityId}, ErrorCode={ErrorCode}, ErrorName={ErrorName}.",
                    request.SerialNumber,
                    request.ImportId,
                    importedIdentity.Id,
                    importedIdentity.State?.DeviceErrorCode,
                    importedIdentity.State?.DeviceErrorName);
                return new AutopilotImportWaitResult(importedIdentity, null);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                logger.LogWarning(
                    "Windows Autopilot device visibility timed out. SerialNumber={SerialNumber}, ImportId={ImportId}, ImportedIdentityId={ImportedIdentityId}, ImportStatus={ImportStatus}.",
                    request.SerialNumber,
                    request.ImportId,
                    importedIdentity.Id,
                    importedIdentity.State?.DeviceImportStatus);
                return new AutopilotImportWaitResult(
                    importedIdentity,
                    null,
                    AutopilotHardwareHashUploadState.UploadTimedOut,
                    "Imported Autopilot device did not appear in Windows Autopilot devices before the timeout.",
                    "AutopilotDeviceTimedOut");
            }

            if (!importError && !IsImportComplete(importedIdentity.State?.DeviceImportStatus))
            {
                ImportedWindowsAutopilotDeviceIdentity? current = await GetImportedIdentityAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                if (current is not null)
                {
                    importedIdentity = current;
                }

            }

            TimeSpan delay = deadline - DateTimeOffset.UtcNow;
            if (delay > options.PollInterval)
            {
                delay = options.PollInterval;
            }

            await DelayWithProgressAsync(
                deadline,
                delay,
                progress,
                "Waiting for Autopilot device visibility...",
                remainingTime => $"Checking Windows Autopilot devices ({FormatRemaining(remainingTime)} remaining)...",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<WindowsAutopilotDeviceIdentity?> WaitForAutopilotDeviceGroupTagAsync(
        AutopilotGraphImportRequest request,
        IProgress<AutopilotHardwareHashUploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(options.VisibilityTimeout);
        logger.LogInformation(
            "Waiting up to {Timeout} for Windows Autopilot group tag update. SerialNumber={SerialNumber}, ImportId={ImportId}, GroupTag={GroupTag}.",
            options.VisibilityTimeout,
            request.SerialNumber,
            request.ImportId,
            NormalizeGroupTagForGraph(request.GroupTag));
        while (true)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            progress?.Report(new AutopilotHardwareHashUploadProgress(
                "Waiting for Autopilot group tag update...",
                $"Checking Windows Autopilot group tag ({FormatRemaining(remaining)} remaining)..."));
            AutopilotDeviceLookupResult deviceLookup = await FindAutopilotDeviceAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (deviceLookup.FailureState is not null)
            {
                logger.LogWarning(
                    "Windows Autopilot group tag update could not be verified because device lookup failed. SerialNumber={SerialNumber}, ImportId={ImportId}, FailureCode={FailureCode}.",
                    request.SerialNumber,
                    request.ImportId,
                    deviceLookup.FailureCode);
                return null;
            }

            if (deviceLookup.Device is not null && !ShouldUpdateGroupTag(deviceLookup.Device.GroupTag, request.GroupTag))
            {
                logger.LogInformation(
                    "Windows Autopilot group tag update confirmed. SerialNumber={SerialNumber}, ImportId={ImportId}, AutopilotDeviceId={AutopilotDeviceId}.",
                    request.SerialNumber,
                    request.ImportId,
                    deviceLookup.Device.Id);
                return deviceLookup.Device;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                logger.LogWarning(
                    "Windows Autopilot group tag update timed out. SerialNumber={SerialNumber}, ImportId={ImportId}, ExpectedGroupTag={ExpectedGroupTag}.",
                    request.SerialNumber,
                    request.ImportId,
                    NormalizeGroupTagForGraph(request.GroupTag));
                return null;
            }

            TimeSpan delay = deadline - DateTimeOffset.UtcNow;
            if (delay > options.PollInterval)
            {
                delay = options.PollInterval;
            }

            await DelayWithProgressAsync(
                deadline,
                delay,
                progress,
                "Waiting for Autopilot group tag update...",
                remainingTime => $"Checking Windows Autopilot group tag ({FormatRemaining(remainingTime)} remaining)...",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ImportedWindowsAutopilotDeviceIdentity?> GetImportedIdentityAsync(
        AutopilotGraphImportRequest request,
        CancellationToken cancellationToken)
    {
        string filter = EscapeODataFilter($"importId eq '{EscapeODataString(request.ImportId)}'");
        string? path = $"{ImportedIdentitiesPath}?$filter={filter}";
        while (!string.IsNullOrWhiteSpace(path))
        {
            GraphCollectionResponse<ImportedWindowsAutopilotDeviceIdentity>? response =
                await SendGraphAsync<object, GraphCollectionResponse<ImportedWindowsAutopilotDeviceIdentity>>(
                    HttpMethod.Get,
                    path,
                    request.AccessToken,
                    body: null,
                    "Autopilot import status polling",
                    cancellationToken).ConfigureAwait(false);

            ImportedWindowsAutopilotDeviceIdentity? identity = response?.Value?.FirstOrDefault(candidate =>
                string.Equals(candidate.ImportId, request.ImportId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.SerialNumber, request.SerialNumber, StringComparison.OrdinalIgnoreCase));
            if (identity is not null)
            {
                return identity;
            }

            path = response?.NextLink;
        }

        return null;
    }

    private async Task<AutopilotDeviceLookupResult> FindAutopilotDeviceAsync(
        AutopilotGraphImportRequest request,
        CancellationToken cancellationToken)
    {
        List<WindowsAutopilotDeviceIdentity> matches = [];
        string? path = WindowsAutopilotDevicesPath;
        while (!string.IsNullOrWhiteSpace(path))
        {
            GraphCollectionResponse<WindowsAutopilotDeviceIdentity>? response =
                await SendGraphAsync<object, GraphCollectionResponse<WindowsAutopilotDeviceIdentity>>(
                    HttpMethod.Get,
                    path,
                    request.AccessToken,
                    body: null,
                    "Windows Autopilot device visibility scan",
                    cancellationToken).ConfigureAwait(false);

            matches.AddRange(FindDevicesBySerialNumber(response, request.SerialNumber));

            path = response?.NextLink;
        }

        if (matches.Count == 0)
        {
            return new AutopilotDeviceLookupResult(null);
        }

        if (matches.Count == 1)
        {
            return new AutopilotDeviceLookupResult(matches[0]);
        }

        logger.LogWarning(
            "Multiple Windows Autopilot devices matched the captured serial number. SerialNumber={SerialNumber}, MatchCount={MatchCount}.",
            request.SerialNumber,
            matches.Count);
        return AutopilotDeviceLookupResult.Failed(
            AutopilotHardwareHashUploadState.UploadFailed,
            "Multiple Windows Autopilot devices matched the captured serial number; group tag reconciliation was skipped to avoid updating the wrong device.",
            "AutopilotDeviceAmbiguous");
    }

    private static IEnumerable<WindowsAutopilotDeviceIdentity> FindDevicesBySerialNumber(
        GraphCollectionResponse<WindowsAutopilotDeviceIdentity>? response,
        string serialNumber)
    {
        return response?.Value?.Where(device =>
            string.Equals(device.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase)) ?? [];
    }

    private async Task<TResponse?> SendGraphAsync<TBody, TResponse>(
        HttpMethod method,
        string path,
        string accessToken,
        TBody? body,
        string operationName,
        CancellationToken cancellationToken)
        where TBody : class
    {
        return await HttpRetryPolicy.ExecuteAsync(
            async ct =>
            {
                using var request = new HttpRequestMessage(method, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (body is not null)
                {
                    request.Content = JsonContent.Create(body, options: AutopilotGraphJson.Options);
                }

                using HttpResponseMessage response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    string graphError = await ReadGraphErrorAsync(response, ct).ConfigureAwait(false);
                    throw new HttpRequestException(
                        string.IsNullOrWhiteSpace(graphError)
                            ? $"Microsoft Graph request failed with status code {(int)response.StatusCode}."
                            : $"Microsoft Graph request failed with status code {(int)response.StatusCode}: {graphError}.",
                        null,
                        response.StatusCode);
                }

                await using Stream responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<TResponse>(
                    responseStream,
                    AutopilotGraphJson.Options,
                    ct).ConfigureAwait(false);
            },
            logger,
            operationName,
            cancellationToken,
            HttpOperationOptions.Metadata with
            {
                MaximumAttempts = checked(options.RetryCount + 1),
                InitialRetryDelay = options.RetryDelay,
                MaximumRetryDelay = options.RetryDelay > HttpOperationOptions.Metadata.MaximumRetryDelay
                    ? options.RetryDelay : HttpOperationOptions.Metadata.MaximumRetryDelay
            }).ConfigureAwait(false);
    }

    private async Task SendGraphNoContentAsync<TBody>(
        HttpMethod method,
        string path,
        string accessToken,
        TBody? body,
        string operationName,
        CancellationToken cancellationToken)
        where TBody : class
    {
        await HttpRetryPolicy.ExecuteAsync(
            async ct =>
            {
                using var request = new HttpRequestMessage(method, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (body is not null)
                {
                    request.Content = JsonContent.Create(body, options: AutopilotGraphJson.Options);
                }

                using HttpResponseMessage response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    string graphError = await ReadGraphErrorAsync(response, ct).ConfigureAwait(false);
                    throw new HttpRequestException(
                        string.IsNullOrWhiteSpace(graphError)
                            ? $"Microsoft Graph request failed with status code {(int)response.StatusCode}."
                            : $"Microsoft Graph request failed with status code {(int)response.StatusCode}: {graphError}.",
                        null,
                        response.StatusCode);
                }
            },
            logger,
            operationName,
            cancellationToken,
            HttpOperationOptions.Metadata with
            {
                MaximumAttempts = checked(options.RetryCount + 1),
                InitialRetryDelay = options.RetryDelay,
                MaximumRetryDelay = options.RetryDelay > HttpOperationOptions.Metadata.MaximumRetryDelay
                    ? options.RetryDelay : HttpOperationOptions.Metadata.MaximumRetryDelay
            }).ConfigureAwait(false);
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return delay <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(delay, cancellationToken);
    }

    private async Task DelayWithProgressAsync(
        DateTimeOffset deadline,
        TimeSpan delay,
        IProgress<AutopilotHardwareHashUploadProgress>? progress,
        string message,
        Func<TimeSpan, string> detailFactory,
        CancellationToken cancellationToken)
    {
        TimeSpan remainingDelay = delay;
        TimeSpan progressInterval = options.ProgressInterval > TimeSpan.Zero
            ? options.ProgressInterval
            : TimeSpan.FromSeconds(1);
        while (remainingDelay > TimeSpan.Zero)
        {
            TimeSpan currentDelay = remainingDelay > progressInterval
                ? progressInterval
                : remainingDelay;
            await DelayAsync(currentDelay, cancellationToken).ConfigureAwait(false);
            remainingDelay -= currentDelay;

            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            progress?.Report(new AutopilotHardwareHashUploadProgress(message, detailFactory(remaining)));
        }
    }

    private static bool IsImportError(string? status)
    {
        return string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImportComplete(string? status)
    {
        return string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldContinueVisibilityWaitAfterImportError(ImportedWindowsAutopilotDeviceIdentityState? state)
    {
        string errorName = state?.DeviceErrorName ?? string.Empty;
        return errorName.Contains("AlreadyAssigned", StringComparison.OrdinalIgnoreCase) ||
               errorName.Contains("AlreadyExists", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldUpdateGroupTag(string? currentGroupTag, string? requestedGroupTag)
    {
        return !string.Equals(
            NormalizeGroupTagForComparison(currentGroupTag),
            NormalizeGroupTagForComparison(requestedGroupTag),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeGroupTagForComparison(string? groupTag)
    {
        return string.IsNullOrWhiteSpace(groupTag)
            ? string.Empty
            : groupTag.Trim();
    }

    private static string NormalizeGroupTagForGraph(string? groupTag)
    {
        return NormalizeGroupTagForComparison(groupTag);
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        double seconds = Math.Ceiling(remaining.TotalSeconds);
        return seconds == 1
            ? "1 second"
            : $"{seconds:0} seconds";
    }

    private static string EscapeODataFilter(string filter)
    {
        return Uri.EscapeDataString(filter);
    }

    private static string EscapeODataString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static async Task<string> ReadGraphErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                string? code = error.TryGetProperty("code", out JsonElement codeElement)
                    ? codeElement.GetString()
                    : null;
                string? message = error.TryGetProperty("message", out JsonElement messageElement)
                    ? messageElement.GetString()
                    : null;
                return string.Join(
                    ": ",
                    new[] { code, message }
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value!.Trim()));
            }
        }
        catch (JsonException)
        {
        }

        return body.Length <= 500 ? body : body[..500];
    }

    private sealed record ImportRequestBody(
        IReadOnlyList<ImportRequestDevice> ImportedWindowsAutopilotDeviceIdentities);

    private sealed record ImportRequestDevice(
        [property: JsonPropertyName("@odata.type")] string ODataType,
        string SerialNumber,
        string HardwareIdentifier,
        string? GroupTag,
        string? AssignedUserPrincipalName,
        string ImportId);

    private sealed record UpdateDevicePropertiesRequest(string GroupTag);
}

/// <summary>
/// Controls Graph retry and polling bounds for the Autopilot import workflow.
/// </summary>
public sealed record AutopilotGraphImportClientOptions
{
    public int RetryCount { get; init; } = HttpRetryPolicy.DefaultRetryCount;
    public TimeSpan RetryDelay { get; init; } = HttpRetryPolicy.DefaultRetryDelay;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan VisibilityTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed record AutopilotGraphImportRequest(
    string AccessToken,
    string SerialNumber,
    string HardwareIdentifier,
    string? GroupTag,
    string? AssignedUserPrincipalName,
    string ImportId);

internal sealed record GraphCollectionResponse<TItem>
{
    public List<TItem>? Value { get; init; }

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; init; }
}

internal sealed record ImportedWindowsAutopilotDeviceIdentity
{
    public string? Id { get; init; }
    public string? SerialNumber { get; init; }
    public string? ImportId { get; init; }
    public ImportedWindowsAutopilotDeviceIdentityState? State { get; init; }
}

internal sealed record ImportedWindowsAutopilotDeviceIdentityState
{
    public string? DeviceImportStatus { get; init; }
    public int? DeviceErrorCode { get; init; }
    public string? DeviceErrorName { get; init; }
}

internal sealed record WindowsAutopilotDeviceIdentity
{
    public string? Id { get; init; }
    public string? SerialNumber { get; init; }
    public string? GroupTag { get; init; }
}

internal sealed record AutopilotImportWaitResult(
    ImportedWindowsAutopilotDeviceIdentity ImportedIdentity,
    WindowsAutopilotDeviceIdentity? AutopilotDevice,
    AutopilotHardwareHashUploadState? FailureState = null,
    string? FailureMessage = null,
    string? FailureCode = null);

internal sealed record AutopilotDeviceLookupResult(
    WindowsAutopilotDeviceIdentity? Device,
    AutopilotHardwareHashUploadState? FailureState = null,
    string? FailureMessage = null,
    string? FailureCode = null)
{
    public static AutopilotDeviceLookupResult Failed(
        AutopilotHardwareHashUploadState state,
        string message,
        string failureCode)
    {
        return new AutopilotDeviceLookupResult(null, state, message, failureCode);
    }
}

internal static class AutopilotGraphJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
