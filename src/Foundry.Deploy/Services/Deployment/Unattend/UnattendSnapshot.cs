// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Security.Cryptography;
using Foundry.Core.Services.Configuration;

namespace Foundry.Deploy.Services.Deployment.Unattend;

/// <summary>Owns the exact validated bytes until staging; disposing clears the credential-bearing buffer.</summary>
public sealed class UnattendSnapshot : IDisposable
{
    private byte[]? _content;

    internal UnattendSnapshot(byte[] content, UnattendInspection inspection)
    {
        _content = content;
        Inspection = inspection;
    }

    /// <summary>Gets structural compatibility information without exposing XML content.</summary>
    public UnattendInspection Inspection { get; }

    /// <summary>Atomically stages the validated bytes, without reading the media asset again.</summary>
    public async Task StageAsync(string windowsPartitionRoot, CancellationToken cancellationToken)
    {
        byte[] content = _content ?? throw new InvalidOperationException("The answer-file snapshot is no longer available.");
        string directory = Path.Combine(windowsPartitionRoot, "Windows", "Panther");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "unattend.xml");
        string temporaryPath = Path.Combine(directory, ".foundry-unattend-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    /// <summary>Clears the owned plaintext, including when deployment ends before staging.</summary>
    public void Dispose()
    {
        byte[]? content = Interlocked.Exchange(ref _content, null);
        if (content is not null) CryptographicOperations.ZeroMemory(content);
    }
}
