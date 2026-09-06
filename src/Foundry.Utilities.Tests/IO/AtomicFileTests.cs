// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json;
using Foundry.Utilities.IO;

namespace Foundry.Utilities.Tests.IO;

public sealed class AtomicFileTests
{
    [Fact]
    public void WriteAllText_WhenPublicationFails_PreservesDestinationAndCleansTemporaryFile()
    {
        using var tempDirectory = new TemporaryDirectory();
        string destinationPath = Path.Combine(tempDirectory.Path, "settings.json");
        File.WriteAllText(destinationPath, "old settings");

        Assert.Throws<IOException>(() => AtomicFile.WriteAllText(
            destinationPath,
            "new settings",
            static (_, _) => throw new IOException("Synthetic publication failure.")));

        Assert.Equal("old settings", File.ReadAllText(destinationPath));
        Assert.Equal([destinationPath], Directory.GetFiles(tempDirectory.Path));
    }

    [Fact]
    public void WriteAllText_WhenPublicationSucceeds_ReplacesDestinationCompletely()
    {
        using var tempDirectory = new TemporaryDirectory();
        string destinationPath = Path.Combine(tempDirectory.Path, "settings.json");
        File.WriteAllText(destinationPath, "old settings");
        const string expected = """{"schemaVersion":14,"value":"complete"}""";

        AtomicFile.WriteAllText(destinationPath, expected);

        Assert.Equal(expected, File.ReadAllText(destinationPath));
        Assert.Equal([destinationPath], Directory.GetFiles(tempDirectory.Path));
    }

    [Fact]
    public async Task WriteAllText_WhenWritersRunConcurrently_NeverExposesPartialJson()
    {
        using var tempDirectory = new TemporaryDirectory();
        string destinationPath = Path.Combine(tempDirectory.Path, "settings.json");
        AtomicFile.WriteAllText(destinationPath, """{"writer":-1,"payload":"initial"}""");
        using var cancellation = new CancellationTokenSource();
        var invalidDocuments = new ConcurrentQueue<string>();
        Task reader = Task.Run(() =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                string json;
                try
                {
                    json = ReadAllTextWhileAllowingReplacement(destinationPath);
                }
                catch (IOException)
                {
                    continue;
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(json);
                    _ = document.RootElement.GetProperty("writer").GetInt32();
                    _ = document.RootElement.GetProperty("payload").GetString();
                }
                catch (JsonException)
                {
                    invalidDocuments.Enqueue(json);
                }
            }
        }, TestContext.Current.CancellationToken);

        try
        {
            await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
                AtomicFile.WriteAllText(
                    destinationPath,
                    JsonSerializer.Serialize(new { writer = index, payload = new string((char)('a' + index), 32_768) })))));
        }
        finally
        {
            cancellation.Cancel();
            await reader;
        }

        Assert.Empty(invalidDocuments);
        using JsonDocument finalDocument = JsonDocument.Parse(File.ReadAllText(destinationPath));
        Assert.InRange(finalDocument.RootElement.GetProperty("writer").GetInt32(), 0, 15);
        Assert.Equal([destinationPath], Directory.GetFiles(tempDirectory.Path));
    }

    private static string ReadAllTextWhileAllowingReplacement(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
