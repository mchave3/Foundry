// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Utilities.Hardware;
using Foundry.Utilities.Processes;
using Foundry.Utilities.Storage;

namespace Foundry.Utilities.Tests.Processes;

public sealed class ProcessOutputInspectionTests
{
    [Theory]
    [InlineData("disks")]
    [InlineData("path")]
    [InlineData("hardware")]
    public async Task Inspection_WhenTruncatedSuffixIsValidJson_RejectsIncompleteMetadata(string inspection)
    {
        ProcessExecutionRequest? capturedRequest = null;
        Task<ProcessExecutionResult> Execute(ProcessExecutionRequest request, CancellationToken cancellationToken)
        {
            capturedRequest = request;
            return Task.FromResult(new ProcessExecutionResult
            {
                ExitCode = 0,
                StandardOutput = inspection switch
                {
                    "disks" => """{"Number":9,"IsBoot":false,"IsSystem":false,"IsReadOnly":false,"IsOffline":false}""",
                    "path" => """{"DiskNumber":9}""",
                    _ => """{"Manufacturer":"Tail vendor","Model":"Tail model"}"""
                },
                StandardOutputTruncated = true
            });
        }

        Func<Task> inspect = inspection switch
        {
            "disks" => async () => await new WindowsDiskInspector(Execute).GetDisksAsync(TestContext.Current.CancellationToken),
            "path" => async () => await new WindowsDiskInspector(Execute).ResolveDiskNumberForPathAsync(@"C:\Windows", TestContext.Current.CancellationToken),
            _ => async () => await new WindowsHardwareInspector(Execute, () => WindowsFirmwareType.Uefi)
                .GetCurrentAsync(TestContext.Current.CancellationToken)
        };

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(inspect);

        Assert.Contains("capture limit", error.Message, StringComparison.Ordinal);
        Assert.NotNull(capturedRequest);
        Assert.Equal(TimeSpan.FromMinutes(1), capturedRequest.ExecutionTimeout);
    }
}
