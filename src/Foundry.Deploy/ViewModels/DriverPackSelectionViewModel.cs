// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Foundry.Deploy.Models;
using Foundry.Deploy.Services.DriverPacks;
using Foundry.Deploy.Services.Localization;

namespace Foundry.Deploy.ViewModels;

public sealed partial class DriverPackSelectionViewModel : LocalizedViewModelBase
{
    private const string NoneDriverPackOptionKey = "none";
    private const string MicrosoftUpdateCatalogDriverPackOptionKey = "microsoft-update-catalog";
    private const string DellDriverPackOptionKey = "oem:dell";
    private const string LenovoDriverPackOptionKey = "oem:lenovo";
    private const string HpDriverPackOptionKey = "oem:hp";
    private const string MicrosoftOemDriverPackOptionKey = "oem:microsoft";

    private readonly IDriverPackSelectionService _driverPackSelectionService;
    private HardwareProfile? _detectedHardware;
    private OperatingSystemCatalogItem? _selectedOperatingSystem;
    private string _effectiveArchitecture;
    private bool _isUpdatingDriverPackOptionSelection;
    private bool _isUpdatingDriverPackDetails;
    private bool _hasUserSelectedDriverPackOption;
    private bool _hasUserSelectedDriverPackDetails;
    private DriverPackCatalogItem? _manuallySelectedDriverPack;

    public DriverPackSelectionViewModel(
        IDriverPackSelectionService driverPackSelectionService,
        ILocalizationService localizationService)
        : this(driverPackSelectionService, localizationService, string.Empty)
    {
    }

    public DriverPackSelectionViewModel(
        IDriverPackSelectionService driverPackSelectionService,
        ILocalizationService localizationService,
        string initialArchitecture)
        : base(localizationService)
    {
        _driverPackSelectionService = driverPackSelectionService ?? throw new ArgumentNullException(nameof(driverPackSelectionService));
        _effectiveArchitecture = NormalizeArchitecture(initialArchitecture);
        LocalizationService.LanguageChanged += OnLocalizationLanguageChanged;
    }

    public event EventHandler? StateChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOemDriverSourceSelected))]
    [NotifyPropertyChangedFor(nameof(IsDriverPackModelSelectionEnabled))]
    [NotifyPropertyChangedFor(nameof(IsDriverPackVersionSelectionEnabled))]
    [NotifyPropertyChangedFor(nameof(SelectedDriverPackSelectionDisplay))]
    private DriverPackOptionItem? selectedDriverPackOption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDriverPackSelectionDisplay))]
    private string selectedDriverPackModel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDriverPackSelectionDisplay))]
    private string selectedDriverPackVersion = string.Empty;

    public ObservableCollection<DriverPackCatalogItem> DriverPacks { get; } = [];

    public ObservableCollection<DriverPackOptionItem> DriverPackOptions { get; } = [];

    public ObservableCollection<string> DriverPackModelOptions { get; } = [];

    public ObservableCollection<string> DriverPackVersionOptions { get; } = [];

    public DriverPackSelectionKind EffectiveSelectionKind => GetEffectiveSelectionKind();

    public bool IsOemDriverSourceSelected => SelectedDriverPackOption?.Kind == DriverPackSelectionKind.OemCatalog;

    /// <summary>Indicates an explicit model or version choice whose applicability requires operator verification.</summary>
    public bool IsManualDriverPackSelection => IsOemDriverSourceSelected && _hasUserSelectedDriverPackDetails;

    public bool IsDriverPackModelSelectionEnabled => IsOemDriverSourceSelected && DriverPackModelOptions.Count > 0;

    public bool IsDriverPackVersionSelectionEnabled => IsDriverPackModelSelectionEnabled && DriverPackVersionOptions.Count > 0;

    public string SelectedDriverPackSelectionDisplay => BuildSelectedDriverPackSelectionDisplay();

    public void ReplaceCatalog(IReadOnlyList<DriverPackCatalogItem> driverPacks)
    {
        ArgumentNullException.ThrowIfNull(driverPacks);

        DriverPacks.Clear();
        foreach (DriverPackCatalogItem item in driverPacks)
        {
            DriverPacks.Add(item);
        }

        RefreshDriverPackOptions();
    }

    public void UpdateSelectionContext(
        HardwareProfile? detectedHardware,
        OperatingSystemCatalogItem? selectedOperatingSystem,
        string effectiveArchitecture)
    {
        _detectedHardware = detectedHardware;
        _selectedOperatingSystem = selectedOperatingSystem;
        _effectiveArchitecture = NormalizeArchitecture(effectiveArchitecture);
        RefreshDriverPackOptions();
    }

    public void SetDetectedHardware(HardwareProfile? detectedHardware)
    {
        _detectedHardware = detectedHardware;
        RefreshDriverPackOptions();
    }

    public void SetOperatingSystemContext(OperatingSystemCatalogItem? selectedOperatingSystem, string effectiveArchitecture)
    {
        _selectedOperatingSystem = selectedOperatingSystem;
        _effectiveArchitecture = NormalizeArchitecture(effectiveArchitecture);
        RefreshDriverPackOptions();
    }

    public DriverPackSelectionKind GetEffectiveSelectionKind()
    {
        return SelectedDriverPackOption?.Kind ?? DriverPackSelectionKind.None;
    }

    public bool HasValidSelection()
    {
        return GetEffectiveSelectionKind() != DriverPackSelectionKind.OemCatalog ||
               ResolveEffectiveDriverPackSelection() is not null;
    }

    partial void OnSelectedDriverPackOptionChanged(DriverPackOptionItem? value)
    {
        if (_isUpdatingDriverPackOptionSelection)
        {
            return;
        }

        _hasUserSelectedDriverPackOption = true;
        _hasUserSelectedDriverPackDetails = false;
        _manuallySelectedDriverPack = null;
        RefreshDriverPackModelAndVersionOptions();
    }

    partial void OnSelectedDriverPackModelChanged(string value)
    {
        if (!_isUpdatingDriverPackDetails)
        {
            _hasUserSelectedDriverPackOption = true;
            _hasUserSelectedDriverPackDetails = true;
            _manuallySelectedDriverPack = null;
        }

        RefreshDriverPackVersionOptions();
        if (!_isUpdatingDriverPackDetails)
        {
            _manuallySelectedDriverPack = ResolveManualSelectionFromDetails();
            NotifyDriverPackSelectionStateChanged();
        }
    }

    partial void OnSelectedDriverPackVersionChanged(string value)
    {
        if (!_isUpdatingDriverPackDetails)
        {
            _hasUserSelectedDriverPackOption = true;
            _hasUserSelectedDriverPackDetails = true;
            _manuallySelectedDriverPack = ResolveManualSelectionFromDetails();
        }

        NotifyDriverPackSelectionStateChanged();
    }

    public DriverPackCatalogItem? ResolveEffectiveDriverPackSelection()
    {
        DriverPackSelectionKind selectionKind = GetEffectiveSelectionKind();
        if (selectionKind != DriverPackSelectionKind.OemCatalog)
        {
            return SelectedDriverPackOption?.DriverPack;
        }

        DriverPackCatalogItem[] sourceCandidates = BuildSourceDriverPackCandidates();
        if (sourceCandidates.Length == 0)
        {
            return null;
        }

        if (!_hasUserSelectedDriverPackDetails)
        {
            return _detectedHardware is not null && _selectedOperatingSystem is not null
                ? _driverPackSelectionService.SelectBest(sourceCandidates, _detectedHardware, _selectedOperatingSystem).DriverPack
                : null;
        }

        if (string.IsNullOrWhiteSpace(SelectedDriverPackModel) || string.IsNullOrWhiteSpace(SelectedDriverPackVersion))
        {
            return null;
        }

        return sourceCandidates.FirstOrDefault(IsManuallySelectedPackage);
    }

    private DriverPackCatalogItem? ResolveManualSelectionFromDetails()
    {
        if (string.IsNullOrWhiteSpace(SelectedDriverPackVersion))
        {
            return null;
        }

        DriverPackCatalogItem[] modelCandidates = FilterDriverPackCandidatesBySelectedModel(BuildSourceDriverPackCandidates());
        if (modelCandidates.Length == 0)
        {
            return null;
        }

        DriverPackCatalogItem[] versionCandidates = modelCandidates
            .Where(item => GetDriverPackVersionDisplay(item).Equals(SelectedDriverPackVersion.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return SortDriverPackCandidates(versionCandidates, _selectedOperatingSystem?.ReleaseId ?? string.Empty)
            .FirstOrDefault();
    }

    public DriverPackCatalogItem? ResolveEffectiveSelection()
    {
        return ResolveEffectiveDriverPackSelection();
    }

    private void RefreshDriverPackOptions()
    {
        string previousKey = _hasUserSelectedDriverPackOption
            ? SelectedDriverPackOption?.Key ?? string.Empty
            : string.Empty;
        DriverPackOptionItem[] options = BuildDriverPackOptions();

        _isUpdatingDriverPackOptionSelection = true;
        try
        {
            DriverPackOptions.Clear();
            foreach (DriverPackOptionItem option in options)
            {
                DriverPackOptions.Add(option);
            }

            DriverPackOptionItem? selected = null;
            if (!string.IsNullOrWhiteSpace(previousKey))
            {
                selected = options.FirstOrDefault(option =>
                    option.Key.Equals(previousKey, StringComparison.OrdinalIgnoreCase));
            }

            selected ??= ResolveDefaultDriverPackOption(options);
            SelectedDriverPackOption = selected;
        }
        finally
        {
            _isUpdatingDriverPackOptionSelection = false;
        }

        RefreshDriverPackModelAndVersionOptions();
    }

    private DriverPackOptionItem[] BuildDriverPackOptions()
    {
        return
        [
            CreateNoneDriverPackOption(),
            CreateMicrosoftUpdateCatalogOption(),
            CreateOemDriverPackOption(DellDriverPackOptionKey, "Dell"),
            CreateOemDriverPackOption(LenovoDriverPackOptionKey, "Lenovo"),
            CreateOemDriverPackOption(HpDriverPackOptionKey, "HP"),
            CreateOemDriverPackOption(MicrosoftOemDriverPackOptionKey, "Microsoft")
        ];
    }

    private DriverPackCatalogItem[] BuildSourceDriverPackCandidates()
    {
        if (!IsOemDriverSourceSelected)
        {
            return [];
        }

        string sourceManufacturer = ResolveManufacturerFromSourceOptionKey(SelectedDriverPackOption?.Key ?? string.Empty);
        if (string.IsNullOrWhiteSpace(sourceManufacturer))
        {
            return [];
        }

        return BuildFilteredDriverPackCandidates(forceManufacturer: sourceManufacturer);
    }

    private void RefreshDriverPackModelAndVersionOptions()
    {
        bool wasUpdating = _isUpdatingDriverPackDetails;
        _isUpdatingDriverPackDetails = true;
        try
        {
            RefreshDriverPackModelAndVersionOptionsCore();
        }
        finally
        {
            _isUpdatingDriverPackDetails = wasUpdating;
        }
    }

    private void RefreshDriverPackModelAndVersionOptionsCore()
    {
        string previousModel = _hasUserSelectedDriverPackDetails ? SelectedDriverPackModel : string.Empty;

        DriverPackModelOptions.Clear();
        DriverPackVersionOptions.Clear();
        SelectedDriverPackModel = string.Empty;
        SelectedDriverPackVersion = string.Empty;

        if (!IsOemDriverSourceSelected)
        {
            NotifyDriverPackSelectionStateChanged();
            return;
        }

        DriverPackCatalogItem[] sourceCandidates = BuildSourceDriverPackCandidates();
        if (_hasUserSelectedDriverPackDetails)
        {
            _manuallySelectedDriverPack = sourceCandidates.FirstOrDefault(IsManuallySelectedPackage);
        }
        string[] models = sourceCandidates
            .SelectMany(GetSelectableModelNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string model in models)
        {
            DriverPackModelOptions.Add(model);
        }

        if (models.Length > 0)
        {
            string preferredModel = string.Empty;
            if (!_hasUserSelectedDriverPackDetails)
            {
                preferredModel = ResolvePreferredModelFromHardware(sourceCandidates, models);
            }
            else if (_manuallySelectedDriverPack is not null)
            {
                string[] selectedModels = GetSelectableModelNames(_manuallySelectedDriverPack);
                preferredModel = selectedModels.FirstOrDefault(model => model.Equals(previousModel, StringComparison.OrdinalIgnoreCase))
                    ?? selectedModels.FirstOrDefault() ?? string.Empty;
            }

            SelectedDriverPackModel = preferredModel;
            if (_hasUserSelectedDriverPackDetails)
            {
                SelectedDriverPackVersion = _manuallySelectedDriverPack is null
                    ? string.Empty : GetDriverPackVersionDisplay(_manuallySelectedDriverPack);
            }
        }

        NotifyDriverPackSelectionStateChanged();
    }

    private void RefreshDriverPackVersionOptions()
    {
        bool wasUpdating = _isUpdatingDriverPackDetails;
        _isUpdatingDriverPackDetails = true;
        try
        {
            RefreshDriverPackVersionOptionsCore();
        }
        finally
        {
            _isUpdatingDriverPackDetails = wasUpdating;
        }
    }

    private void RefreshDriverPackVersionOptionsCore()
    {
        string previousVersion = _hasUserSelectedDriverPackDetails ? SelectedDriverPackVersion : string.Empty;
        DriverPackVersionOptions.Clear();
        SelectedDriverPackVersion = string.Empty;

        if (!IsOemDriverSourceSelected || string.IsNullOrWhiteSpace(SelectedDriverPackModel))
        {
            NotifyDriverPackSelectionStateChanged();
            return;
        }

        DriverPackCatalogItem[] modelCandidates = FilterDriverPackCandidatesBySelectedModel(BuildSourceDriverPackCandidates());
        DriverPackCatalogItem[] orderedCandidates = SortDriverPackCandidates(modelCandidates, _selectedOperatingSystem?.ReleaseId ?? string.Empty);

        string[] versions = orderedCandidates
            .Select(GetDriverPackVersionDisplay)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string version in versions)
        {
            DriverPackVersionOptions.Add(version);
        }

        if (versions.Length > 0)
        {
            DriverPackCatalogItem? selectedPackage = _hasUserSelectedDriverPackDetails
                ? modelCandidates.FirstOrDefault(IsManuallySelectedPackage)
                : _detectedHardware is not null && _selectedOperatingSystem is not null
                    ? _driverPackSelectionService.SelectBest(modelCandidates, _detectedHardware, _selectedOperatingSystem).DriverPack
                    : null;

            SelectedDriverPackVersion = selectedPackage is not null
                ? GetDriverPackVersionDisplay(selectedPackage)
                : versions.FirstOrDefault(version => version.Equals(previousVersion, StringComparison.OrdinalIgnoreCase))
                    ?? versions[0];
        }

        NotifyDriverPackSelectionStateChanged();
    }

    /// <summary>Preserves package provenance and applicability across catalog reloads, independently of revision and display labels.</summary>
    private bool IsManuallySelectedPackage(DriverPackCatalogItem candidate)
    {
        DriverPackCatalogItem? selected = _manuallySelectedDriverPack;
        return selected is not null && candidate.Id == selected.Id && candidate.DownloadUrl == selected.DownloadUrl &&
            candidate.PackageId == selected.PackageId && candidate.FileName == selected.FileName && candidate.Version == selected.Version &&
            candidate.SizeBytes == selected.SizeBytes && candidate.Sha256.Equals(selected.Sha256, StringComparison.OrdinalIgnoreCase) &&
            candidate.PackageRole == selected.PackageRole &&
            NormalizePackageIdentityText(candidate.Manufacturer) == NormalizePackageIdentityText(selected.Manufacturer) &&
            NormalizePackageIdentityText(candidate.Type) == NormalizePackageIdentityText(selected.Type) &&
            NormalizePackageIdentityText(candidate.Format) == NormalizePackageIdentityText(selected.Format) &&
            NormalizePackageIdentityText(candidate.OsName) == NormalizePackageIdentityText(selected.OsName) &&
            NormalizePackageIdentityText(candidate.OsReleaseId) == NormalizePackageIdentityText(selected.OsReleaseId) &&
            NormalizeArchitecture(candidate.OsArchitecture) == NormalizeArchitecture(selected.OsArchitecture) &&
            HaveSamePackageIdentityValues(candidate.ModelNames, selected.ModelNames) &&
            HaveSamePackageIdentityValues(candidate.SystemIds, selected.SystemIds);
    }

    private static bool HaveSamePackageIdentityValues(IEnumerable<string> candidate, IEnumerable<string> selected)
    {
        return candidate.Select(NormalizePackageIdentityText).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .SequenceEqual(selected.Select(NormalizePackageIdentityText).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    private static string NormalizePackageIdentityText(string value)
    {
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }

    private DriverPackCatalogItem[] FilterDriverPackCandidatesBySelectedModel(IEnumerable<DriverPackCatalogItem> candidates)
    {
        if (string.IsNullOrWhiteSpace(SelectedDriverPackModel))
        {
            return [];
        }

        string selectedModel = SelectedDriverPackModel.Trim();
        return candidates
            .Where(item => GetSelectableModelNames(item).Any(model =>
                model.Equals(selectedModel, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private string ResolvePreferredModelFromHardware(
        IReadOnlyList<DriverPackCatalogItem> sourceCandidates,
        IReadOnlyList<string> modelOptions)
    {
        if (modelOptions.Count == 0)
        {
            return string.Empty;
        }

        if (_detectedHardware is null || _selectedOperatingSystem is null)
        {
            return string.Empty;
        }

        DriverPackCatalogItem? bestPackMatch = _driverPackSelectionService
            .SelectBest(sourceCandidates, _detectedHardware, _selectedOperatingSystem)
            .DriverPack;

        if (bestPackMatch is not null)
        {
            string? modelFromPack = GetSelectableModelNames(bestPackMatch)
                .FirstOrDefault(model => modelOptions.Any(option =>
                    option.Equals(model, StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrWhiteSpace(modelFromPack))
            {
                return modelFromPack;
            }
        }

        return string.Empty;
    }

    private DriverPackCatalogItem[] BuildFilteredDriverPackCandidates(string forceManufacturer = "")
    {
        IEnumerable<DriverPackCatalogItem> query = DriverPacks;

        string architecture = NormalizeArchitecture(_selectedOperatingSystem?.Architecture ?? _effectiveArchitecture);
        if (!string.IsNullOrWhiteSpace(architecture))
        {
            query = query.Where(item => IsArchitectureMatch(architecture, item.OsArchitecture));
        }

        string selectedOsRelease = _selectedOperatingSystem?.WindowsRelease?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(selectedOsRelease))
        {
            string windowsLabel = $"Windows {selectedOsRelease}";
            query = query.Where(item =>
                item.OsName.Contains(windowsLabel, StringComparison.OrdinalIgnoreCase) ||
                item.OsName.Contains(selectedOsRelease, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(forceManufacturer))
        {
            query = query.Where(item =>
                NormalizeManufacturer(item.Manufacturer).Equals(forceManufacturer, StringComparison.OrdinalIgnoreCase));
        }

        DriverPackCatalogItem[] baseCandidates = query.ToArray();
        return SortDriverPackCandidates(baseCandidates, _selectedOperatingSystem?.ReleaseId ?? string.Empty);
    }

    private DriverPackOptionItem? ResolveDefaultDriverPackOption(IReadOnlyList<DriverPackOptionItem> options)
    {
        if (options.Count == 0)
        {
            return null;
        }

        if (_detectedHardware?.IsVirtualMachine == true)
        {
            return options.FirstOrDefault(option => option.Kind == DriverPackSelectionKind.None)
                   ?? options[0];
        }

        if (_detectedHardware is not null && _selectedOperatingSystem is not null && DriverPacks.Count > 0)
        {
            DriverPackSelectionResult selection = _driverPackSelectionService.SelectBest(
                DriverPacks.ToArray(),
                _detectedHardware,
                _selectedOperatingSystem);

            if (selection.DriverPack is not null)
            {
                string selectedKey = ResolveSourceOptionKey(selection.DriverPack.Manufacturer);
                DriverPackOptionItem? oemMatch = options.FirstOrDefault(option =>
                    option.Key.Equals(selectedKey, StringComparison.OrdinalIgnoreCase));

                if (oemMatch is not null)
                {
                    return oemMatch;
                }
            }
        }

        return options.FirstOrDefault(option => option.Kind == DriverPackSelectionKind.MicrosoftUpdateCatalog)
               ?? options[0];
    }

    private DriverPackOptionItem CreateNoneDriverPackOption()
    {
        return new DriverPackOptionItem
        {
            Key = NoneDriverPackOptionKey,
            DisplayName = GetString("Common.None"),
            Kind = DriverPackSelectionKind.None,
            DriverPack = null
        };
    }

    private DriverPackOptionItem CreateMicrosoftUpdateCatalogOption()
    {
        return new DriverPackOptionItem
        {
            Key = MicrosoftUpdateCatalogDriverPackOptionKey,
            DisplayName = GetString("DriverPack.MicrosoftUpdateCatalog"),
            Kind = DriverPackSelectionKind.MicrosoftUpdateCatalog,
            DriverPack = null
        };
    }

    private static DriverPackOptionItem CreateOemDriverPackOption(string key, string displayName)
    {
        return new DriverPackOptionItem
        {
            Key = key,
            DisplayName = displayName,
            Kind = DriverPackSelectionKind.OemCatalog,
            DriverPack = null
        };
    }

    private string BuildSelectedDriverPackSelectionDisplay()
    {
        DriverPackSelectionKind selectionKind = GetEffectiveSelectionKind();
        if (selectionKind == DriverPackSelectionKind.None)
        {
            return GetString("Common.None");
        }

        if (selectionKind == DriverPackSelectionKind.MicrosoftUpdateCatalog)
        {
            return GetString("DriverPack.MicrosoftUpdateCatalog");
        }

        DriverPackCatalogItem? selectedPack = ResolveEffectiveDriverPackSelection();
        if (selectedPack is null)
        {
            string sourceName = SelectedDriverPackOption?.DisplayName ?? GetString("DriverPack.Oem");
            return Format("DriverPack.NoMatchingModelVersionFormat", sourceName);
        }

        string modelName = string.IsNullOrWhiteSpace(SelectedDriverPackModel)
            ? ResolveDriverPackFriendlyName(selectedPack)
            : SelectedDriverPackModel;
        string version = GetDriverPackVersionDisplay(selectedPack);

        return $"{selectedPack.Manufacturer} | {modelName} | {version}";
    }

    private void NotifyDriverPackSelectionStateChanged()
    {
        OnPropertyChanged(nameof(IsOemDriverSourceSelected));
        OnPropertyChanged(nameof(IsManualDriverPackSelection));
        OnPropertyChanged(nameof(IsDriverPackModelSelectionEnabled));
        OnPropertyChanged(nameof(IsDriverPackVersionSelectionEnabled));
        OnPropertyChanged(nameof(SelectedDriverPackSelectionDisplay));
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string ResolveManufacturerFromSourceOptionKey(string optionKey)
    {
        return optionKey.Trim().ToLowerInvariant() switch
        {
            DellDriverPackOptionKey => "dell",
            LenovoDriverPackOptionKey => "lenovo",
            HpDriverPackOptionKey => "hp",
            MicrosoftOemDriverPackOptionKey => "microsoft",
            _ => string.Empty
        };
    }

    private static string ResolveSourceOptionKey(string manufacturer)
    {
        string normalized = NormalizeManufacturer(manufacturer);
        return normalized switch
        {
            "dell" => DellDriverPackOptionKey,
            "lenovo" => LenovoDriverPackOptionKey,
            "hp" => HpDriverPackOptionKey,
            "microsoft" => MicrosoftOemDriverPackOptionKey,
            _ => string.Empty
        };
    }

    private static string[] GetSelectableModelNames(DriverPackCatalogItem driverPack)
    {
        string[] models = driverPack.ModelNames
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (models.Length > 0)
        {
            return models;
        }

        string fallback = ResolveDriverPackFriendlyName(driverPack);
        return string.IsNullOrWhiteSpace(fallback)
            ? []
            : [fallback];
    }

    private static string GetDriverPackVersionDisplay(DriverPackCatalogItem driverPack)
    {
        if (!string.IsNullOrWhiteSpace(driverPack.Version))
        {
            return driverPack.Version.Trim();
        }

        if (driverPack.ReleaseDate is not null)
        {
            string releaseDate = driverPack.ReleaseDate.Value.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
            return string.IsNullOrWhiteSpace(driverPack.OsReleaseId)
                ? releaseDate
                : $"{releaseDate} ({driverPack.OsReleaseId.Trim()})";
        }

        if (!string.IsNullOrWhiteSpace(driverPack.PackageId))
        {
            return driverPack.PackageId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(driverPack.FileName))
        {
            return Path.GetFileNameWithoutExtension(driverPack.FileName.Trim());
        }

        return LocalizationText.GetString("Common.Unknown");
    }

    private static string ResolveDriverPackFriendlyName(DriverPackCatalogItem driverPack)
    {
        string[] models = driverPack.ModelNames
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (models.Length > 0)
        {
            return models.Length == 1
                ? models[0]
                : LocalizationText.Format("DriverPack.MultiModelFormat", models[0], models.Length - 1);
        }

        if (!LooksLikeArchiveOrInstallerName(driverPack.Name))
        {
            return driverPack.Name.Trim();
        }

        if (!LooksLikeArchiveOrInstallerName(driverPack.PackageId))
        {
            return driverPack.PackageId.Trim();
        }

        string fallback = !string.IsNullOrWhiteSpace(driverPack.FileName)
            ? driverPack.FileName
            : driverPack.Name;

        return Path.GetFileNameWithoutExtension(fallback).Trim();
    }

    private static bool LooksLikeArchiveOrInstallerName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string extension = Path.GetExtension(value.Trim());
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cab", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".7z", StringComparison.OrdinalIgnoreCase);
    }

    private static DriverPackCatalogItem[] SortDriverPackCandidates(
        IEnumerable<DriverPackCatalogItem> candidates,
        string targetReleaseId)
    {
        int targetReleaseRank = WindowsReleaseId.GetSortRank(targetReleaseId);
        return candidates
            .OrderByDescending(item => GetDriverPackReleaseSortRank(item, targetReleaseRank))
            .ThenByDescending(item => item.ReleaseDate ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Manufacturer, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int GetDriverPackReleaseSortRank(DriverPackCatalogItem driverPack, int targetReleaseRank)
    {
        int releaseRank = WindowsReleaseId.GetSortRank(driverPack.OsReleaseId);
        if (targetReleaseRank > 0 && releaseRank <= targetReleaseRank)
        {
            return releaseRank;
        }

        return 0;
    }

    private static bool IsArchitectureMatch(string osArchitecture, string driverArchitecture)
    {
        string os = NormalizeArchitecture(osArchitecture);
        string driver = NormalizeArchitecture(driverArchitecture);
        return os.Equals(driver, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeArchitecture(string architecture)
    {
        string normalized = architecture.Trim().ToLowerInvariant();
        return normalized switch
        {
            "amd64" => "x64",
            "x64" => "x64",
            "arm64" => "arm64",
            "aarch64" => "arm64",
            _ => normalized
        };
    }

    private static string NormalizeManufacturer(string manufacturer)
    {
        string normalized = manufacturer.Trim().ToLowerInvariant();
        if (normalized.Contains("hewlett") || normalized == "hp")
        {
            return "hp";
        }

        if (normalized.Contains("dell"))
        {
            return "dell";
        }

        if (normalized.Contains("lenovo"))
        {
            return "lenovo";
        }

        if (normalized.Contains("microsoft"))
        {
            return "microsoft";
        }

        return normalized;
    }

    public override void Dispose()
    {
        LocalizationService.LanguageChanged -= OnLocalizationLanguageChanged;
        base.Dispose();
    }

    private void OnLocalizationLanguageChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            RefreshDriverPackOptions();
            OnPropertyChanged(nameof(SelectedDriverPackSelectionDisplay));
        });
    }

    private string GetString(string key)
    {
        return Strings[key];
    }

    private string Format(string key, params object[] args)
    {
        return string.Format(LocalizationService.CurrentCulture, GetString(key), args);
    }
}
