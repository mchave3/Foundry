// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Models.Configuration;
using Foundry.Core.Services.Configuration;

namespace Foundry.Core.Tests.Configuration;

public sealed class ConfigurationOverviewEvaluatorTests
{
    [Fact]
    public void Evaluate_DefaultAnswerFiles_AreDisabledInOverviewAndNavigation()
    {
        ConfigurationOverviewEvaluation evaluation = ConfigurationOverviewEvaluator.Evaluate(
            CreateContext(new FoundryConfigurationDocument()));

        Assert.Equal(ConfigurationOverviewState.Disabled, evaluation[ConfigurationOverviewItem.Unattend]);
        Assert.Equal(ConfigurationOverviewState.Disabled, ConfigurationOverviewNavigationEvaluator.EvaluateTarget(
            evaluation, ConfigurationNavigationTarget.Unattend));
    }

    [Theory]
    [InlineData(true, ConfigurationOverviewState.Configured)]
    [InlineData(false, ConfigurationOverviewState.NeedsAttention)]
    public void Evaluate_EnabledAnswerFiles_UsesSourceAndProtectionReadiness(bool isReady, ConfigurationOverviewState expected)
    {
        var configuration = new FoundryConfigurationDocument
        {
            Unattend = new UnattendSettings { IsEnabled = true }
        };
        ConfigurationOverviewEvaluation evaluation = ConfigurationOverviewEvaluator.Evaluate(
            CreateContext(configuration) with { IsUnattendConfigurationReady = isReady });

        Assert.Equal(expected, evaluation[ConfigurationOverviewItem.Unattend]);
        Assert.Equal(expected, ConfigurationOverviewNavigationEvaluator.EvaluateTarget(
            evaluation, ConfigurationNavigationTarget.Unattend));
    }

    [Fact]
    public void Evaluate_DefaultConfiguration_UsesValidDefaultsAndNeutralOptionalStates()
    {
        ConfigurationOverviewEvaluation evaluation = ConfigurationOverviewEvaluator.Evaluate(
            CreateContext(new FoundryConfigurationDocument()));

        Assert.Equal(ConfigurationOverviewState.Default, evaluation[ConfigurationOverviewItem.Architecture]);
        Assert.Equal(ConfigurationOverviewState.Default, evaluation[ConfigurationOverviewItem.SecureBoot]);
        Assert.Equal(ConfigurationOverviewState.Default, evaluation[ConfigurationOverviewItem.TimeZone]);
        Assert.Equal(ConfigurationOverviewState.Default, evaluation[ConfigurationOverviewItem.DeploymentCompletion]);
        Assert.Equal(ConfigurationOverviewState.Disabled, evaluation[ConfigurationOverviewItem.DeploymentProtection]);
        Assert.Equal(ConfigurationOverviewState.Disabled, evaluation[ConfigurationOverviewItem.DriverOptions]);
        Assert.Equal(ConfigurationOverviewState.NotConfigured, evaluation[ConfigurationOverviewItem.EthernetDot1x]);
        Assert.Equal(ConfigurationOverviewState.NotConfigured, evaluation[ConfigurationOverviewItem.Wifi]);
        Assert.Equal(ConfigurationOverviewState.NotSelected, evaluation[ConfigurationOverviewItem.AutopilotJsonProfile]);
        Assert.Equal(ConfigurationOverviewState.Disabled, evaluation[ConfigurationOverviewItem.OperatingSystemSelection]);
        Assert.Equal(ConfigurationOverviewState.Disabled, evaluation[ConfigurationOverviewItem.MachineNaming]);
        Assert.Equal(ConfigurationOverviewState.Disabled, evaluation[ConfigurationOverviewItem.Oobe]);
        Assert.Equal(ConfigurationOverviewState.Disabled, evaluation[ConfigurationOverviewItem.OptionalFeatures]);
        Assert.Equal(ConfigurationOverviewState.Disabled, evaluation[ConfigurationOverviewItem.AppxRemoval]);
        Assert.Equal(ConfigurationOverviewState.Disabled, evaluation[ConfigurationOverviewItem.AiComponents]);
    }

    [Fact]
    public void Evaluate_EnabledConfigurationWithoutRequiredRuntimeInputs_NeedsAttention()
    {
        var configuration = new FoundryConfigurationDocument
        {
            General = new GeneralSettings
            {
                DeploymentProtection = new DeploymentProtectionSettings { IsEnabled = true },
                CustomDriverDirectoryPath = "C:\\MissingDrivers"
            },
            Network = new NetworkSettings
            {
                WifiProvisioned = true,
                Wifi = new WifiSettings
                {
                    IsEnabled = true,
                    Ssid = "Contoso",
                    SecurityType = NetworkConfigurationValidator.WifiSecurityPersonal
                }
            }
        };

        ConfigurationOverviewEvaluation evaluation = ConfigurationOverviewEvaluator.Evaluate(
            CreateContext(configuration) with
            {
                IsDeploymentProtectionSecretReady = false,
                IsCustomDriverConfigurationReady = false
            });

        Assert.Equal(ConfigurationOverviewState.NeedsAttention, evaluation[ConfigurationOverviewItem.DeploymentProtection]);
        Assert.Equal(ConfigurationOverviewState.NeedsAttention, evaluation[ConfigurationOverviewItem.DriverOptions]);
        Assert.Equal(ConfigurationOverviewState.NeedsAttention, evaluation[ConfigurationOverviewItem.Wifi]);
    }

    [Fact]
    public void Evaluate_Autopilot_OnlyActiveModeCanBeConfiguredOrNeedAttention()
    {
        var configuration = new FoundryConfigurationDocument
        {
            Autopilot = new AutopilotSettings
            {
                IsEnabled = true,
                ProvisioningMode = AutopilotProvisioningMode.HardwareHashUpload
            }
        };

        ConfigurationOverviewEvaluation ready = ConfigurationOverviewEvaluator.Evaluate(
            CreateContext(configuration) with { IsAutopilotConfigurationReady = true });
        ConfigurationOverviewEvaluation blocked = ConfigurationOverviewEvaluator.Evaluate(
            CreateContext(configuration) with { IsAutopilotConfigurationReady = false });

        Assert.Equal(ConfigurationOverviewState.NotSelected, ready[ConfigurationOverviewItem.AutopilotJsonProfile]);
        Assert.Equal(ConfigurationOverviewState.Configured, ready[ConfigurationOverviewItem.AutopilotZeroTouch]);
        Assert.Equal(ConfigurationOverviewState.NotSelected, ready[ConfigurationOverviewItem.AutopilotInteractive]);
        Assert.Equal(ConfigurationOverviewState.NeedsAttention, blocked[ConfigurationOverviewItem.AutopilotZeroTouch]);
    }

    [Fact]
    public void Evaluate_OptionalCustomizationWithoutActions_IsEffectivelyDisabled()
    {
        var configuration = new FoundryConfigurationDocument
        {
            Customization = new CustomizationSettings
            {
                WindowsOptionalFeatures = new WindowsOptionalFeatureSettings { IsEnabled = true },
                AppxRemoval = new AppxRemovalSettings { IsEnabled = true },
                AiComponentRemoval = new AiComponentRemovalSettings { IsEnabled = true }
            }
        };

        ConfigurationOverviewEvaluation evaluation = ConfigurationOverviewEvaluator.Evaluate(CreateContext(configuration));

        Assert.Equal(ConfigurationOverviewState.Disabled, evaluation[ConfigurationOverviewItem.OptionalFeatures]);
        Assert.Equal(ConfigurationOverviewState.Disabled, evaluation[ConfigurationOverviewItem.AppxRemoval]);
        Assert.Equal(ConfigurationOverviewState.Disabled, evaluation[ConfigurationOverviewItem.AiComponents]);
    }

    [Fact]
    public void Evaluate_EnabledOpenOsSelectionAndProvisionedWifi_AreConfigured()
    {
        var configuration = new FoundryConfigurationDocument
        {
            OperatingSystemSelection = new OperatingSystemSelectionSettings { IsEnabled = true },
            Network = new NetworkSettings { WifiProvisioned = true }
        };

        ConfigurationOverviewEvaluation evaluation = ConfigurationOverviewEvaluator.Evaluate(CreateContext(configuration));

        Assert.Equal(ConfigurationOverviewState.Configured, evaluation[ConfigurationOverviewItem.OperatingSystemSelection]);
        Assert.Equal(ConfigurationOverviewState.Configured, evaluation[ConfigurationOverviewItem.Wifi]);
    }

    [Fact]
    public void Evaluate_InvalidMachineNameComposition_NeedsAttention()
    {
        var configuration = new FoundryConfigurationDocument
        {
            Customization = new CustomizationSettings
            {
                MachineNaming = new MachineNamingSettings
                {
                    IsEnabled = true,
                    Mode = MachineNamingMode.Composed,
                    Components =
                    [
                        new MachineNameComponentSettings { Type = MachineNameComponentType.StaticText, StaticText = "PC" },
                        new MachineNameComponentSettings { Type = MachineNameComponentType.StaticText, StaticText = "LAB" }
                    ]
                }
            }
        };

        ConfigurationOverviewEvaluation evaluation = ConfigurationOverviewEvaluator.Evaluate(CreateContext(configuration));

        Assert.Equal(ConfigurationOverviewState.NeedsAttention, evaluation[ConfigurationOverviewItem.MachineNaming]);
    }

    [Fact]
    public void Evaluate_ValidMachineNameComposition_IsConfigured()
    {
        var configuration = new FoundryConfigurationDocument
        {
            Customization = new CustomizationSettings
            {
                MachineNaming = new MachineNamingSettings
                {
                    IsEnabled = true,
                    Mode = MachineNamingMode.Composed,
                    Components =
                    [
                        new MachineNameComponentSettings { Type = MachineNameComponentType.StaticText, StaticText = "PC" },
                        new MachineNameComponentSettings
                        {
                            Type = MachineNameComponentType.SerialNumber,
                            MaximumLength = 12,
                            Truncation = MachineNameTruncation.KeepRight
                        }
                    ],
                    Separator = MachineNameSeparator.Hyphen
                }
            }
        };

        ConfigurationOverviewEvaluation evaluation = ConfigurationOverviewEvaluator.Evaluate(CreateContext(configuration));

        Assert.Equal(ConfigurationOverviewState.Configured, evaluation[ConfigurationOverviewItem.MachineNaming]);
    }

    [Fact]
    public void Evaluate_InvalidOobeAdditionalAccount_NeedsAttention()
    {
        var configuration = new FoundryConfigurationDocument
        {
            Customization = new CustomizationSettings
            {
                Oobe = new OobeSettings
                {
                    IsEnabled = true,
                    AdditionalAccounts =
                    [
                        new OobeAdditionalAccountSettings
                        {
                            Id = "account-1",
                            UserName = "Tech/User",
                            Type = OobeAccountType.Standard
                        }
                    ]
                }
            }
        };

        ConfigurationOverviewEvaluation evaluation = ConfigurationOverviewEvaluator.Evaluate(CreateContext(configuration));

        Assert.Equal(ConfigurationOverviewState.NeedsAttention, evaluation[ConfigurationOverviewItem.Oobe]);
    }

    [Fact]
    public void Evaluate_OobeSecretMismatch_NeedsAttention()
    {
        var configuration = new FoundryConfigurationDocument
        {
            Customization = new CustomizationSettings
            {
                Oobe = new OobeSettings
                {
                    IsEnabled = true,
                    EnableAdministratorAccount = true
                }
            }
        };

        ConfigurationOverviewEvaluation evaluation = ConfigurationOverviewEvaluator.Evaluate(
            CreateContext(configuration) with
            {
                IsOobeAccountConfigurationReady = false
            });

        Assert.Equal(ConfigurationOverviewState.NeedsAttention, evaluation[ConfigurationOverviewItem.Oobe]);
    }

    [Fact]
    public void Evaluate_OobePasswordWithoutDeploymentProtection_MarksRelatedSettingsAsNeedingAttention()
    {
        var configuration = new FoundryConfigurationDocument
        {
            Customization = new CustomizationSettings
            {
                Oobe = new OobeSettings
                {
                    IsEnabled = true,
                    EnableAdministratorAccount = true,
                    UseAdministratorPassword = true
                }
            }
        };

        ConfigurationOverviewEvaluation evaluation = ConfigurationOverviewEvaluator.Evaluate(CreateContext(configuration));

        Assert.Equal(ConfigurationOverviewState.NeedsAttention, evaluation[ConfigurationOverviewItem.DeploymentProtection]);
        Assert.Equal(ConfigurationOverviewState.NeedsAttention, evaluation[ConfigurationOverviewItem.Oobe]);
    }

    [Theory]
    [InlineData(false, true, ConfigurationOverviewState.NeedsAttention)]
    [InlineData(true, true, ConfigurationOverviewState.NeedsAttention)]
    [InlineData(true, false, ConfigurationOverviewState.Configured)]
    public void Evaluate_OobeAccountsWhenAutopilotEnabled_OnlyAdditionalAccountsNeedAttention(
        bool enableAdministrator, bool includeAdditionalAccount, ConfigurationOverviewState expectedState)
    {
        var configuration = new FoundryConfigurationDocument
        {
            Autopilot = new AutopilotSettings
            {
                IsEnabled = true,
                ProvisioningMode = AutopilotProvisioningMode.JsonProfile
            },
            Customization = new CustomizationSettings
            {
                Oobe = new OobeSettings
                {
                    IsEnabled = true,
                    EnableAdministratorAccount = enableAdministrator,
                    AdditionalAccounts = includeAdditionalAccount ?
                    [
                        new OobeAdditionalAccountSettings
                        {
                            Id = "account-1",
                            UserName = "Technician",
                            Type = OobeAccountType.Standard
                        }
                    ] : []
                }
            }
        };

        ConfigurationOverviewEvaluation evaluation = ConfigurationOverviewEvaluator.Evaluate(CreateContext(configuration));

        Assert.Equal(expectedState, evaluation[ConfigurationOverviewItem.Oobe]);
    }

    [Fact]
    public void Count_InvalidEthernetConfiguration_CountsOneActionableItem()
    {
        var configuration = new FoundryConfigurationDocument
        {
            Network = new NetworkSettings
            {
                Dot1x = new Dot1xSettings { IsEnabled = true }
            }
        };

        ConfigurationOverviewEvaluation evaluation = ConfigurationOverviewEvaluator.Evaluate(CreateContext(configuration));

        Assert.Equal(1, evaluation.NeedsAttentionCount);
    }

    [Fact]
    public void EvaluateTarget_InvalidEthernetConfiguration_NeedsAttention()
    {
        var configuration = new FoundryConfigurationDocument
        {
            Network = new NetworkSettings
            {
                Dot1x = new Dot1xSettings { IsEnabled = true }
            }
        };
        ConfigurationOverviewEvaluation evaluation = ConfigurationOverviewEvaluator.Evaluate(CreateContext(configuration));

        ConfigurationOverviewState state = ConfigurationOverviewNavigationEvaluator.EvaluateTarget(
            evaluation,
            ConfigurationNavigationTarget.EthernetDot1x);

        Assert.Equal(ConfigurationOverviewState.NeedsAttention, state);
    }

    [Fact]
    public void EvaluateTarget_GeneralConfigurationWithInvalidSecret_NeedsAttention()
    {
        var configuration = new FoundryConfigurationDocument
        {
            General = new GeneralSettings
            {
                DeploymentProtection = new DeploymentProtectionSettings { IsEnabled = true }
            }
        };
        ConfigurationOverviewEvaluation evaluation = ConfigurationOverviewEvaluator.Evaluate(
            CreateContext(configuration) with { IsDeploymentProtectionSecretReady = false });

        ConfigurationOverviewState state = ConfigurationOverviewNavigationEvaluator.EvaluateTarget(
            evaluation,
            ConfigurationNavigationTarget.General);

        Assert.Equal(ConfigurationOverviewState.NeedsAttention, state);
    }

    private static ConfigurationOverviewContext CreateContext(FoundryConfigurationDocument configuration)
    {
        return new ConfigurationOverviewContext
        {
            Configuration = configuration,
            EffectiveNetwork = configuration.Network,
            IsWinPeLanguageReady = true,
            IsCustomDriverConfigurationReady = true,
            IsDeploymentProtectionSecretReady = true,
            IsOobeAccountConfigurationReady = true,
            IsAutopilotConfigurationReady = true
        };
    }
}
