// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text;
using Foundry.Core.Models.Configuration;
using Foundry.Core.Services.Configuration;
using Foundry.Core.Tests.TestUtilities;

namespace Foundry.Core.Tests.Configuration;

public sealed class UnattendFileServiceTests
{
    [Theory]
    [InlineData("windowsPE")]
    [InlineData("offlineServicing")]
    [InlineData("generalize")]
    [InlineData("auditSystem")]
    [InlineData("auditUser")]
    [InlineData("unknown-secret-value")]
    public void Inspect_RejectsNonemptyUnsupportedPasses(string pass)
    {
        Assert.Throws<InvalidDataException>(() => UnattendFileService.Inspect(Xml(
            $"<settings pass='{pass}'><component name='Microsoft-Windows-Setup'/></settings>")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<unattend/>")]
    [InlineData("<other xmlns='urn:schemas-microsoft-com:unattend'/>")]
    [InlineData("<!DOCTYPE unattend [<!ENTITY secret SYSTEM 'file:///private'>]><unattend xmlns='urn:schemas-microsoft-com:unattend'>&secret;</unattend>")]
    [InlineData("<unattend xmlns='urn:schemas-microsoft-com:unattend'><secret-password></unattend>")]
    public void Inspect_RejectsUnsafeOrInvalidXmlWithoutExposingContent(string content)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => UnattendFileService.Inspect(Encoding.UTF8.GetBytes(content)));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inspect_RejectsOversizedContent()
    {
        Assert.Throws<InvalidDataException>(() => UnattendFileService.Inspect(new byte[(4 * 1024 * 1024) + 1]));
    }

    [Fact]
    public void Inspect_RejectsRootOfflineServicingInstructions()
    {
        Assert.Throws<InvalidDataException>(() => UnattendFileService.Inspect(Xml(
            "<servicing><package action='install'><assemblyIdentity name='Package'/></package></servicing>" +
            Settings("<ComputerName>PC</ComputerName>"))));
    }

    [Theory]
    [InlineData("<servicing/>")]
    [InlineData("<servicing xmlns='urn:extension'><package>extension content</package></servicing>")]
    public void Inspect_PreservesEmptyServicingAndForeignExtensions(string servicing)
    {
        UnattendInspection result = UnattendFileService.Inspect(Xml(servicing + Settings("<ComputerName>PC</ComputerName>")));
        Assert.Equal(["amd64"], result.Architectures);
    }

    [Theory]
    [InlineData("amd64", "x64")]
    [InlineData("arm64", "arm64")]
    public void Inspect_AcceptsSupportedSettingsAndEmptyUnsupportedPass(string architecture, string target)
    {
        UnattendInspection result = UnattendFileService.Inspect(Xml(
            "<settings pass='windowsPE'> <!-- unused --> </settings>" + Settings("<ComputerName>WORKSTATION</ComputerName>", architecture)), target);
        Assert.Equal([architecture], result.Architectures);
        Assert.False(result.ConflictsWithAutopilot);
    }

    [Fact]
    public void Inspect_RejectsMissingApplicableSettings()
    {
        Assert.Throws<InvalidDataException>(() => UnattendFileService.Inspect(Xml(Settings("<ComputerName>PC</ComputerName>", "amd64")), "arm64"));
        Assert.Throws<InvalidDataException>(() => UnattendFileService.Inspect(Xml("<settings pass='specialize'><component name='Microsoft-Windows-Shell-Setup' processorArchitecture='amd64'/></settings>")));
        Assert.Throws<InvalidDataException>(() => UnattendFileService.Inspect(Xml("<Extensions xmlns='urn:extensions'><ComputerName>PC</ComputerName></Extensions>")));
    }

    [Fact]
    public void Inspect_AllowsAuxiliaryComponentsAndScopesConflictsToTarget()
    {
        byte[] content = Xml(Settings("<ComputerName>PC</ComputerName>", "arm64") +
            Settings("<AutoLogon><Enabled>true</Enabled></AutoLogon>", "amd64", "oobeSystem") +
            Settings("<RegisteredOwner>Owner</RegisteredOwner>", "x86"));
        UnattendInspection result = UnattendFileService.Inspect(content, "arm64");
        Assert.Equal(["amd64", "arm64", "x86"], result.Architectures);
        Assert.False(result.ConflictsWithAutopilot);
        Assert.True(UnattendFileService.Inspect(content, "amd64").ConflictsWithAutopilot);
    }

    [Theory]
    [InlineData("<UserAccounts><LocalAccounts><LocalAccount><Name>User</Name></LocalAccount></LocalAccounts></UserAccounts>")]
    [InlineData("<AutoLogon><Enabled>true</Enabled></AutoLogon>")]
    [InlineData("<AutoLogon><Enabled>1</Enabled></AutoLogon>")]
    [InlineData("<OOBE><SkipMachineOOBE>true</SkipMachineOOBE></OOBE>")]
    [InlineData("<OOBE><SkipUserOOBE>true</SkipUserOOBE></OOBE>")]
    [InlineData("<OOBE><HideOnlineAccountScreens>true</HideOnlineAccountScreens></OOBE>")]
    [InlineData("<OOBE><HideLocalAccountScreen>true</HideLocalAccountScreen></OOBE>")]
    public void Inspect_DetectsKnownOobeEnrollmentConflicts(string settings)
    {
        Assert.True(UnattendFileService.Inspect(Xml(Settings(settings, pass: "oobeSystem"))).ConflictsWithAutopilot);
    }

    [Fact]
    public void Inspect_DetectsDomainJoinAndCommands()
    {
        byte[] content = Xml("<settings pass='specialize'><component name='Microsoft-Windows-UnattendedJoin' processorArchitecture='amd64'><Identification><JoinDomain>example.test</JoinDomain></Identification></component><component name='Microsoft-Windows-Deployment' processorArchitecture='amd64'><RunSynchronous><RunSynchronousCommand><Path>example</Path></RunSynchronousCommand></RunSynchronous></component></settings>");
        UnattendInspection result = UnattendFileService.Inspect(content);
        Assert.True(result.ConflictsWithAutopilot);
        Assert.True(result.HasCommands);
    }

    [Fact]
    public void Inspect_IgnoresLookalikeExtensionAndWrongComponentOrPassConflicts()
    {
        byte[] content = Xml(Settings("<AutoLogon><Enabled>false</Enabled></AutoLogon><OOBE><SkipMachineOOBE>0</SkipMachineOOBE></OOBE><AutoLogon xmlns='urn:extensions'><Enabled>true</Enabled></AutoLogon>", pass: "oobeSystem") +
            Settings("<OOBE><SkipMachineOOBE>true</SkipMachineOOBE></OOBE>") +
            "<settings pass='oobeSystem'><component name='Vendor-Extension' processorArchitecture='amd64'><AutoLogon><Enabled>true</Enabled></AutoLogon></component></settings>");
        Assert.False(UnattendFileService.Inspect(content).ConflictsWithAutopilot);
    }

    [Fact]
    public void Inspect_RejectsAuditReseal()
    {
        Assert.Throws<InvalidDataException>(() => UnattendFileService.Inspect(Xml(
            "<settings pass='oobeSystem'><component name='Microsoft-Windows-Deployment' processorArchitecture='amd64'><Reseal><Mode>Audit</Mode></Reseal></component></settings>")));
    }

    [Fact]
    public void Inspect_RejectsAuditResealEvenInAnotherArchitectureSection()
    {
        Assert.Throws<InvalidDataException>(() => UnattendFileService.Inspect(Xml(
            Settings("<ComputerName>PC</ComputerName>") +
            "<settings pass='oobeSystem'><component name='Microsoft-Windows-Deployment' processorArchitecture='arm64'><Reseal><Mode>Audit</Mode></Reseal></component></settings>"), "amd64"));
    }

    [Fact]
    public void Inspect_IgnoresOfflineDomainJoinOutsideItsSupportedPass()
    {
        Assert.False(UnattendFileService.Inspect(Xml(
            "<settings pass='specialize'><component name='Microsoft-Windows-UnattendedJoin' processorArchitecture='amd64'><OfflineIdentification><Provisioning><AccountData>join-data</AccountData></Provisioning></OfflineIdentification></component></settings>")).ConflictsWithAutopilot);
    }

    [Fact]
    public void Inspect_DetectsDomainProvisioningWithoutJoinDomain()
    {
        Assert.True(UnattendFileService.Inspect(Xml(
            "<settings pass='specialize'><component name='Microsoft-Windows-UnattendedJoin' processorArchitecture='amd64'><Identification><Provisioning><AccountData>join-data</AccountData></Provisioning></Identification></component></settings>")).ConflictsWithAutopilot);
    }

    [Theory]
    [InlineData("<FirstLogonCommands><SynchronousCommand><CommandLine>example</CommandLine></SynchronousCommand></FirstLogonCommands>")]
    [InlineData("<LogonCommands><AsynchronousCommand><CommandLine>example</CommandLine></AsynchronousCommand></LogonCommands>")]
    public void Inspect_DetectsShellCommandsWithoutInterpretingThem(string commands)
    {
        UnattendInspection result = UnattendFileService.Inspect(Xml(Settings(commands, pass: "oobeSystem")));
        Assert.True(result.HasCommands);
        Assert.False(result.ConflictsWithAutopilot);
    }

    [Fact]
    public void Inspect_DoesNotTreatExtensionCommandNamesAsWindowsCommands()
    {
        UnattendInspection result = UnattendFileService.Inspect(Xml(Settings(
            "<ComputerName>PC</ComputerName><FirstLogonCommands xmlns='urn:extensions'><SynchronousCommand/></FirstLogonCommands>", pass: "oobeSystem")));
        Assert.False(result.HasCommands);
    }

    [Fact]
    public void ValidateSettings_AcceptsNativeAndCustomDefaultsWithoutReadingSources()
    {
        UnattendFileSettings file = Metadata();
        UnattendFileService.ValidateSettings(Catalog(file), true);
        UnattendFileService.ValidateSettings(Catalog(file) with { DefaultFileId = file.Id }, true);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.xml.encrypted", UnattendFileService.GetAssetFileName(file.Id));
    }

    [Fact]
    public void Import_PreservesExactBytesIncludingExtensionsAndDetectsSourceChange()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "answer.xml");
        byte[] content = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(
            "<?xml version='1.0' encoding='utf-16'?>" + Encoding.UTF8.GetString(Xml(Settings("<ComputerName>PC</ComputerName>") + "<Extensions xmlns='urn:extensions'><Script><![CDATA[secret < value]]></Script></Extensions>")))).ToArray();
        File.WriteAllBytes(path, content);

        UnattendFileSettings file = UnattendFileService.Import(path);

        Assert.Equal(content, UnattendFileService.ReadValidated(file));
        Assert.Equal("answer.xml", file.DisplayName);
        Assert.Equal(32, file.Id.Length);
        Assert.Equal(64, file.ContentHash.Length);
        File.WriteAllBytes(path, Xml(Settings("<ComputerName>CHANGED</ComputerName>")));
        Assert.Throws<InvalidDataException>(() => UnattendFileService.ReadValidated(file));
    }

    [Fact]
    public void ReadValidated_RejectsMissingAndOversizedSourcesWithoutExposingPath()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "private-answer.xml");
        File.WriteAllBytes(path, Xml(Settings("<ComputerName>PC</ComputerName>")));
        UnattendFileSettings file = UnattendFileService.Import(path);
        File.Delete(path);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => UnattendFileService.ReadValidated(file));
        Assert.DoesNotContain(path, error.ToString());
        using (var stream = File.Create(path))
        {
            stream.SetLength((4 * 1024 * 1024) + 1);
        }

        Assert.Throws<InvalidDataException>(() => UnattendFileService.ReadValidated(file));
    }

    [Fact]
    public void ValidateSettings_RequiresProtectionAndNonemptyCatalogOnlyWhenEnabled()
    {
        UnattendFileService.ValidateSettings(new UnattendSettings(), false);
        Assert.Throws<InvalidOperationException>(() => UnattendFileService.ValidateSettings(new UnattendSettings { IsEnabled = true }, false));
        Assert.Throws<InvalidOperationException>(() => UnattendFileService.ValidateSettings(new UnattendSettings { IsEnabled = true }, true));
    }

    [Fact]
    public void ValidateSettings_RejectsDuplicateIdsDigestsAndMissingDefaults()
    {
        UnattendFileSettings file = Metadata();
        Assert.Throws<InvalidOperationException>(() => UnattendFileService.ValidateSettings(Catalog(file, file with { ContentHash = new string('b', 64) }), true));
        Assert.Throws<InvalidOperationException>(() => UnattendFileService.ValidateSettings(Catalog(file, file with { Id = new string('b', 32) }), true));
        Assert.Throws<InvalidOperationException>(() => UnattendFileService.ValidateSettings(Catalog(file) with { DefaultFileId = new string('b', 32) }, true));
    }

    [Theory]
    [InlineData("../private")]
    [InlineData("C:\\private")]
    [InlineData("answer.xml")]
    [InlineData("")]
    public void GetAssetFileName_RejectsNonGeneratedIds(string id)
    {
        Assert.Throws<InvalidDataException>(() => UnattendFileService.GetAssetFileName(id));
    }

    [Fact]
    public void ValidateSettings_RejectsInvalidDigestAndBlankLabel()
    {
        Assert.Throws<InvalidOperationException>(() => UnattendFileService.ValidateSettings(Catalog(Metadata() with { ContentHash = "invalid" }), true));
        Assert.Throws<InvalidOperationException>(() => UnattendFileService.ValidateSettings(Catalog(Metadata() with { DisplayName = " " }), true));
    }

    [Theory]
    [InlineData("Line\nBreak")]
    [InlineData("Tab\tLabel")]
    public void ValidateSettings_RejectsLabelsThatRuntimeCannotDisplay(string displayName)
    {
        Assert.Throws<InvalidOperationException>(() => UnattendFileService.ValidateSettings(Catalog(Metadata() with { DisplayName = displayName }), true));
        Assert.Throws<InvalidDataException>(() => UnattendFileService.ReadValidated(Metadata() with { DisplayName = displayName }));
    }

    [Fact]
    public void ValidateSettings_RejectsLabelsLongerThanRuntimeLimit()
    {
        Assert.Throws<InvalidOperationException>(() => UnattendFileService.ValidateSettings(Catalog(Metadata() with { DisplayName = new string('a', 201) }), true));
        UnattendFileService.ValidateSettings(Catalog(Metadata() with { DisplayName = new string('a', 200) }), true);
    }

    [Fact]
    public void Import_LimitsLongSourceFilenameToRuntimeDisplayLabelLimit()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, new string('a', 201) + ".xml");
        File.WriteAllBytes(path, Xml(Settings("<ComputerName>PC</ComputerName>")));

        UnattendFileSettings file = UnattendFileService.Import(path);

        Assert.Equal(new string('a', 200), file.DisplayName);
        UnattendFileService.ValidateSettings(Catalog(file), true);
    }

    private static UnattendFileSettings Metadata() => new() { Id = new string('a', 32), DisplayName = "Answer", SourcePath = "answer.xml", ContentHash = new string('a', 64) };

    private static UnattendSettings Catalog(params UnattendFileSettings[] files) => new() { IsEnabled = true, Files = files };

    private static byte[] Xml(string content) => Encoding.UTF8.GetBytes($"<unattend xmlns='urn:schemas-microsoft-com:unattend'>{content}</unattend>");

    private static string Settings(string content, string architecture = "amd64", string pass = "specialize") =>
        $"<settings pass='{pass}'><component name='Microsoft-Windows-Shell-Setup' processorArchitecture='{architecture}'>{content}</component></settings>";
}
