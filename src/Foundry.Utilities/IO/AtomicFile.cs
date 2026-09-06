// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text;

namespace Foundry.Utilities.IO;

/// <summary>
/// Publishes complete text files through a uniquely owned sibling file.
/// </summary>
public static class AtomicFile
{
    private static readonly object PublicationLock = new();

    public static void WriteAllText(string destinationPath, string content)
    {
        WriteAllText(destinationPath, content, Publish);
    }

    internal static void WriteAllText(
        string destinationPath,
        string content,
        Action<string, string> publish)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(publish);

        string fullDestinationPath = Path.GetFullPath(destinationPath);
        string? directoryPath = Path.GetDirectoryName(fullDestinationPath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("The destination directory could not be resolved.", nameof(destinationPath));
        }

        string temporaryPath = Path.Combine(
            directoryPath,
            $".{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.tmp");
        Exception? operationException = null;
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            publish(temporaryPath, fullDestinationPath);
        }
        catch (Exception ex)
        {
            operationException = ex;
            throw;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch when (operationException is not null)
            {
                // Preserve the write or publication failure that explains why the destination was not replaced.
            }
        }
    }

    private static void Publish(string temporaryPath, string destinationPath)
    {
        lock (PublicationLock)
        {
            if (File.Exists(destinationPath))
            {
                File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
            }
        }
    }
}
