// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;
using Foundry.Deploy.Services.DriverPacks;
using Foundry.Deploy.Services.Localization;
using Foundry.Deploy.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class DriverPackSelectionViewModelTests
{
    [Fact]
    public void EffectiveSelectionKind_WhenDetectedHardwareIsVirtualMachine_DefaultsToNone()
    {
        var viewModel = new DriverPackSelectionViewModel(
            new DriverPackSelectionService(NullLogger<DriverPackSelectionService>.Instance),
            new LocalizationService(),
            "x64");
        HardwareProfile hardware = new()
        {
            Manufacturer = "Microsoft Corporation",
            Model = "Virtual Machine",
            Product = "Virtual Machine",
            IsVirtualMachine = true
        };
        OperatingSystemCatalogItem operatingSystem = new()
        {
            WindowsRelease = "11",
            ReleaseId = "25H2",
            Architecture = "x64"
        };

        viewModel.UpdateSelectionContext(hardware, operatingSystem, "x64");
        viewModel.ReplaceCatalog([]);

        Assert.Equal(DriverPackSelectionKind.None, viewModel.EffectiveSelectionKind);
    }

    [Fact]
    public void ResolveEffectiveSelection_WhenNoExactReleaseExists_RequiresExplicitOemModelSelection()
    {
        var viewModel = new DriverPackSelectionViewModel(
            new DriverPackSelectionService(NullLogger<DriverPackSelectionService>.Instance),
            new LocalizationService(),
            "x64");
        HardwareProfile hardware = new()
        {
            Manufacturer = "Lenovo",
            Model = "ThinkPad X13 Yoga Gen 3 Type 21AW 21AX",
            Product = "21AW",
            Architecture = "x64"
        };
        OperatingSystemCatalogItem operatingSystem = new()
        {
            WindowsRelease = "11",
            ReleaseId = "25H2",
            Architecture = "x64"
        };
        DateTimeOffset catalogDate = new(2024, 06, 13, 0, 0, 0, TimeSpan.Zero);

        viewModel.UpdateSelectionContext(hardware, operatingSystem, "x64");
        viewModel.ReplaceCatalog(
        [
            CreateCatalogItem("21h2", "21H2", catalogDate),
            CreateCatalogItem("22h2", "22H2", catalogDate),
            CreateCatalogItem("23h2", "23H2", catalogDate)
        ]);

        DriverPackCatalogItem? selected = viewModel.ResolveEffectiveSelection();

        Assert.Equal(DriverPackSelectionKind.MicrosoftUpdateCatalog, viewModel.EffectiveSelectionKind);
        Assert.Null(selected);
        Assert.False(viewModel.IsManualDriverPackSelection);

        viewModel.SelectedDriverPackOption = viewModel.DriverPackOptions.Single(option => option.Key == "oem:lenovo");

        Assert.Empty(viewModel.SelectedDriverPackModel);
        Assert.False(viewModel.HasValidSelection());
        Assert.Null(viewModel.ResolveEffectiveSelection());

        viewModel.SelectedDriverPackModel = "ThinkPad X13 Yoga Gen 3 Type 21AW 21AX";

        Assert.True(viewModel.HasValidSelection());
        Assert.True(viewModel.IsManualDriverPackSelection);
        Assert.Equal("23h2", viewModel.ResolveEffectiveSelection()?.Id);
        Assert.Equal("ThinkPad X13 Yoga Gen 3 Type 21AW 21AX", hardware.Model);
        Assert.Equal("21AW", hardware.Product);
    }

    [Fact]
    public void ResolveEffectiveSelection_WhenLenovoModelsShareMarketingName_SelectsMatchingMachineType()
    {
        var viewModel = new DriverPackSelectionViewModel(
            new DriverPackSelectionService(NullLogger<DriverPackSelectionService>.Instance),
            new LocalizationService(),
            "x64");
        HardwareProfile hardware = new()
        {
            Manufacturer = "Lenovo",
            Model = "21Y6000JMX",
            Product = "ThinkPad E14 Gen 8",
            Architecture = "x64"
        };
        OperatingSystemCatalogItem operatingSystem = new()
        {
            WindowsRelease = "11",
            ReleaseId = "25H2",
            Architecture = "x64"
        };

        viewModel.UpdateSelectionContext(hardware, operatingSystem, "x64");
        viewModel.ReplaceCatalog(
        [
            CreateCatalogItem(
                "21y2-21y3",
                "25H2",
                new DateTimeOffset(2026, 05, 19, 0, 0, 0, TimeSpan.Zero),
                "ThinkPad E14 Gen 8 Type 21Y2 21Y3",
                ["21Y2", "21Y3"]),
            CreateCatalogItem(
                "21y6-21y7",
                "25H2",
                new DateTimeOffset(2026, 04, 28, 0, 0, 0, TimeSpan.Zero),
                "ThinkPad E14 Gen 8 Type 21Y6 21Y7",
                ["21Y6", "21Y7"])
        ]);

        DriverPackCatalogItem? selected = viewModel.ResolveEffectiveSelection();

        Assert.Equal("ThinkPad E14 Gen 8 Type 21Y6 21Y7", viewModel.SelectedDriverPackModel);
        Assert.Equal("21y6-21y7", selected?.Id);
    }

    [Fact]
    public void ResolveEffectiveSelection_WhenPacksShareModelAndVersion_RetainsCompatibleMachineType()
    {
        using var viewModel = CreateViewModel();
        HardwareProfile hardware = new()
        {
            Manufacturer = "Lenovo",
            Model = "21Y6000JMX",
            Product = "ThinkPad E14 Gen 8",
            Architecture = "x64"
        };
        viewModel.UpdateSelectionContext(hardware, CreateOperatingSystem(), "x64");
        viewModel.ReplaceCatalog(
        [
            CreateCatalogItem("wrong-type", "25H2", new DateTimeOffset(2026, 5, 19, 0, 0, 0, TimeSpan.Zero), "ThinkPad E14 Gen 8", ["21Y2"]) with { Version = "1.0" },
            CreateCatalogItem("matching-type", "25H2", new DateTimeOffset(2026, 4, 28, 0, 0, 0, TimeSpan.Zero), "ThinkPad E14 Gen 8", ["21Y6"]) with { Version = "1.0" }
        ]);

        Assert.Equal("matching-type", viewModel.ResolveEffectiveSelection()?.Id);
        Assert.Equal("21Y6000JMX", hardware.Model);
        Assert.False(viewModel.IsManualDriverPackSelection);

        viewModel.SelectedDriverPackOption = viewModel.DriverPackOptions.Single(option => option.Key == "none");
        viewModel.SelectedDriverPackOption = viewModel.DriverPackOptions.Single(option => option.Key == "oem:lenovo");

        Assert.Equal("matching-type", viewModel.ResolveEffectiveSelection()?.Id);
    }

    [Fact]
    public void ResolveEffectiveSelection_AfterHardwareContextChanges_DiscardsPreviousAutomaticModel()
    {
        using var viewModel = CreateViewModel();
        HardwareProfile hardware = new()
        {
            Manufacturer = "Lenovo",
            Model = "21Y6000JMX",
            Product = "ThinkPad E14 Gen 8",
            Architecture = "x64"
        };
        viewModel.UpdateSelectionContext(hardware, CreateOperatingSystem(), "x64");
        viewModel.ReplaceCatalog(
        [
            CreateCatalogItem("first-model", "25H2", new DateTimeOffset(2026, 5, 19, 0, 0, 0, TimeSpan.Zero), "ThinkPad E14 Gen 8", ["21Y6"]),
            CreateCatalogItem("second-model", "25H2", new DateTimeOffset(2026, 4, 28, 0, 0, 0, TimeSpan.Zero), "ThinkPad X13 Yoga Gen 3", ["21AW"])
        ]);

        viewModel.SetDetectedHardware(hardware with { Model = "21AW", Product = "ThinkPad X13 Yoga Gen 3" });

        Assert.Equal("ThinkPad X13 Yoga Gen 3", viewModel.SelectedDriverPackModel);
        Assert.Equal("second-model", viewModel.ResolveEffectiveSelection()?.Id);
    }

    [Fact]
    public void ResolveEffectiveSelection_WhenManualVersionDoesNotExist_DoesNotSubstituteAnotherVersion()
    {
        using var viewModel = CreateViewModel();
        viewModel.UpdateSelectionContext(null, CreateOperatingSystem(), "x64");
        viewModel.ReplaceCatalog([CreateCatalogItem("manual", "25H2", DateTimeOffset.MinValue)]);
        viewModel.SelectedDriverPackOption = viewModel.DriverPackOptions.Single(option => option.Key == "oem:lenovo");
        viewModel.SelectedDriverPackModel = "ThinkPad X13 Yoga Gen 3 Type 21AW 21AX";
        viewModel.SelectedDriverPackVersion = "Unavailable version";

        Assert.Null(viewModel.ResolveEffectiveSelection());
        Assert.False(viewModel.HasValidSelection());
    }

    private static DriverPackSelectionViewModel CreateViewModel() => new(
        new DriverPackSelectionService(NullLogger<DriverPackSelectionService>.Instance),
        new LocalizationService(),
        "x64");

    [Fact]
    public void ResolveEffectiveSelection_WhenContextRefreshes_PreservesExplicitOlderVersion()
    {
        using var viewModel = CreateViewModel();
        viewModel.UpdateSelectionContext(null, CreateOperatingSystem(), "x64");
        viewModel.ReplaceCatalog(
        [
            CreateCatalogItem("older", "25H2", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)) with { Version = "1.0" },
            CreateCatalogItem("newer", "25H2", new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)) with { Version = "2.0" }
        ]);
        viewModel.SelectedDriverPackOption = viewModel.DriverPackOptions.Single(option => option.Key == "oem:lenovo");
        viewModel.SelectedDriverPackModel = "ThinkPad X13 Yoga Gen 3 Type 21AW 21AX";
        viewModel.SelectedDriverPackVersion = "1.0";

        viewModel.UpdateSelectionContext(null, CreateOperatingSystem(), "x64");

        Assert.Equal("older", viewModel.ResolveEffectiveSelection()?.Id);
        Assert.True(viewModel.IsManualDriverPackSelection);
    }

    [Fact]
    public void ResolveEffectiveSelection_WhenManuallySelectedPackIsRemoved_RequiresAnotherExplicitChoice()
    {
        using var viewModel = CreateViewModel();
        viewModel.UpdateSelectionContext(null, CreateOperatingSystem(), "x64");
        DriverPackCatalogItem older = CreateCatalogItem("older", "25H2", DateTimeOffset.MinValue) with { Version = "1.0" };
        DriverPackCatalogItem newer = older with { Id = "newer", Version = "2.0" };
        viewModel.ReplaceCatalog([older, newer]);
        viewModel.SelectedDriverPackOption = viewModel.DriverPackOptions.Single(option => option.Key == "oem:lenovo");
        viewModel.SelectedDriverPackModel = "ThinkPad X13 Yoga Gen 3 Type 21AW 21AX";
        viewModel.SelectedDriverPackVersion = "1.0";

        viewModel.ReplaceCatalog([newer]);

        Assert.Null(viewModel.ResolveEffectiveSelection());
        Assert.False(viewModel.HasValidSelection());
    }

    [Theory]
    [InlineData("catalog-id")]
    [InlineData("source-url")]
    [InlineData("machine-type")]
    public void ResolveEffectiveSelection_WhenManualPackageIdentityChanges_RequiresAnotherExplicitChoice(string changedIdentity)
    {
        using var viewModel = CreateViewModel();
        viewModel.UpdateSelectionContext(null, CreateOperatingSystem(), "x64");
        DriverPackCatalogItem original = CreateCatalogItem("original", "25H2", DateTimeOffset.MinValue,
            systemIds: ["21AW"]) with
        { Version = "1.0" };
        viewModel.ReplaceCatalog([original]);
        viewModel.SelectedDriverPackOption = viewModel.DriverPackOptions.Single(option => option.Key == "oem:lenovo");
        viewModel.SelectedDriverPackModel = "ThinkPad X13 Yoga Gen 3 Type 21AW 21AX";
        Assert.Same(original, viewModel.ResolveEffectiveSelection());

        DriverPackCatalogItem replacement = changedIdentity switch
        {
            "catalog-id" => original with { Id = "replacement" },
            "source-url" => original with { DownloadUrl = "https://example.test/replacement.exe" },
            _ => original with { SystemIds = ["21Y6"] }
        };
        viewModel.ReplaceCatalog([replacement]);

        Assert.Null(viewModel.ResolveEffectiveSelection());
        Assert.False(viewModel.HasValidSelection());
        viewModel.SelectedDriverPackModel = "ThinkPad X13 Yoga Gen 3 Type 21AW 21AX";
        Assert.Same(replacement, viewModel.ResolveEffectiveSelection());
    }

    [Fact]
    public void ResolveEffectiveSelection_WhenManualPackageIsReacquired_RetainsItsIdentityOverNewerDisplayCollision()
    {
        using var viewModel = CreateViewModel();
        viewModel.UpdateSelectionContext(null, CreateOperatingSystem(), "x64");
        DriverPackCatalogItem original = CreateCatalogItem("original", "25H2", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            systemIds: ["21AW", "21AX"]) with
        { Version = "1.0", CatalogRevision = "first-catalog" };
        viewModel.ReplaceCatalog([original]);
        viewModel.SelectedDriverPackOption = viewModel.DriverPackOptions.Single(option => option.Key == "oem:lenovo");
        viewModel.SelectedDriverPackModel = "ThinkPad X13 Yoga Gen 3 Type 21AW 21AX";
        DriverPackCatalogItem reacquired = original with
        {
            CatalogRevision = "new-catalog",
            ModelNames = ["ThinkPad X13 Yoga Gen 3 Type 21AW 21AX"],
            SystemIds = ["21AX", "21AW"]
        };
        DriverPackCatalogItem newer = original with
        {
            Id = "newer",
            DownloadUrl = "https://example.test/newer.exe",
            ReleaseDate = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)
        };

        viewModel.ReplaceCatalog([newer, reacquired]);
        viewModel.UpdateSelectionContext(null, CreateOperatingSystem(), "x64");

        Assert.Same(reacquired, viewModel.ResolveEffectiveSelection());
        Assert.True(viewModel.HasValidSelection());
        Assert.True(viewModel.IsManualDriverPackSelection);
    }

    private static OperatingSystemCatalogItem CreateOperatingSystem() => new()
    {
        WindowsRelease = "11",
        ReleaseId = "25H2",
        Architecture = "x64"
    };

    private static DriverPackCatalogItem CreateCatalogItem(
        string id,
        string releaseId,
        DateTimeOffset releaseDate,
        string modelName = "ThinkPad X13 Yoga Gen 3 Type 21AW 21AX",
        IReadOnlyList<string>? systemIds = null)
    {
        return new DriverPackCatalogItem
        {
            Id = id,
            Manufacturer = "Lenovo",
            Name = $"ThinkPad X13 Yoga Gen 3 {releaseId}",
            FileName = $"tp_x13_yoga_g3_w11_{releaseId}.exe",
            DownloadUrl = $"https://example.test/{id}.exe",
            OsName = "Windows 11",
            OsReleaseId = releaseId,
            OsArchitecture = "x64",
            PackageRole = DriverPackPackageRole.System,
            ReleaseDate = releaseDate,
            ModelNames = [modelName],
            SystemIds = systemIds ?? []
        };
    }
}
