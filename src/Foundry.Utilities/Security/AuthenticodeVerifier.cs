// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Text.Json;
using Foundry.Utilities.Processes;

namespace Foundry.Utilities.Security;

/// <summary>Verifies Windows Authenticode trust without executing the inspected payload.</summary>
public static class AuthenticodeVerifier
{
    /// <summary>Requires a valid signature from an exact expected certificate subject.</summary>
    /// <remarks>The caller must hold a read handle with FileShare.Read from before verification through native process completion.</remarks>
    public static Task VerifyAsync(string filePath, IReadOnlySet<string> expectedPublisherSubjects, CancellationToken cancellationToken = default)
        => VerifyAsync(filePath, expectedPublisherSubjects, new ProcessRunner().RunAsync, cancellationToken);

    internal static async Task VerifyAsync(
        string filePath,
        IReadOnlySet<string> expectedPublisherSubjects,
        Func<ProcessExecutionRequest, CancellationToken, Task<ProcessExecutionResult>> run,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(expectedPublisherSubjects);
        ArgumentNullException.ThrowIfNull(run);
        if (expectedPublisherSubjects.Count == 0 || expectedPublisherSubjects.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one exact publisher certificate subject is required.", nameof(expectedPublisherSubjects));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Authenticode verification requires Windows.");
        }

        string fullPath = Path.GetFullPath(filePath);
        string encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(fullPath));
        string script = "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); $ErrorActionPreference = 'Stop'; " +
            $"$path = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedPath}')); " +
            "$signature = Microsoft.PowerShell.Security\\Get-AuthenticodeSignature -LiteralPath $path; " +
            "@{ Status = $signature.Status.ToString(); Subject = $signature.SignerCertificate.Subject } | ConvertTo-Json -Compress";
        string powershellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var request = new ProcessExecutionRequest(powershellPath,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
            Path.GetDirectoryName(fullPath)!)
        {
            ExecutionTimeout = TimeSpan.FromMinutes(2),
            MaxCapturedOutputCharacters = 16_384
        };
        ProcessExecutionResult result = await run(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        result.EnsureCompleteOutput();
        if (!result.IsSuccess || !string.IsNullOrWhiteSpace(result.StandardError))
        {
            throw new InvalidDataException("The Windows Authenticode trust check failed.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("Status", out JsonElement status) || status.ValueKind != JsonValueKind.String ||
                !string.Equals(status.GetString(), "Valid", StringComparison.Ordinal) ||
                !root.TryGetProperty("Subject", out JsonElement subject) || subject.ValueKind != JsonValueKind.String ||
                !expectedPublisherSubjects.Any(expected => string.Equals(expected, subject.GetString(), StringComparison.Ordinal)))
            {
                throw new InvalidDataException("The package does not have a valid signature from an expected publisher.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The Windows Authenticode trust check returned invalid metadata.", ex);
        }
    }
}
