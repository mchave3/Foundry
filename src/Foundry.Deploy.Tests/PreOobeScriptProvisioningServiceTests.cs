// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Text.Json;
using Foundry.Deploy.Services.Deployment;
using Foundry.Deploy.Services.Deployment.PreOobe;

namespace Foundry.Deploy.Tests;

public sealed class PreOobeScriptProvisioningServiceTests
{
    private const string DriverResourceName = PreOobeScriptResources.InstallDriverPack;

    [Fact]
    public void Provision_OrdersScriptsByPriorityThenId()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        PreOobeScriptProvisioningResult result = service.Provision(
            windowsRoot,
            [
                CreateScript("cleanup", "Cleanup-PreOobe.ps1", PreOobeScriptPriority.Cleanup),
                CreateScript("apply-branding", "Apply-Branding.ps1", PreOobeScriptPriority.Customization),
                CreateScript("driver-pack", "Install-DriverPack.ps1", PreOobeScriptPriority.DriverProvisioning)
            ]);

        string runner = File.ReadAllText(result.RunnerPath);

        Assert.True(
            runner.IndexOf("Install-DriverPack.ps1", StringComparison.Ordinal) <
            runner.IndexOf("Apply-Branding.ps1", StringComparison.Ordinal));
        Assert.True(
            runner.IndexOf("Apply-Branding.ps1", StringComparison.Ordinal) <
            runner.IndexOf("Cleanup-PreOobe.ps1", StringComparison.Ordinal));
    }

    [Fact]
    public void Provision_RunsCleanupScriptsInFinally()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        PreOobeScriptProvisioningResult result = service.Provision(
            windowsRoot,
            [
                CreateScript("cleanup", "Cleanup-PreOobe.ps1", PreOobeScriptPriority.Cleanup),
                CreateScript("driver-pack", "Install-DriverPack.ps1", PreOobeScriptPriority.DriverProvisioning)
            ]);

        string runner = File.ReadAllText(result.RunnerPath);

        int driverIndex = runner.IndexOf("Install-DriverPack.ps1", StringComparison.Ordinal);
        int finallyIndex = runner.IndexOf("finally {", StringComparison.Ordinal);
        int cleanupIndex = runner.IndexOf("Cleanup-PreOobe.ps1", StringComparison.Ordinal);

        Assert.Contains("try {", runner);
        Assert.True(driverIndex < finallyIndex);
        Assert.True(finallyIndex < cleanupIndex);
        Assert.Contains("Write-Warning $_", runner);
    }

    [Fact]
    public void Provision_OrdersSamePriorityScriptsById()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        PreOobeScriptProvisioningResult result = service.Provision(
            windowsRoot,
            [
                CreateScript("configure-start-menu", "Configure-StartMenu.ps1", PreOobeScriptPriority.Customization),
                CreateScript("apply-branding", "Apply-Branding.ps1", PreOobeScriptPriority.Customization)
            ]);

        string runner = File.ReadAllText(result.RunnerPath);

        Assert.True(
            runner.IndexOf("Apply-Branding.ps1", StringComparison.Ordinal) <
            runner.IndexOf("Configure-StartMenu.ps1", StringComparison.Ordinal));
    }

    [Fact]
    public void Provision_ReplacesDuplicateScriptIdsWithLastDefinition()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        PreOobeScriptProvisioningResult result = service.Provision(
            windowsRoot,
            [
                CreateScript("apply-branding", "Apply-Branding.ps1", PreOobeScriptPriority.Customization, "-Mode", "old"),
                CreateScript("apply-branding", "Apply-Branding.ps1", PreOobeScriptPriority.Customization, "-Mode", "new")
            ]);

        using FileStream manifestStream = File.OpenRead(result.ManifestPath);
        using JsonDocument manifest = JsonDocument.Parse(manifestStream);
        JsonElement scripts = manifest.RootElement.GetProperty("scripts");

        JsonElement script = Assert.Single(scripts.EnumerateArray());
        Assert.Equal("apply-branding", script.GetProperty("id").GetString());
        string[] arguments = script.GetProperty("arguments")
            .EnumerateArray()
            .Select(argument => argument.GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(["-Mode", "new"], arguments);
    }

    [Fact]
    public void Provision_StagesEmbeddedCleanupScript()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        PreOobeScriptProvisioningResult result = service.Provision(
            windowsRoot,
            [
                new PreOobeScriptDefinition
                {
                    Id = "cleanup",
                    FileName = "Cleanup-PreOobe.ps1",
                    ResourceName = PreOobeScriptResources.CleanupPreOobe,
                    Priority = PreOobeScriptPriority.Cleanup
                }
            ]);

        string stagedScriptPath = Assert.Single(result.StagedScriptPaths);
        string stagedScript = File.ReadAllText(stagedScriptPath);

        Assert.EndsWith(Path.Combine("Scripts", "Cleanup-PreOobe.ps1"), stagedScriptPath);
        Assert.Contains("'C:\\DRIVERS'", stagedScript);
        Assert.Contains("'C:\\Drivers'", stagedScript);
        Assert.Contains("Remove-RootFolder", stagedScript);
        Assert.Contains("Start-Transcript -Path $TranscriptPath -Force", stagedScript);
        Assert.Contains("Cleanup-PreOobe.transcript.log", stagedScript);
        Assert.Contains("Write-FoundryLog", stagedScript);
        Assert.Contains("$elapsed = $now - $script:ScriptStartedAt", stagedScript);
    }

    [Fact]
    public void Provision_KeepsRootFolderCleanupOutOfDriverScript()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        PreOobeScriptProvisioningResult result = service.Provision(
            windowsRoot,
            [
                new PreOobeScriptDefinition
                {
                    Id = "driver-pack",
                    FileName = "Install-DriverPack.ps1",
                    ResourceName = PreOobeScriptResources.InstallDriverPack,
                    Priority = PreOobeScriptPriority.DriverProvisioning
                },
                new PreOobeScriptDefinition
                {
                    Id = "cleanup",
                    FileName = "Cleanup-PreOobe.ps1",
                    ResourceName = PreOobeScriptResources.CleanupPreOobe,
                    Priority = PreOobeScriptPriority.Cleanup
                }
            ]);

        string scriptsRoot = Path.GetDirectoryName(result.StagedScriptPaths[0])!;
        string driverScript = File.ReadAllText(Path.Combine(scriptsRoot, "Install-DriverPack.ps1"));
        string cleanupScript = File.ReadAllText(Path.Combine(scriptsRoot, "Cleanup-PreOobe.ps1"));

        Assert.DoesNotContain("Remove-Item -Path 'C:\\Drivers'", driverScript);
        Assert.Contains("'C:\\Drivers'", cleanupScript);
    }

    [Fact]
    public void Provision_StagesNetworkImportScriptWithPasswordlessPfxImport()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        PreOobeScriptProvisioningResult result = service.Provision(
            windowsRoot,
            [
                new PreOobeScriptDefinition
                {
                    Id = "network-profile-roaming",
                    FileName = "Import-NetworkProfiles.ps1",
                    ResourceName = PreOobeScriptResources.ImportNetworkProfiles,
                    Priority = PreOobeScriptPriority.NetworkProfileImport
                }
            ]);

        string stagedScript = File.ReadAllText(result.StagedScriptPaths.Single());

        Assert.Contains("$importArguments = @", stagedScript);
        Assert.Contains("if ($securePassword -ne $null)", stagedScript);
        Assert.Contains("$importArguments.Password = $securePassword", stagedScript);
        Assert.DoesNotContain("Skipping PFX import because no password file was staged.", stagedScript);
    }

    [Fact]
    public void Provision_StagesNetworkImportScriptWithPreOobeWifiConnect()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        PreOobeScriptProvisioningResult result = service.Provision(
            windowsRoot,
            [
                new PreOobeScriptDefinition
                {
                    Id = "network-profile-roaming",
                    FileName = "Import-NetworkProfiles.ps1",
                    ResourceName = PreOobeScriptResources.ImportNetworkProfiles,
                    Priority = PreOobeScriptPriority.NetworkProfileImport
                }
            ]);

        string stagedScript = File.ReadAllText(result.StagedScriptPaths.Single());

        Assert.Contains("Connect-FoundryWifiProfile", stagedScript);
        Assert.Contains("Set-FoundryWifiProfileConnectionMode", stagedScript);
        Assert.Contains("'preOobeConnectable'", stagedScript);
        Assert.Contains("'auto'", stagedScript);
        Assert.Contains("@('wlan', 'connect', \"name=$profileName\")", stagedScript);
    }

    [Fact]
    public void Provision_StagesDriverPackScriptWithTranscriptAndProcessWrapper()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        PreOobeScriptProvisioningResult result = service.Provision(
            windowsRoot,
            [
                new PreOobeScriptDefinition
                {
                    Id = "driver-pack",
                    FileName = "Install-DriverPack.ps1",
                    ResourceName = PreOobeScriptResources.InstallDriverPack,
                    Priority = PreOobeScriptPriority.DriverProvisioning
                }
            ]);

        string stagedScriptPath = Assert.Single(result.StagedScriptPaths);
        string stagedScript = File.ReadAllText(stagedScriptPath);

        Assert.Contains("Install-DriverPack.transcript.log", stagedScript);
        Assert.Contains("Start-Transcript -Path $TranscriptPath -Force", stagedScript);
        Assert.Contains("Write-FoundryLog", stagedScript);
        Assert.Contains("$operationDuration = [DateTimeOffset]::Now - $operationStartedAt", stagedScript);
        Assert.Contains("-FilePath $ResolvedPackagePath", stagedScript);
        Assert.Contains("-FilePath 'reg.exe'", stagedScript);
        Assert.Contains("-FilePath 'pnpunattend.exe'", stagedScript);
        Assert.DoesNotContain("& $ResolvedPackagePath", stagedScript);
        Assert.DoesNotContain("pnpunattend.exe AuditSystem /L", stagedScript);
    }

    [Fact]
    public void Provision_StagesAppxRemovalScriptWithSelectedPackages()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        PreOobeScriptProvisioningResult result = service.Provision(
            windowsRoot,
            [
                new PreOobeScriptDefinition
                {
                    Id = "remove-appx",
                    FileName = "Remove-AppX.ps1",
                    ResourceName = PreOobeScriptResources.RemoveAppx,
                    Priority = PreOobeScriptPriority.Customization,
                    DataFiles =
                    [
                        new PreOobeScriptDataFile
                        {
                            FileName = "Remove-AppX.packages.json",
                            Content = """
                                [
                                  {
                                    "packageName": "Microsoft.BingNews"
                                  },
                                  {
                                    "packageName": "Microsoft.BingWeather"
                                  }
                                ]
                                """
                        }
                    ]
                }
            ]);

        string stagedScriptPath = Assert.Single(result.StagedScriptPaths);
        string stagedScript = File.ReadAllText(stagedScriptPath);
        string runner = File.ReadAllText(result.RunnerPath);
        string stagedCatalog = File.ReadAllText(Path.Combine(Path.GetDirectoryName(result.RunnerPath)!, "Data", "Remove-AppX.packages.json"));

        Assert.EndsWith(Path.Combine("Scripts", "Remove-AppX.ps1"), stagedScriptPath);
        Assert.Contains("Remove-AppX.transcript.log", stagedScript);
        Assert.Contains("Get-AppxProvisionedPackage -Online", stagedScript);
        Assert.Contains("Remove-AppxProvisionedPackage @removeArguments", stagedScript);
        Assert.Contains("PackageName = $resolvedPackageName", stagedScript);
        Assert.Contains("Online = $true", stagedScript);
        Assert.Contains("ConvertFrom-Json", stagedScript);
        Assert.Contains("Remove-AppX.packages.json", stagedScript);
        Assert.DoesNotContain("dism.exe", stagedScript);
        Assert.Contains("Remove-FoundryProvisionedAppxPackage -CatalogPackageName ([string]$selectedPackageName)", stagedScript);
        Assert.Contains("Write-FoundryLog", stagedScript);
        Assert.DoesNotContain("Microsoft.BingNews", runner);
        Assert.DoesNotContain("Microsoft.BingWeather", runner);
        Assert.Contains("Microsoft.BingNews", stagedCatalog);
        Assert.Contains("Microsoft.BingWeather", stagedCatalog);
    }

    [Fact]
    public void Provision_StagesAiComponentRemovalScriptWithSettings()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        PreOobeScriptProvisioningResult result = service.Provision(
            windowsRoot,
            [
                new PreOobeScriptDefinition
                {
                    Id = "remove-ai-components",
                    FileName = "Remove-AiComponents.ps1",
                    ResourceName = PreOobeScriptResources.RemoveAiComponents,
                    Priority = PreOobeScriptPriority.Customization,
                    DataFiles =
                    [
                        new PreOobeScriptDataFile
                        {
                            FileName = "Remove-AiComponents.settings.json",
                            Content = """
                                {
                                  "appxPackages": [
                                    {
                                      "packageName": "Microsoft.Copilot"
                                    },
                                    {
                                      "packageName": "Microsoft.Windows.AIHub"
                                    }
                                  ]
                                }
                                """
                        }
                    ]
                }
            ]);

        string stagedScriptPath = Assert.Single(result.StagedScriptPaths);
        string stagedScript = File.ReadAllText(stagedScriptPath);
        string runner = File.ReadAllText(result.RunnerPath);
        string stagedSettings = File.ReadAllText(Path.Combine(Path.GetDirectoryName(result.RunnerPath)!, "Data", "Remove-AiComponents.settings.json"));

        Assert.EndsWith(Path.Combine("Scripts", "Remove-AiComponents.ps1"), stagedScriptPath);
        Assert.Contains("Remove-AiComponents.transcript.log", stagedScript);
        Assert.Contains("Remove-AiComponents.settings.json", stagedScript);
        Assert.Contains("Get-AppxProvisionedPackage -Online", stagedScript);
        Assert.Contains("Remove-AppxProvisionedPackage @removeArguments", stagedScript);
        Assert.Contains("Get-SelectedAiAppxPackageNames", stagedScript);
        Assert.Contains("Remove-FoundryProvisionedAppxPackage -CatalogPackageName ([string]$selectedPackageName)", stagedScript);
        Assert.DoesNotContain("-isnot $null", stagedScript);
        Assert.DoesNotContain("Registry::", stagedScript);
        Assert.DoesNotContain("reg.exe", stagedScript);
        Assert.DoesNotContain("Users\\Default\\NTUSER.DAT", stagedScript);
        Assert.DoesNotContain("TurnOffWindowsCopilot", stagedScript);
        Assert.DoesNotContain("DisableAIDataAnalysis", stagedScript);
        Assert.DoesNotContain("DisableClickToDo", stagedScript);
        Assert.DoesNotContain("WSAIFabricSvc", stagedScript);
        Assert.DoesNotContain("CopilotCDPPageContext", stagedScript);
        Assert.DoesNotContain("DisableCocreator", stagedScript);
        Assert.DoesNotContain("DisableAIFeatures", stagedScript);
        Assert.Contains("Write-FoundryLog", stagedScript);
        Assert.DoesNotContain("removeCopilot", runner);
        Assert.Contains("\"packageName\": \"Microsoft.Copilot\"", stagedSettings);
        Assert.Contains("\"packageName\": \"Microsoft.Windows.AIHub\"", stagedSettings);
        Assert.DoesNotContain("removeCopilot", stagedSettings);
        Assert.DoesNotContain("disableNotepadAi", stagedSettings);
    }

    [Fact]
    public void Provision_StagesNestedBinaryDataFiles()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        PreOobeScriptProvisioningResult result = service.Provision(
            windowsRoot,
            [
                new PreOobeScriptDefinition
                {
                    Id = "network-profile-roaming",
                    FileName = "Install-DriverPack.ps1",
                    ResourceName = PreOobeScriptResources.InstallDriverPack,
                    Priority = PreOobeScriptPriority.NetworkProfileImport,
                    DataFiles =
                    [
                        new PreOobeScriptDataFile
                        {
                            FileName = @"NetworkProfiles\certificates\My\client.pfx",
                            Bytes = [4, 5, 6],
                            IsSensitive = true
                        }
                    ]
                }
            ]);

        string dataPath = Path.Combine(
            Path.GetDirectoryName(result.RunnerPath)!,
            "Data",
            "NetworkProfiles",
            "certificates",
            "My",
            "client.pfx");

        Assert.True(File.Exists(dataPath));
        Assert.Equal([4, 5, 6], File.ReadAllBytes(dataPath));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(result.ManifestPath));
        Assert.Contains(
            @"NetworkProfiles\certificates\My\client.pfx",
            manifest.RootElement.GetProperty("scripts")[0].GetProperty("dataFiles")[0].GetString());
    }

    [Fact]
    public void Provision_RunnerReliesOnScriptTranscriptsInsteadOfPerScriptRedirectLogs()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        PreOobeScriptProvisioningResult result = service.Provision(
            windowsRoot,
            [CreateScript("driver-pack", "Install-DriverPack.ps1", PreOobeScriptPriority.DriverProvisioning)]);

        string runner = File.ReadAllText(result.RunnerPath);

        Assert.Contains("$name.transcript.log", runner);
        Assert.DoesNotContain("$name.log", runner);
        Assert.DoesNotContain("*> $logPath", runner);
    }

    [Fact]
    public void Provision_WritesSetupCompleteLauncherBlock()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        PreOobeScriptProvisioningResult result = service.Provision(
            windowsRoot,
            [CreateScript("driver-pack", "Install-DriverPack.ps1", PreOobeScriptPriority.DriverProvisioning)]);

        string setupComplete = File.ReadAllText(result.SetupCompletePath);

        Assert.Contains("REM >>> FOUNDRY PRE-OOBE BEGIN", setupComplete);
        Assert.Contains(
            "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%SystemRoot%\\Temp\\Foundry\\PreOobe\\Invoke-FoundryPreOobe.ps1\" >>\"%SystemRoot%\\Temp\\Foundry\\Logs\\PreOobe\\SetupComplete.log\" 2>&1",
            setupComplete);
        Assert.Contains("mkdir \"%SystemRoot%\\Temp\\Foundry\\Logs\\PreOobe\" >nul 2>&1", setupComplete);
        Assert.Contains("Foundry pre-OOBE runner exited with %FOUNDRY_PREOOBE_EXIT%", setupComplete);
        Assert.Contains("REM <<< FOUNDRY PRE-OOBE END", setupComplete);
    }

    [Fact]
    public void Provision_DoesNotDuplicateSetupCompleteLauncherBlock()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());
        PreOobeScriptDefinition script = CreateScript(
            "driver-pack",
            "Install-DriverPack.ps1",
            PreOobeScriptPriority.DriverProvisioning);

        service.Provision(windowsRoot, [script]);
        PreOobeScriptProvisioningResult result = service.Provision(windowsRoot, [script]);

        string setupComplete = File.ReadAllText(result.SetupCompletePath);

        Assert.Equal(1, CountOccurrences(setupComplete, "REM >>> FOUNDRY PRE-OOBE BEGIN"));
    }

    [Fact]
    public void Provision_ReplacesExistingSetupCompleteLauncherBlock()
    {
        string windowsRoot = CreateWindowsRoot();
        string setupCompletePath = Path.Combine(windowsRoot, "Windows", "Setup", "Scripts", "SetupComplete.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(setupCompletePath)!);
        File.WriteAllText(
            setupCompletePath,
            string.Join(
                Environment.NewLine,
                [
                    "@echo off",
                    "REM >>> FOUNDRY PRE-OOBE BEGIN",
                    "old-runner-command",
                    "REM <<< FOUNDRY PRE-OOBE END"
                ]));
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        PreOobeScriptProvisioningResult result = service.Provision(
            windowsRoot,
            [CreateScript("driver-pack", "Install-DriverPack.ps1", PreOobeScriptPriority.DriverProvisioning)]);

        string setupComplete = File.ReadAllText(result.SetupCompletePath);

        Assert.DoesNotContain("old-runner-command", setupComplete);
        Assert.Contains("SetupComplete.log", setupComplete);
        Assert.Equal(1, CountOccurrences(setupComplete, "REM >>> FOUNDRY PRE-OOBE BEGIN"));
    }

    [Fact]
    public void Provision_RemovesLegacyDriverPackSetupCompleteBlock()
    {
        string windowsRoot = CreateWindowsRoot();
        string setupCompletePath = Path.Combine(windowsRoot, "Windows", "Setup", "Scripts", "SetupComplete.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(setupCompletePath)!);
        File.WriteAllText(
            setupCompletePath,
            string.Join(
                Environment.NewLine,
                [
                    "@echo off",
                    "REM >>> FOUNDRY DRIVERPACK BEGIN",
                    "legacy-driver-command",
                    "REM <<< FOUNDRY DRIVERPACK END",
                    "echo keep-existing-command"
                ]));
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        PreOobeScriptProvisioningResult result = service.Provision(
            windowsRoot,
            [CreateScript("driver-pack", "Install-DriverPack.ps1", PreOobeScriptPriority.DriverProvisioning)]);

        string setupComplete = File.ReadAllText(result.SetupCompletePath);

        Assert.DoesNotContain("FOUNDRY DRIVERPACK", setupComplete);
        Assert.DoesNotContain("legacy-driver-command", setupComplete);
        Assert.Contains("echo keep-existing-command", setupComplete);
        Assert.Contains("FOUNDRY PRE-OOBE", setupComplete);
    }

    [Fact]
    public void Provision_ThrowsWhenScriptsAreEmpty()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => service.Provision(windowsRoot, []));

        Assert.Equal("At least one pre-OOBE script is required.", exception.Message);
    }

    [Fact]
    public void Provision_ThrowsWhenEmbeddedScriptResourceIsMissing()
    {
        string windowsRoot = CreateWindowsRoot();
        var service = new PreOobeScriptProvisioningService(new SetupCompleteScriptService());
        var script = new PreOobeScriptDefinition
        {
            Id = "missing",
            FileName = "Missing.ps1",
            ResourceName = "Foundry.Deploy.PreOobe.Missing",
            Priority = PreOobeScriptPriority.Customization
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => service.Provision(windowsRoot, [script]));

        Assert.Equal(
            "Embedded pre-OOBE script resource 'Foundry.Deploy.PreOobe.Missing' was not found.",
            exception.Message);
    }

    private static PreOobeScriptDefinition CreateScript(
        string id,
        string fileName,
        PreOobeScriptPriority priority,
        params string[] arguments)
    {
        return new PreOobeScriptDefinition
        {
            Id = id,
            FileName = fileName,
            ResourceName = DriverResourceName,
            Priority = priority,
            Arguments = arguments
        };
    }

    private static string CreateWindowsRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "FoundryDeployTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static int CountOccurrences(string value, string expected)
    {
        int count = 0;
        int index = 0;

        while ((index = value.IndexOf(expected, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += expected.Length;
        }

        return count;
    }
}
