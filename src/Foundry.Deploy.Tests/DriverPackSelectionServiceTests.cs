// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;
using Foundry.Deploy.Services.DriverPacks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class DriverPackSelectionServiceTests
{
    [Fact]
    public void SelectBest_WhenExactModelExists_PrefersItOverNewerGenericCandidate()
    {
        var service = new DriverPackSelectionService(NullLogger<DriverPackSelectionService>.Instance);
        HardwareProfile hardware = new()
        {
            Manufacturer = "Dell Inc.",
            Model = "Latitude 5450",
            Product = "Latitude 5450",
            Architecture = "x64"
        };
        OperatingSystemCatalogItem operatingSystem = new()
        {
            WindowsRelease = "11",
            ReleaseId = "24H2",
            Architecture = "amd64"
        };

        DriverPackCatalogItem olderExactMatch = CreateCatalogItem(
            id: "exact",
            manufacturer: "Dell",
            releaseId: "24H2",
            architecture: "x64",
            releaseDate: new DateTimeOffset(2026, 03, 01, 0, 0, 0, TimeSpan.Zero),
            modelNames: ["Latitude 5450"]);

        DriverPackCatalogItem newerGeneric = CreateCatalogItem(
            id: "generic",
            manufacturer: "Dell",
            releaseId: "24H2",
            architecture: "x64",
            releaseDate: new DateTimeOffset(2026, 04, 01, 0, 0, 0, TimeSpan.Zero),
            modelNames: ["OptiPlex"]);

        DriverPackSelectionResult result = service.SelectBest([olderExactMatch, newerGeneric], hardware, operatingSystem);

        Assert.Equal("exact", result.DriverPack?.Id);
        Assert.Equal("Matched by hardware model/product and compatible OS release.", result.SelectionReason);
    }

    [Fact]
    public void SelectBest_WhenNoExactModelExists_DoesNotSelectAnotherHpModel()
    {
        var service = new DriverPackSelectionService(NullLogger<DriverPackSelectionService>.Instance);
        HardwareProfile hardware = new()
        {
            Manufacturer = "HP",
            Model = "EliteBook 845",
            Product = "EliteBook 845",
            Architecture = "x64"
        };
        OperatingSystemCatalogItem operatingSystem = new()
        {
            WindowsRelease = "11",
            ReleaseId = "25H2",
            Architecture = "x64"
        };

        DriverPackCatalogItem olderCandidate = CreateCatalogItem(
            id: "older",
            manufacturer: "HP",
            releaseId: "25H2",
            architecture: "x64",
            releaseDate: new DateTimeOffset(2026, 01, 01, 0, 0, 0, TimeSpan.Zero),
            modelNames: ["EliteBook 840"]);

        DriverPackCatalogItem newerCandidate = CreateCatalogItem(
            id: "newer",
            manufacturer: "HP",
            releaseId: "25H2",
            architecture: "x64",
            releaseDate: new DateTimeOffset(2026, 02, 01, 0, 0, 0, TimeSpan.Zero),
            modelNames: ["ProBook"]);

        DriverPackSelectionResult result = service.SelectBest([olderCandidate, newerCandidate], hardware, operatingSystem);

        Assert.Null(result.DriverPack);
        Assert.Contains("No compatible", result.SelectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectBest_WhenTargetReleaseIsUnavailable_DoesNotAssumeOlderReleaseCompatibility()
    {
        var service = new DriverPackSelectionService(NullLogger<DriverPackSelectionService>.Instance);
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

        DriverPackCatalogItem win11_21H2 = CreateCatalogItem(
            id: "21h2",
            manufacturer: "Lenovo",
            releaseId: "21H2",
            architecture: "x64",
            releaseDate: catalogDate,
            modelNames: ["ThinkPad X13 Yoga Gen 3 Type 21AW 21AX"]);
        DriverPackCatalogItem win11_22H2 = CreateCatalogItem(
            id: "22h2",
            manufacturer: "Lenovo",
            releaseId: "22H2",
            architecture: "x64",
            releaseDate: catalogDate,
            modelNames: ["ThinkPad X13 Yoga Gen 3 Type 21AW 21AX"]);
        DriverPackCatalogItem win11_23H2 = CreateCatalogItem(
            id: "23h2",
            manufacturer: "Lenovo",
            releaseId: "23H2",
            architecture: "x64",
            releaseDate: catalogDate,
            modelNames: ["ThinkPad X13 Yoga Gen 3 Type 21AW 21AX"]);

        DriverPackSelectionResult result = service.SelectBest([win11_21H2, win11_22H2, win11_23H2], hardware, operatingSystem);

        Assert.Null(result.DriverPack);
    }

    [Fact]
    public void SelectBest_WhenOnlyAnotherModelHasTargetRelease_DoesNotSelectEitherPack()
    {
        var service = new DriverPackSelectionService(NullLogger<DriverPackSelectionService>.Instance);
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

        DriverPackCatalogItem exactModel = CreateCatalogItem(
            id: "exact-23h2",
            manufacturer: "Lenovo",
            releaseId: "23H2",
            architecture: "x64",
            releaseDate: new DateTimeOffset(2024, 06, 13, 0, 0, 0, TimeSpan.Zero),
            modelNames: ["ThinkPad X13 Yoga Gen 3 Type 21AW 21AX"]);
        DriverPackCatalogItem otherModel = CreateCatalogItem(
            id: "other-25h2",
            manufacturer: "Lenovo",
            releaseId: "25H2",
            architecture: "x64",
            releaseDate: new DateTimeOffset(2025, 01, 01, 0, 0, 0, TimeSpan.Zero),
            modelNames: ["ThinkPad T14 Gen 5"]);

        DriverPackSelectionResult result = service.SelectBest([exactModel, otherModel], hardware, operatingSystem);

        Assert.Null(result.DriverPack);
    }

    [Fact]
    public void SelectBest_WhenLenovoModelsShareMarketingName_PrefersMatchingMachineType()
    {
        var service = new DriverPackSelectionService(NullLogger<DriverPackSelectionService>.Instance);
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

        DriverPackCatalogItem newerWrongType = CreateCatalogItem(
            id: "21y2-21y3",
            manufacturer: "Lenovo",
            releaseId: "25H2",
            architecture: "x64",
            releaseDate: new DateTimeOffset(2026, 05, 19, 0, 0, 0, TimeSpan.Zero),
            modelNames: ["ThinkPad E14 Gen 8 Type 21Y2 21Y3"],
            systemIds: ["21Y2", "21Y3"]);
        DriverPackCatalogItem olderMatchingType = CreateCatalogItem(
            id: "21y6-21y7",
            manufacturer: "Lenovo",
            releaseId: "25H2",
            architecture: "x64",
            releaseDate: new DateTimeOffset(2026, 04, 28, 0, 0, 0, TimeSpan.Zero),
            modelNames: ["ThinkPad E14 Gen 8 Type 21Y6 21Y7"],
            systemIds: ["21Y6", "21Y7"]);

        DriverPackSelectionResult result = service.SelectBest(
            [newerWrongType, olderMatchingType],
            hardware,
            operatingSystem);

        Assert.Equal("21y6-21y7", result.DriverPack?.Id);
    }

    [Theory]
    [InlineData("Latitude 545", "Latitude 5450")]
    [InlineData("Latitude 5450", "Latitude 5450 Rugged")]
    [InlineData("Unknown", "Unknown")]
    [InlineData("", "")]
    public void SelectBest_WithoutExactKnownModel_DoesNotSelectPack(string detectedModel, string catalogModel)
    {
        DriverPackSelectionResult result = SelectSingle(
            CreateHardware() with { Model = detectedModel, Product = detectedModel },
            CreateCompatiblePack() with { ModelNames = [catalogModel] });

        Assert.Null(result.DriverPack);
    }

    [Fact]
    public void SelectBest_WithCaseAndWhitespaceDifferences_SelectsExactModel()
    {
        DriverPackSelectionResult result = SelectSingle(
            CreateHardware() with { Model = "  LATITUDE\t5450  ", Product = "Unknown" },
            CreateCompatiblePack());

        Assert.Equal("compatible", result.DriverPack?.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("Not Dell")]
    [InlineData("Other")]
    public void SelectBest_WithUnknownOrDifferentManufacturer_DoesNotSearchOtherVendors(string manufacturer)
    {
        DriverPackSelectionResult result = SelectSingle(
            CreateHardware() with { Manufacturer = manufacturer },
            CreateCompatiblePack());

        Assert.Null(result.DriverPack);
    }

    [Theory]
    [InlineData("", "x64", "x64")]
    [InlineData("arm64", "x64", "x64")]
    [InlineData("x64", "", "")]
    [InlineData("unknown", "unknown", "unknown")]
    public void SelectBest_WithoutMatchingKnownArchitectures_DoesNotSelectPack(
        string hardwareArchitecture, string osArchitecture, string packArchitecture)
    {
        DriverPackSelectionResult result = SelectSingle(
            CreateHardware() with { Architecture = hardwareArchitecture },
            CreateCompatiblePack() with { OsArchitecture = packArchitecture },
            CreateOperatingSystem() with { Architecture = osArchitecture });

        Assert.Null(result.DriverPack);
    }

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("23H2")]
    [InlineData("25H2")]
    [InlineData("124H2")]
    public void SelectBest_WithoutExactReleaseMetadata_DoesNotTrustNameOrOtherRelease(string releaseId)
    {
        DriverPackSelectionResult result = SelectSingle(
            CreateHardware(),
            CreateCompatiblePack() with { OsReleaseId = releaseId, Name = "Latitude 5450 24H2" });

        Assert.Null(result.DriverPack);
    }

    [Fact]
    public void SelectBest_WhenLenovoTypeConflicts_RejectsExactMarketingName()
    {
        DriverPackSelectionResult result = SelectSingle(
            CreateHardware() with { Manufacturer = "Lenovo", Model = "21Y6000JMX", Product = "ThinkPad E14 Gen 8" },
            CreateCompatiblePack() with { Manufacturer = "Lenovo", ModelNames = ["ThinkPad E14 Gen 8"], SystemIds = ["21Y2", "21Y3"] });

        Assert.Null(result.DriverPack);
    }

    [Theory]
    [InlineData("21Y60", "21Y6")]
    [InlineData("21Y6000JMX", "21Y")]
    [InlineData("21Y6000JMX-extra", "21Y6")]
    public void SelectBest_WhenLenovoIdentifierOnlySharesPrefix_DoesNotTreatItAsMachineType(string model, string systemId)
    {
        DriverPackSelectionResult result = SelectSingle(
            CreateHardware() with { Manufacturer = "Lenovo", Model = model, Product = "Unknown" },
            CreateCompatiblePack() with { Manufacturer = "Lenovo", ModelNames = ["ThinkPad E14 Gen 8"], SystemIds = [systemId] });

        Assert.Null(result.DriverPack);
    }

    [Fact]
    public void SelectBest_WhenSurfaceArm64CatalogContainsOnlyDock_DoesNotSelectAccessory()
    {
        DriverPackSelectionResult result = SelectSingle(
            CreateHardware() with { Manufacturer = "Microsoft Corporation", Model = "Surface Pro", Product = "Surface Pro", Architecture = "arm64" },
            CreateCompatiblePack() with
            {
                Manufacturer = "Microsoft",
                Name = "SurfaceThunderbolt4DockDrivers_Win11_arm64_22000_23.033.35231.0.msi",
                ModelNames = ["Surface Thunderbolt 4 Dock"],
                OsReleaseId = "21H2",
                OsArchitecture = "arm64"
            },
            CreateOperatingSystem() with { Architecture = "arm64" });

        Assert.Null(result.DriverPack);
    }

    private static DriverPackSelectionResult SelectSingle(
        HardwareProfile hardware,
        DriverPackCatalogItem item,
        OperatingSystemCatalogItem? operatingSystem = null)
    {
        var service = new DriverPackSelectionService(NullLogger<DriverPackSelectionService>.Instance);
        return service.SelectBest([item], hardware, operatingSystem ?? CreateOperatingSystem());
    }

    [Theory]
    [InlineData(DriverPackPackageRole.Unknown)]
    [InlineData(DriverPackPackageRole.Accessory)]
    public void SelectBest_WhenRoleDoesNotEstablishSystemPack_RejectsExactModel(DriverPackPackageRole role)
    {
        DriverPackSelectionResult result = SelectSingle(CreateHardware(), CreateCompatiblePack() with { PackageRole = role });

        Assert.Null(result.DriverPack);
    }

    [Theory]
    [InlineData(DriverPackPackageRole.Unknown, true)]
    [InlineData(DriverPackPackageRole.Accessory, false)]
    public void SelectBest_WhenLenovoTypeMatches_UsesDocumentedSystemMappingExceptForAccessory(
        DriverPackPackageRole role, bool expectedMatch)
    {
        DriverPackSelectionResult result = SelectSingle(
            CreateHardware() with { Manufacturer = "Lenovo", Model = "21Y6000JMX", Product = "Unknown" },
            CreateCompatiblePack() with { Manufacturer = "Lenovo", PackageRole = role, ModelNames = [], SystemIds = ["21Y6"] });

        Assert.Equal(expectedMatch, result.DriverPack is not null);
    }

    private static HardwareProfile CreateHardware() => new()
    {
        Manufacturer = "Dell Inc.",
        Model = "Latitude 5450",
        Product = "Latitude 5450",
        Architecture = "x64"
    };

    private static OperatingSystemCatalogItem CreateOperatingSystem() => new()
    {
        WindowsRelease = "11",
        ReleaseId = "24H2",
        Architecture = "x64"
    };

    private static DriverPackCatalogItem CreateCompatiblePack() => CreateCatalogItem(
        "compatible", "Dell", "24H2", "x64", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), ["Latitude 5450"]);

    private static DriverPackCatalogItem CreateCatalogItem(
        string id,
        string manufacturer,
        string releaseId,
        string architecture,
        DateTimeOffset releaseDate,
        IReadOnlyList<string> modelNames,
        IReadOnlyList<string>? systemIds = null)
    {
        return new DriverPackCatalogItem
        {
            Id = id,
            Manufacturer = manufacturer,
            Name = $"{manufacturer} {releaseId}",
            FileName = "driverpack.cab",
            DownloadUrl = "https://example.test/driverpack.cab",
            OsName = "Windows 11",
            OsReleaseId = releaseId,
            OsArchitecture = architecture,
            PackageRole = DriverPackPackageRole.System,
            ReleaseDate = releaseDate,
            ModelNames = modelNames,
            SystemIds = systemIds ?? []
        };
    }
}
