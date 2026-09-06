// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text;
using Foundry.Utilities.Processes;
using Foundry.Utilities.Storage;

namespace Foundry.Utilities.Tests.Storage;

public sealed class WindowsDiskInspectorTests
{
    [Fact]
    public async Task GetDisksAsync_ParsesRawDiskFacts()
    {
        ProcessExecutionRequest? capturedRequest = null;
        var inspector = new WindowsDiskInspector((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(CreateSuccessResult(
                """
                {
                  "Number":"2",
                  "FriendlyName":" NVMe Disk ",
                  "UniqueId":" UNIQUE-2 ",
                  "SerialNumber":" SERIAL-2 ",
                  "BusType":" NVMe ",
                  "PartitionStyle":" GPT ",
                  "Size":"1073741824",
                  "IsSystem":"true",
                  "IsBoot":false,
                  "IsReadOnly":true,
                  "IsOffline":"false",
                  "IsRemovable":false
                }
                """));
        });

        DiskInfo disk = Assert.Single(
            await inspector.GetDisksAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, disk.Number);
        Assert.Equal("NVMe Disk", disk.FriendlyName);
        Assert.Equal("UNIQUE-2", disk.UniqueId);
        Assert.Equal("SERIAL-2", disk.SerialNumber);
        Assert.Equal("NVMe", disk.BusType);
        Assert.Equal("GPT", disk.PartitionStyle);
        Assert.Equal(1073741824UL, disk.SizeBytes);
        Assert.True(disk.IsSystem);
        Assert.False(disk.IsBoot);
        Assert.True(disk.IsReadOnly);
        Assert.False(disk.IsOffline);
        Assert.False(disk.IsRemovable);

        Assert.NotNull(capturedRequest);
        Assert.Equal("powershell.exe", capturedRequest.FileName);
        Assert.Equal("-NoProfile", capturedRequest.ArgumentList?[0]);
        IReadOnlyList<string> arguments = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            capturedRequest.ArgumentList);
        int encodedCommandIndex = arguments.ToList().IndexOf("-EncodedCommand");
        Assert.True(encodedCommandIndex >= 0);
        string script = Encoding.Unicode.GetString(
            Convert.FromBase64String(arguments[encodedCommandIndex + 1]));
        Assert.Contains("Get-Disk", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDisksAsync_WhenPayloadIsArray_PreservesSourceOrder()
    {
        var inspector = CreateInspector(
            """
            [
              {"Number":3,"IsSystem":false,"IsBoot":false,"IsReadOnly":false,"IsOffline":false},
              {"Number":1,"IsSystem":false,"IsBoot":false,"IsReadOnly":false,"IsOffline":false}
            ]
            """);

        IReadOnlyList<DiskInfo> disks = await inspector.GetDisksAsync(TestContext.Current.CancellationToken);

        Assert.Equal([3, 1], disks.Select(static disk => disk.Number));
    }

    [Fact]
    public async Task GetDisksAsync_WhenPayloadIsEmptyArray_ReturnsEmptyList()
    {
        var inspector = CreateInspector("[]");

        IReadOnlyList<DiskInfo> disks = await inspector.GetDisksAsync(TestContext.Current.CancellationToken);

        Assert.Empty(disks);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "query failed")]
    public async Task GetDisksAsync_WhenQueryReturnsNoUsableData_ThrowsInvalidDataException(
        int exitCode,
        string standardOutput)
    {
        var inspector = new WindowsDiskInspector((_, _) => Task.FromResult(
            new ProcessExecutionResult { ExitCode = exitCode, StandardOutput = standardOutput }));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => inspector.GetDisksAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetDisksAsync_WhenPayloadIsMalformed_ThrowsInvalidDataException()
    {
        var inspector = CreateInspector("{");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => inspector.GetDisksAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{\"Number\":-1,\"IsSystem\":false,\"IsBoot\":false,\"IsReadOnly\":false,\"IsOffline\":false}")]
    [InlineData("{\"Number\":1,\"IsBoot\":false,\"IsReadOnly\":false,\"IsOffline\":false}")]
    [InlineData("{\"Number\":1,\"IsSystem\":\"invalid\",\"IsBoot\":false,\"IsReadOnly\":false,\"IsOffline\":false}")]
    public async Task GetDisksAsync_WhenSafetyFactsAreInvalid_ThrowsInvalidDataException(string json)
    {
        var inspector = CreateInspector(json);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => inspector.GetDisksAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveDiskNumberForPathAsync_ParsesNumericString()
    {
        ProcessExecutionRequest? capturedRequest = null;
        var inspector = new WindowsDiskInspector((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(CreateSuccessResult("""{"DiskNumber":"4"}"""));
        });

        int? diskNumber = await inspector.ResolveDiskNumberForPathAsync(
            "X:\\Foundry\\Runtime",
            TestContext.Current.CancellationToken);

        Assert.Equal(4, diskNumber);
        Assert.NotNull(capturedRequest);
        IReadOnlyList<string> arguments = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            capturedRequest.ArgumentList);
        int encodedCommandIndex = arguments.ToList().IndexOf("-EncodedCommand");
        string script = Encoding.Unicode.GetString(
            Convert.FromBase64String(arguments[encodedCommandIndex + 1]));
        Assert.Contains("Get-Partition -DriveLetter 'X'", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveDiskNumberForPathAsync_WhenPartitionIsNotFound_ReturnsNull()
    {
        var inspector = CreateInspector("{}");

        int? diskNumber = await inspector.ResolveDiskNumberForPathAsync(
            "X:\\Foundry",
            TestContext.Current.CancellationToken);

        Assert.Null(diskNumber);
    }

    [Fact]
    public async Task ResolveDiskNumberForPathAsync_WhenQueryReturnsBlankOutput_ReturnsNull()
    {
        var inspector = CreateInspector(string.Empty);

        int? diskNumber = await inspector.ResolveDiskNumberForPathAsync(
            "X:\\Foundry",
            TestContext.Current.CancellationToken);

        Assert.Null(diskNumber);
    }

    [Fact]
    public async Task ResolveDiskNumberForPathAsync_WhenPayloadIsMalformed_ThrowsInvalidDataException()
    {
        var inspector = CreateInspector("{");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => inspector.ResolveDiskNumberForPathAsync(
                "X:\\Foundry",
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative\\path")]
    [InlineData("x:relative")]
    [InlineData("\\\\server\\share\\folder")]
    [InlineData("\\\\?\\Volume{00000000-0000-0000-0000-000000000000}\\folder")]
    public async Task ResolveDiskNumberForPathAsync_WhenPathHasNoDrive_ReturnsNullWithoutExecution(string path)
    {
        bool executed = false;
        var inspector = new WindowsDiskInspector((_, _) =>
        {
            executed = true;
            return Task.FromResult(CreateSuccessResult("""{"DiskNumber":1}"""));
        });

        int? diskNumber = await inspector.ResolveDiskNumberForPathAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.Null(diskNumber);
        Assert.False(executed);
    }

    [Fact]
    public async Task GetDisksAsync_WhenExecutionFailsUnexpectedly_PropagatesFailure()
    {
        var inspector = new WindowsDiskInspector((_, _) => Task.FromException<ProcessExecutionResult>(
            new ApplicationException("Unexpected failure.")));

        await Assert.ThrowsAsync<ApplicationException>(
            () => inspector.GetDisksAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetDisksAsync_WhenExecutionIsCanceled_PropagatesCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var inspector = new WindowsDiskInspector(
            (_, cancellationToken) => Task.FromCanceled<ProcessExecutionResult>(cancellationToken));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => inspector.GetDisksAsync(cancellationSource.Token));
    }

    private static WindowsDiskInspector CreateInspector(string json)
    {
        return new WindowsDiskInspector((_, _) => Task.FromResult(CreateSuccessResult(json)));
    }

    private static ProcessExecutionResult CreateSuccessResult(string json)
    {
        return new ProcessExecutionResult
        {
            ExitCode = 0,
            StandardOutput = json
        };
    }
}
