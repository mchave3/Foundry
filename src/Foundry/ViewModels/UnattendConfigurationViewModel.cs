// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Collections.ObjectModel;
using Foundry.Core.Models.Configuration;
using Foundry.Core.Services.Application;
using Foundry.Core.Services.Configuration;
using Foundry.Services.Configuration;
using Foundry.Services.Localization;

namespace Foundry.ViewModels;

/// <summary>
/// Authors a catalog of referenced answer files, retaining metadata only between operations.
/// </summary>
public sealed partial class UnattendConfigurationViewModel : ObservableObject, IDisposable
{
    private readonly IFoundryConfigurationStateService configurationStateService;
    private readonly IFilePickerService filePickerService;
    private readonly IApplicationLocalizationService localizationService;
    private bool isApplyingState;
    private bool isDisposed;

    public UnattendConfigurationViewModel(
        IFoundryConfigurationStateService configurationStateService,
        IFilePickerService filePickerService,
        IApplicationLocalizationService localizationService)
    {
        this.configurationStateService = configurationStateService;
        this.filePickerService = filePickerService;
        this.localizationService = localizationService;
        ApplyState();
        configurationStateService.StateChanged += OnStateChanged;
        localizationService.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<UnattendFileEntryViewModel> Files { get; } = [];

    public ObservableCollection<UnattendDefaultOption> DefaultOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(ImportFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshSourcesCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameFileCommand))]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(ImportFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshSourcesCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameFileCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(RefreshFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameFileCommand))]
    public partial UnattendFileEntryViewModel? SelectedFile { get; set; }

    [ObservableProperty]
    public partial UnattendDefaultOption? SelectedDefault { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameFileCommand))]
    public partial string RenameText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusOpen))]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReadinessMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasReadinessIssue { get; set; }

    public bool IsStatusOpen => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool CanToggle => !IsBusy;

    public bool CanImport => IsEnabled && !IsBusy;

    public bool CanEdit => CanImport && SelectedFile is not null;

    public bool CanRename => CanEdit && !string.IsNullOrWhiteSpace(RenameText);

    public string PageTitle => Text("Nav_UnattendKey.Title");
    public string DocumentationUrl => FoundryApplicationInfo.UnattendDocumentationUrl;
    public string PageDescription => Text("Nav_UnattendKey.Description");
    public string EnableLabel => Text("Unattend.EnableLabel");
    public string EnableDescription => Text("Unattend.EnableDescription");
    public string CatalogLabel => Text("Unattend.CatalogLabel");
    public string CatalogDescription => Text("Unattend.CatalogDescription");
    public string ImportLabel => Text("Unattend.ImportLabel");
    public string RefreshLabel => Text("Unattend.RefreshLabel");
    public string CheckSourcesLabel => Text("Unattend.CheckSourcesLabel");
    public string RemoveLabel => Text("Unattend.RemoveLabel");
    public string RenameLabel => Text("Unattend.RenameLabel");
    public string DisplayNameLabel => Text("Unattend.DisplayNameLabel");
    public string SelectedFileLabel => Text("Unattend.SelectedFileLabel");
    public string DefaultLabel => Text("Unattend.DefaultLabel");
    public string DefaultDescription => Text("Unattend.DefaultDescription");
    public string OwnershipDescription => Text("Unattend.OwnershipDescription");
    public string CompatibilityDescription => Text("Unattend.CompatibilityDescription");
    public string ProtectionDescription => Text("Unattend.ProtectionDescription");

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportFilesAsync()
    {
        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            IReadOnlyList<string> paths = await filePickerService.PickOpenFilesAsync(
                new FileOpenPickerRequest(Text("Unattend.ImportPickerTitle"), (string[])[".xml"]));
            if (paths.Count == 0 || isDisposed)
            {
                return;
            }

            List<UnattendFileSettings> files = [.. configurationStateService.Current.Unattend.Files];
            List<string> failures = [];
            int duplicates = 0;
            foreach (string path in paths)
            {
                try
                {
                    UnattendFileSettings file = await Task.Run(() => UnattendFileService.Import(path));
                    if (files.Any(existing => existing.ContentHash.Equals(file.ContentHash, StringComparison.OrdinalIgnoreCase)))
                    {
                        duplicates++;
                    }
                    else
                    {
                        files.Add(file);
                    }
                }
                catch (Exception ex) when (IsSourceFailure(ex))
                {
                    failures.Add($"{Path.GetFileName(path)}: {GetSourceFailureMessage(ex)}");
                }
            }

            if (isDisposed)
            {
                return;
            }

            Save(configurationStateService.Current.Unattend with { Files = files.ToArray() });
            if (duplicates > 0)
            {
                failures.Add(localizationService.FormatString("Unattend.DuplicateMessage", duplicates));
            }

            StatusMessage = string.Join(Environment.NewLine, failures);
            await RefreshSourcesAsync();
        }
        catch (Exception ex) when (IsSourceFailure(ex))
        {
            StatusMessage = GetSourceFailureMessage(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task RefreshFileAsync()
    {
        if (SelectedFile is not { } selected)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            UnattendFileSettings imported = await Task.Run(() => UnattendFileService.Import(selected.SourcePath));
            if (isDisposed)
            {
                return;
            }

            UnattendSettings settings = configurationStateService.Current.Unattend;
            if (settings.Files.Any(file => file.Id != selected.Settings.Id &&
                file.ContentHash.Equals(imported.ContentHash, StringComparison.OrdinalIgnoreCase)))
            {
                StatusMessage = Text("Unattend.RefreshDuplicateMessage");
                return;
            }

            Save(settings with
            {
                Files = settings.Files.Select(file => file.Id == selected.Settings.Id
                    ? imported with { Id = file.Id, DisplayName = file.DisplayName }
                    : file).ToArray()
            });
            await RefreshSourcesAsync();
        }
        catch (Exception ex) when (IsSourceFailure(ex))
        {
            StatusMessage = GetSourceFailureMessage(ex);
            ApplyState();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task RemoveFileAsync()
    {
        if (SelectedFile is not { } selected)
        {
            return;
        }

        UnattendSettings settings = configurationStateService.Current.Unattend;
        // Preserve a removed default as an invalid reference until the user explicitly chooses its replacement.
        Save(settings with { Files = settings.Files.Where(file => file.Id != selected.Settings.Id).ToArray() });
        await RefreshSourcesAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameFileAsync()
    {
        if (SelectedFile is not { } selected || string.IsNullOrWhiteSpace(RenameText))
        {
            return;
        }

        UnattendSettings settings = configurationStateService.Current.Unattend;
        Save(settings with
        {
            Files = settings.Files.Select(file => file.Id == selected.Settings.Id
                ? file with { DisplayName = RenameText.Trim() }
                : file).ToArray()
        });
        await RefreshSourcesAsync();
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!isApplyingState)
        {
            Save(configurationStateService.Current.Unattend with { IsEnabled = value });
            _ = RefreshSourcesAsync();
        }
    }

    partial void OnSelectedDefaultChanged(UnattendDefaultOption? value)
    {
        if (!isApplyingState && value is not null)
        {
            Save(configurationStateService.Current.Unattend with { DefaultFileId = value.Id });
            _ = RefreshSourcesAsync();
        }
    }

    partial void OnSelectedFileChanged(UnattendFileEntryViewModel? value) => RenameText = value?.DisplayName ?? string.Empty;

    /// <summary>
    /// Refreshes source metadata asynchronously on page entry and explicit checks.
    /// The state service discards results from replaced catalogs and bounds concurrent source scans.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    public async Task RefreshSourcesAsync()
    {
        if (isDisposed || !IsEnabled)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await configurationStateService.RefreshUnattendSourcesAsync();
            if (!isDisposed)
            {
                ApplyState();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Save(UnattendSettings settings)
    {
        try
        {
            isApplyingState = true;
            configurationStateService.UpdateUnattend(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = Text("Unattend.SaveFailedMessage");
        }
        finally
        {
            isApplyingState = false;
            ApplyState();
        }
    }

    private void ApplyState()
    {
        string? selectedId = SelectedFile?.Settings.Id;
        isApplyingState = true;
        try
        {
            UnattendSettings settings = configurationStateService.Current.Unattend;
            IsEnabled = settings.IsEnabled;
            Files.Clear();
            DefaultOptions.Clear();
            DefaultOptions.Add(new UnattendDefaultOption(null, Text("Unattend.NativeOption")));
            foreach (UnattendFileSettings file in settings.Files)
            {
                Files.Add(CreateEntry(file));
                DefaultOptions.Add(new UnattendDefaultOption(file.Id, file.DisplayName));
            }

            if (settings.DefaultFileId is not null && !settings.Files.Any(file => file.Id == settings.DefaultFileId))
            {
                DefaultOptions.Add(new UnattendDefaultOption(settings.DefaultFileId, Text("Unattend.MissingDefaultOption")));
            }

            SelectedDefault = DefaultOptions.First(option => option.Id == settings.DefaultFileId);
            SelectedFile = Files.FirstOrDefault(file => file.Settings.Id == selectedId);
            UpdateReadiness(settings);
        }
        finally
        {
            isApplyingState = false;
        }
    }

    private UnattendFileEntryViewModel CreateEntry(UnattendFileSettings file)
    {
        UnattendSourceValidation? validation = configurationStateService.UnattendSourceValidations
            .FirstOrDefault(result => result.File == file);
        if (validation?.Inspection is not { } inspection)
        {
            return new UnattendFileEntryViewModel(file, string.Empty,
                validation?.ErrorMessage is { } errorMessage
                    ? LocalizeSourceMessage(errorMessage)
                    : Text("Unattend.SourceNotCheckedMessage"), false);
        }

        string status = Text("Unattend.ValidSource");
        if (inspection.HasCommands)
        {
            status += " " + Text("Unattend.CommandsWarning");
        }

        if (inspection.ConflictsWithAutopilot)
        {
            status += " " + Text("Unattend.AutopilotWarning");
        }

        return new UnattendFileEntryViewModel(file, string.Join(", ", inspection.Architectures), status, true);
    }

    private void UpdateReadiness(UnattendSettings settings)
    {
        HasReadinessIssue = false;
        ReadinessMessage = string.Empty;
        try
        {
            UnattendFileService.ValidateSettings(settings, configurationStateService.Current.General.DeploymentProtection.IsEnabled);
            if (settings.IsEnabled && Files.Any(file => !file.IsValid))
            {
                HasReadinessIssue = true;
                ReadinessMessage = Text("Unattend.InvalidSourcesMessage");
            }
        }
        catch (Exception ex) when (IsSourceFailure(ex))
        {
            HasReadinessIssue = true;
            ReadinessMessage = GetSourceFailureMessage(ex);
        }
    }

    private static bool IsSourceFailure(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException;

    private string GetSourceFailureMessage(Exception ex) => ex is InvalidDataException or InvalidOperationException
        ? LocalizeSourceMessage(ex.Message)
        : Text("Unattend.SourceReadFailedMessage");

    private string LocalizeSourceMessage(string message) => Text(message switch
    {
        "Custom answer files require deployment media password protection." => "Unattend.ProtectionRequiredMessage",
        "Import at least one answer file before enabling custom answer files." => "Unattend.EmptyCatalogMessage",
        "The default answer file is missing from the catalog. Select an available default." => "Unattend.MissingDefaultOption",
        "The answer-file catalog is invalid." or
        "The answer-file catalog contains invalid or duplicate entries." or
        "The answer-file source metadata is invalid." or
        "The answer-file identifier is invalid." => "Unattend.InvalidCatalogMessage",
        "The answer-file source changed. Refresh the imported file before building media." => "Unattend.SourceChangedMessage",
        "Source validation timed out. Check the source location and refresh again." => "Unattend.SourceTimeoutMessage",
        "Two source checks are still waiting for file access. Restore the unavailable source locations, then check sources again." => "Unattend.SourceChecksBusyMessage",
        "The answer file or selected image declares an unsupported architecture." or
        "The answer file has no supported component settings applicable to the selected architecture." => "Unattend.InvalidArchitectureMessage",
        "Audit-mode resealing is not supported for normal Windows deployment." => "Unattend.UnsupportedSettingsMessage",
        _ when message.StartsWith("The answer file contains unsupported ", StringComparison.Ordinal) => "Unattend.UnsupportedSettingsMessage",
        "The answer file must use the Windows unattend root and namespace." or
        "The answer file exceeds the 4 MiB limit." or
        "The answer file must contain XML and be no larger than 4 MiB." or
        "The answer file contains invalid or prohibited XML. Validate it with Windows System Image Manager." => "Unattend.InvalidFileMessage",
        _ => "Unattend.SourceReadFailedMessage"
    });

    private string Text(string key) => localizationService.GetString(key);

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (!isApplyingState && !isDisposed)
        {
            ApplyState();
        }
    }

    private void OnLanguageChanged(object? sender, ApplicationLanguageChangedEventArgs e)
    {
        ApplyState();
        OnPropertyChanged(string.Empty);
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        configurationStateService.StateChanged -= OnStateChanged;
        localizationService.LanguageChanged -= OnLanguageChanged;
    }
}
