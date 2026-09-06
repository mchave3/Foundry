// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Utilities.Hardware;
using Foundry.Utilities.Processes;

namespace Foundry.Utilities.Tests.Hardware;

public sealed class WindowsFirmwareInspectorTests
{
    [Theory]
    [InlineData(true, 2, WindowsFirmwareType.Uefi)]
    [InlineData(true, 1, WindowsFirmwareType.Bios)]
    [InlineData(true, 0, WindowsFirmwareType.Unknown)]
    [InlineData(true, 7, WindowsFirmwareType.Unknown)]
    [InlineData(false, 2, WindowsFirmwareType.Unknown)]
    public void Read_RequiresSuccessfulKnownNativeValue(bool succeeds, uint value, WindowsFirmwareType expected)
    {
        Assert.Equal(expected, WindowsFirmwareInspector.Read((out uint firmware) => { firmware = value; return succeeds; }));
    }

    [Theory]
    [InlineData(WindowsFirmwareType.Uefi)]
    [InlineData(WindowsFirmwareType.Bios)]
    [InlineData(WindowsFirmwareType.Unknown)]
    public async Task HardwareSnapshot_PreservesIndependentBootFirmware(WindowsFirmwareType firmware)
    {
        var inspector = new WindowsHardwareInspector((_, _) => Task.FromResult(new ProcessExecutionResult
        {
            ExitCode = 0,
            StandardOutput = "{}"
        }), () => firmware);
        HardwareSnapshot snapshot = await inspector.GetCurrentAsync(TestContext.Current.CancellationToken);
        Assert.Equal(firmware, snapshot.FirmwareType);
    }
}
