// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Foundry.Core.Services.Configuration;
using Foundry.Deploy.Models;
using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.Autopilot;
using Foundry.Deploy.Services.Configuration;
using Foundry.Deploy.Services.Security;

namespace Foundry.Deploy.Services.Deployment.Unattend;

/// <summary>Decrypts bounded media assets into a validated snapshot without exposing parser input in errors.</summary>
public sealed class UnattendContentService(IDeploymentSecretKeySession keySession)
{
    private const int MaximumEnvelopeBytes = 6 * 1024 * 1024;

    /// <summary>Returns a disposable snapshot only after integrity, architecture and enrollment checks pass.</summary>
    public UnattendSnapshot Read(UnattendSelection selection, string? architecture,
        bool isAutopilotEnabled, AutopilotProvisioningMode autopilotMode)
    {
        UnattendSnapshot snapshot = ReadValidated(selection, architecture);
        if (isAutopilotEnabled && autopilotMode != AutopilotProvisioningMode.HardwareHashUpload && snapshot.Inspection.ConflictsWithAutopilot)
        {
            snapshot.Dispose();
            throw new InvalidOperationException("The answer file conflicts with the selected Autopilot enrollment mode.");
        }
        return snapshot;
    }

    private UnattendSnapshot ReadValidated(UnattendSelection selection, string? architecture)
    {
        byte[]? key = null;
        byte[]? content = null;
        try
        {
            string expectedName = UnattendFileService.GetAssetFileName(selection.File.Id);
            if (!string.Equals(Path.GetFileName(selection.AssetPath), expectedName, StringComparison.Ordinal))
            {
                throw new InvalidDataException();
            }
            using var stream = new FileStream(selection.AssetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > MaximumEnvelopeBytes)
            {
                throw new InvalidDataException();
            }
            byte[] envelopeBytes = new byte[(int)stream.Length];
            stream.ReadExactly(envelopeBytes);
            SecretEnvelope envelope = JsonSerializer.Deserialize<SecretEnvelope>(envelopeBytes,
                ConfigurationJsonDefaults.SerializerOptions) ?? throw new InvalidDataException();
            key = keySession.GetKeyCopy();
            content = DeployMediaSecretEnvelopeProtector.DecryptBytes(envelope, key,
                DeployMediaSecretEnvelopeProtector.DeploymentKeyId);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(content)), selection.File.ContentHash,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException();
            }
            UnattendInspection inspection = UnattendFileService.Inspect(content, architecture);
            var snapshot = new UnattendSnapshot(content, inspection);
            content = null;
            return snapshot;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or ArgumentException or FormatException or CryptographicException)
        {
            throw new InvalidDataException("The selected answer file is unavailable, invalid, or incompatible with the selected Windows architecture.");
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (content is not null) CryptographicOperations.ZeroMemory(content);
        }
    }
}
