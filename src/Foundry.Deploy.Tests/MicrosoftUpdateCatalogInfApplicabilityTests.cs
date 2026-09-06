// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;
using Foundry.Deploy.Services.DriverPacks;

namespace Foundry.Deploy.Tests;

public sealed class MicrosoftUpdateCatalogInfApplicabilityTests
{
    private const string DeviceId = @"PCI\VEN_1234&DEV_5678&SUBSYS_00011234";
    private const string FirmwareId = @"UEFI\RES_{6bd4efb9-23cc-4b4a-ac37-016517413e9a}";
    private const string FirmwareGuid = "{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}";
    private static readonly OperatingSystemCatalogItem Target = new() { Architecture = "x64", BuildMajor = 26100, WindowsRelease = "11", ReleaseId = "24H2" };

    [Theory]
    [InlineData("NTamd64", true)]
    [InlineData("NTamd64.10.0", true)]
    [InlineData("NTamd64.10.0...22000", true)]
    [InlineData("NTamd64.10.0...26100", true)]
    [InlineData("NTamd64.10.0...26200", false)]
    [InlineData("NTamd64.10.0.1..26100", true)]
    [InlineData("NTamd64.10.0.0x1..26100", true)]
    [InlineData("NTamd64.10.0.3..26100", false)]
    [InlineData("NTamd64.10.0..0x80.26100", false)]
    [InlineData("NTamd64.11.0", false)]
    [InlineData("NTamd64.10.1", false)]
    [InlineData("NTamd64.6.1", true)]
    [InlineData("NTarm64.10.0...22000", false)]
    [InlineData("NTx86.10.0...22000", false)]
    [InlineData("NT.10.0...22000", false)]
    [InlineData("NTamd64.10.0...invalid", false)]
    [InlineData("NTamd64.10.0...26100.extra", false)]
    public void TargetDecoration_MustProveArchitectureClientVersionAndBuild(string decoration, bool expected)
    {
        Assert.Equal(expected, MicrosoftUpdateCatalogInfApplicability.IsApplicable(Inf(decoration), Target, [DeviceId]));
    }

    [Fact]
    public void HighestApplicableSection_CanRemoveSupportPresentInEarlierSection()
    {
        string text = Inf("NTamd64.10.0...22000").Replace("Models,NTamd64.10.0...22000",
            "Models,NTamd64.10.0...22000,NTamd64.10.0...26100") + "\n[Models.NTamd64.10.0...26100]\n; intentionally empty";
        Assert.False(MicrosoftUpdateCatalogInfApplicability.IsApplicable(text, Target, [DeviceId]));
        Assert.True(MicrosoftUpdateCatalogInfApplicability.IsApplicable(text, Target with { BuildMajor = 22631 }, [DeviceId]));
    }

    [Fact]
    public void ManufacturerLines_AreSelectedIndependently()
    {
        string text = Inf("NTamd64.10.0...22000").Replace("Maker=Models,NTamd64.10.0...22000",
            "Maker=Other,NTamd64.10.0...26100\nMaker=Models,NTamd64.10.0...22000") + "\n[Other.NTamd64.10.0...26100]\n";
        Assert.True(MicrosoftUpdateCatalogInfApplicability.IsApplicable(text, Target, [DeviceId]));
    }

    [Theory]
    [InlineData(@"PCI\VEN_1234&DEV_5678")]
    [InlineData(@"PCI\VEN_1234&DEV_5678&SUBSYS_00021234")]
    [InlineData(@"PCI\VEN_1234&DEV_5678&SUBSYS_000112340")]
    public void NearOrLessSpecificHardwareId_DoesNotMatchProvidedSubsystem(string otherId)
    {
        Assert.False(MicrosoftUpdateCatalogInfApplicability.IsApplicable(Inf("NTamd64", otherId), Target, [DeviceId]));
    }

    [Fact]
    public void HardwareMatch_UsesCaseInsensitiveExactModelOrCompatibleId()
    {
        Assert.True(MicrosoftUpdateCatalogInfApplicability.IsApplicable(Inf("NTamd64", @"PCI\OTHER," + DeviceId.ToLowerInvariant()), Target, [DeviceId]));
    }

    [Fact]
    public void QuotedFieldsCommentsContinuationAndStringTokens_AreResolved()
    {
        string text = """
            [Version]
            Signature="$WINDOWS NT$"
            Class=Net
            [Manufacturer]
            %maker%=%modelSection%,\ ; continue decoration
               NTarm64.10.0...22000
            [Models.NTarm64.10.0...22000]
            "Device, with ; punctuation" = Install, "%device%" ; ignored id
            [Strings]
            maker="Example; vendor"
            modelSection="Models"
            device="USB\VID_1234&PID_5678"
            """;
        Assert.True(MicrosoftUpdateCatalogInfApplicability.IsApplicable(text, Target with { Architecture = "ARM64" }, [@"USB\VID_1234&PID_5678"]));
    }

    [Fact]
    public void IdInCommentOrUnreferencedSection_IsNotEvidence()
    {
        string text = Inf("NTamd64", @"PCI\OTHER") + $"\n; {DeviceId}\n[Unreferenced.NTamd64]\nDevice=Install,{DeviceId}\n[Strings]\nId={DeviceId}";
        Assert.False(MicrosoftUpdateCatalogInfApplicability.IsApplicable(text, Target, [DeviceId]));
    }

    [Fact]
    public void LocalizedHardwareToken_CannotChangeApplicabilityProof()
    {
        string text = Inf("NTamd64", "%hardware%") + $"\n[Strings]\nhardware={DeviceId}\n[Strings.040C]\nhardware=PCI\\OTHER";
        Assert.False(MicrosoftUpdateCatalogInfApplicability.IsApplicable(text, Target, [DeviceId]));
    }

    [Fact]
    public void MissingLocalizedHardwareToken_DoesNotFallBackToBaseStrings()
    {
        string text = Inf("NTamd64", "%hardware%") + $"\n[Strings]\nhardware={DeviceId}\n[Strings.040C]\ndescription=Peripheral";
        Assert.False(MicrosoftUpdateCatalogInfApplicability.IsApplicable(text, Target, [DeviceId]));
    }

    [Fact]
    public void LocalizedDescription_DoesNotInvalidateInvariantHardwareToken()
    {
        string text = Inf("NTamd64", "%hardware%").Replace("Device=", "%description%=") +
            $"\n[Strings]\nhardware={DeviceId}\ndescription=Device\n[Strings.040C]\nhardware={DeviceId.ToLowerInvariant()}\ndescription=Peripheral";
        Assert.True(MicrosoftUpdateCatalogInfApplicability.IsApplicable(text, Target, [DeviceId]));
    }

    [Theory]
    [InlineData("Firmware", FirmwareGuid, FirmwareId, true)]
    [InlineData("Net", FirmwareGuid, FirmwareId, false)]
    [InlineData("Firmware", "{00000000-0000-0000-0000-000000000000}", FirmwareId, false)]
    [InlineData("Firmware", FirmwareGuid, "UEFI\\RES_{00000000-0000-0000-0000-000000000000}", false)]
    public void Firmware_RequiresClassGuidAndExactSystemResource(string className, string classGuid, string id, bool expected)
    {
        string text = Inf("NTamd64", id).Replace("Class=Net", $"Class={className}\nClassGuid={classGuid}");
        Assert.Equal(expected, MicrosoftUpdateCatalogInfApplicability.IsApplicable(text, Target, [FirmwareId], requireFirmware: true));
    }

    [Fact]
    public void DriverFlow_RejectsFirmwareEvenWhenHardwareMatches()
    {
        string text = Inf("NTamd64", FirmwareId).Replace("Class=Net", $"Class=Firmware\nClassGuid={FirmwareGuid}");
        Assert.False(MicrosoftUpdateCatalogInfApplicability.IsApplicable(text, Target, [FirmwareId]));
    }

    [Theory]
    [InlineData("x64", 0)]
    [InlineData("", 26100)]
    [InlineData("unknown", 26100)]
    public void UnknownTargetFacts_FailClosed(string architecture, int build)
    {
        Assert.False(MicrosoftUpdateCatalogInfApplicability.IsApplicable(Inf("NTamd64"), Target with { Architecture = architecture, BuildMajor = build }, [DeviceId]));
    }

    [Fact]
    public void ConflictingVersionDirective_FailsClosed()
    {
        Assert.False(MicrosoftUpdateCatalogInfApplicability.IsApplicable(Inf("NTamd64").Replace("Class=Net", "Class=Net\nClass=Firmware"), Target, [DeviceId]));
    }

    [Fact]
    public void RecursiveStringsAndUnterminatedQuotes_FailClosed()
    {
        string text = Inf("NTamd64", "%loop%") + "\n[Strings]\nloop=%loop%";
        Assert.False(MicrosoftUpdateCatalogInfApplicability.IsApplicable(text, Target, [DeviceId]));
        Assert.False(MicrosoftUpdateCatalogInfApplicability.IsApplicable(Inf("NTamd64", "\"" + DeviceId), Target, [DeviceId]));
    }

    [Fact]
    public void OversizedInput_FailsClosed()
    {
        Assert.False(MicrosoftUpdateCatalogInfApplicability.IsApplicable(Inf("NTamd64") + new string(' ', 4 * 1024 * 1024), Target, [DeviceId]));
    }

    private static string Inf(string decoration, string id = DeviceId) => $"""
        [Version]
        Signature="$WINDOWS NT$"
        Class=Net
        [Manufacturer]
        Maker=Models,{decoration}
        [Models.{decoration}]
        Device=Install,{id}
        """;
}
