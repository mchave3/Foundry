// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Foundry.Core.Models.Configuration.Deploy;
using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Deployment.Unattend;

namespace Foundry.Deploy.ViewModels;

/// <summary>Offers the native mode alongside exactly one protected answer file.</summary>
public sealed record UnattendOption(string DisplayName, UnattendSelection? Selection);

public sealed partial class DeploymentPreparationViewModel
{
    private readonly UnattendContentService? _unattendContentService;
    private string? _unattendArchitecture;
    private string? _unattendValidationContext;
    private string _unattendCatalogError = string.Empty;

    [ObservableProperty]
    private UnattendOption? selectedUnattendOption;

    [ObservableProperty]
    private string unattendValidationMessage = string.Empty;

    [ObservableProperty]
    private string unattendWarning = string.Empty;

    public ObservableCollection<UnattendOption> UnattendOptions { get; } = [];
    public bool HasUnattendCatalog => UnattendOptions.Count > 1 || _unattendCatalogError.Length > 0;
    public UnattendSelection? SelectedUnattend => SelectedUnattendOption?.Selection;
    public bool UsesCustomUnattend => SelectedUnattend is not null;
    public bool IsUnattendSelectionValid => string.IsNullOrEmpty(UnattendValidationMessage);
    public bool HasUnattendValidationError => !IsUnattendSelectionValid;
    public bool HasUnattendWarning => !string.IsNullOrEmpty(UnattendWarning);
    public string UnattendSummary => SelectedUnattendOption?.DisplayName ?? GetString(_unattendCatalogError.Length > 0 ? "Common.Unavailable" : "Unattend.Native");
    public bool IsComputerNameInputReadOnly => UsesCustomUnattend || IsTargetComputerNameReadOnly;
    public string EffectiveComputerName
    {
        get => UsesCustomUnattend ? GetString("Unattend.Managed") : TargetComputerName;
        set { if (!UsesCustomUnattend) TargetComputerName = value; }
    }

    /// <summary>Loads manifest choices while preserving a missing default as an error.</summary>
    public void ApplyUnattendConfiguration(DeployUnattendSettings settings, string configurationPath, string? failure = null)
    {
        UnattendOptions.Clear();
        UnattendOptions.Add(new UnattendOption(GetString("Unattend.Native"), null));
        _unattendCatalogError = failure ?? string.Empty;
        try
        {
            foreach (UnattendSelection selection in UnattendCatalog.Resolve(settings, configurationPath))
                UnattendOptions.Add(new UnattendOption(selection.File.DisplayName, selection));
            SelectedUnattendOption = settings.DefaultFileId is null ? UnattendOptions[0] :
                UnattendOptions.FirstOrDefault(option => string.Equals(option.Selection?.File.Id, settings.DefaultFileId, StringComparison.OrdinalIgnoreCase));
            if (SelectedUnattendOption is null)
                _unattendCatalogError = GetString("Unattend.MissingDefault");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException)
        {
            _unattendCatalogError = GetString("Unattend.Invalid");
        }
        OnPropertyChanged(nameof(HasUnattendCatalog));
        RevalidateUnattend();
    }

    /// <summary>Rechecks file applicability when the target architecture or enrollment mode changes.</summary>
    public void UpdateUnattendContext(string? architecture)
    {
        string context = $"{architecture}|{IsAutopilotEnabled}|{AutopilotProvisioningMode}";
        if (context == _unattendValidationContext) return;
        _unattendValidationContext = context;
        _unattendArchitecture = architecture;
        RevalidateUnattend();
    }

    partial void OnSelectedUnattendOptionChanged(UnattendOption? value)
    {
        RevalidateUnattend();
        OnPropertyChanged(nameof(SelectedUnattend));
        OnPropertyChanged(nameof(UsesCustomUnattend));
        OnPropertyChanged(nameof(EffectiveComputerName));
        OnPropertyChanged(nameof(IsComputerNameInputReadOnly));
        OnPropertyChanged(nameof(IsTargetComputerNameValid));
        OnPropertyChanged(nameof(HasTargetComputerNameValidationError));
        OnPropertyChanged(nameof(UnattendSummary));
        RaiseStateChanged();
    }

    /// <summary>Surfaces a launch-time failure if the selected media asset changed after selection.</summary>
    public void ReportUnattendFailure(string message)
    {
        UnattendValidationMessage = message;
        OnPropertyChanged(nameof(IsUnattendSelectionValid));
        OnPropertyChanged(nameof(HasUnattendValidationError));
        RaiseStateChanged();
    }

    private void RevalidateUnattend()
    {
        UnattendValidationMessage = _unattendCatalogError;
        UnattendWarning = string.Empty;
        if (SelectedUnattend is not null && _unattendCatalogError.Length == 0)
        {
            try
            {
                if (_unattendContentService is null) throw new InvalidDataException();
                using UnattendSnapshot snapshot = _unattendContentService.Read(SelectedUnattend, _unattendArchitecture,
                    IsAutopilotEnabled, AutopilotProvisioningMode);
                var warnings = new List<string> { GetString("Unattend.HookCompatibility") };
                if (snapshot.Inspection.HasCommands) warnings.Add(GetString("Unattend.CommandsWarning"));
                if (IsAutopilotEnabled && IsHardwareHashUploadMode) warnings.Add(GetString("Unattend.HashWarning"));
                UnattendWarning = string.Join(" ", warnings);
            }
            catch (InvalidOperationException)
            {
                UnattendValidationMessage = GetString("Unattend.AutopilotConflict");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                UnattendValidationMessage = GetString("Unattend.Invalid");
            }
        }
        OnPropertyChanged(nameof(IsUnattendSelectionValid));
        OnPropertyChanged(nameof(HasUnattendValidationError));
        OnPropertyChanged(nameof(HasUnattendWarning));
        RaiseStateChanged();
    }
}
