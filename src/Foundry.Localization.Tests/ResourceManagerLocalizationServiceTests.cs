// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Globalization;
using System.Resources;
using Foundry.Localization;

namespace Foundry.Localization.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CultureSensitiveTestCollection
{
    public const string Name = "CultureSensitive";
}

[Collection(CultureSensitiveTestCollection.Name)]
public sealed class ResourceManagerLocalizationServiceCultureTests
{
    [Fact]
    public void SetCulture_UpdatesCurrentCulturesAndRaisesSingleLanguageChangedEvent()
    {
        using var cultureScope = new CultureScope();
        ResourceManagerLocalizationService service = ResourceManagerLocalizationServiceTestFactory.Create("en-US");
        List<ApplicationLanguageChangedEventArgs> events = [];

        service.LanguageChanged += (_, args) => events.Add(args);

        service.SetCulture(CultureInfo.GetCultureInfo("fr-FR"));

        Assert.Equal("fr-FR", service.CurrentCulture.Name);
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
        Assert.Equal("fr-FR", CultureInfo.CurrentUICulture.Name);
        Assert.Equal("fr-FR", CultureInfo.DefaultThreadCurrentCulture?.Name);
        Assert.Equal("fr-FR", CultureInfo.DefaultThreadCurrentUICulture?.Name);
        ApplicationLanguageChangedEventArgs eventArgs = Assert.Single(events);
        Assert.Equal("en-US", eventArgs.OldLanguage);
        Assert.Equal("fr-FR", eventArgs.NewLanguage);
    }

    [Fact]
    public void SetCulture_WhenCultureMatchesSupportedLanguageFamily_AppliesConfiguredCulture()
    {
        using var cultureScope = new CultureScope();
        ResourceManagerLocalizationService service = ResourceManagerLocalizationServiceTestFactory.Create("en-US");
        ApplicationLanguageChangedEventArgs? eventArgs = null;

        service.LanguageChanged += (_, args) => eventArgs = args;

        service.SetCulture(CultureInfo.GetCultureInfo("fr-CA"));

        Assert.Equal("fr-FR", service.CurrentCulture.Name);
        Assert.NotNull(eventArgs);
        Assert.Equal("en-US", eventArgs.OldLanguage);
        Assert.Equal("fr-FR", eventArgs.NewLanguage);
    }

    [Fact]
    public void SetCulture_WhenCultureDoesNotChange_DoesNotRaiseLanguageChanged()
    {
        using var cultureScope = new CultureScope();
        ResourceManagerLocalizationService service = ResourceManagerLocalizationServiceTestFactory.Create("en-US");
        int changeCount = 0;

        service.LanguageChanged += (_, _) => changeCount++;

        service.SetCulture(CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal(0, changeCount);
    }

    [Fact]
    public void Strings_ReturnsLocalizedValueAndNotifiesIndexerChange()
    {
        using var cultureScope = new CultureScope();
        ResourceManagerLocalizationService service = ResourceManagerLocalizationServiceTestFactory.Create("en-US");
        List<string?> changedProperties = [];

        service.Strings.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        Assert.Equal("Hello", service.Strings["Greeting"]);

        service.SetCulture(CultureInfo.GetCultureInfo("fr-FR"));

        Assert.Equal("Bonjour", service.Strings["Greeting"]);
        Assert.Contains("Item[]", changedProperties);
    }
}

public sealed class ResourceManagerLocalizationServiceTests
{
    [Fact]
    public void Constructor_WhenInitialCultureMatchesSupportedLanguageFamily_UsesConfiguredCulture()
    {
        SupportedCultureCatalog catalog = new(
            "en-US",
            [
                new SupportedCultureDefinition("en-US", "Language.English", 10),
                new SupportedCultureDefinition("fr-FR", "Language.French", 20)
            ]);

        ResourceManagerLocalizationService service = ResourceManagerLocalizationServiceTestFactory.Create("fr-CA", catalog);

        Assert.Equal("fr-FR", service.CurrentCulture.Name);
    }

    [Fact]
    public void GetString_WhenKeyIsMissing_ReturnsKey()
    {
        ResourceManagerLocalizationService service = ResourceManagerLocalizationServiceTestFactory.Create("en-US");

        string result = service.GetString("Missing.Key");

        Assert.Equal("Missing.Key", result);
    }

    [Fact]
    public void CreateSupportedCultureOptions_UsesCurrentCultureAndLocalizedDisplayNames()
    {
        SupportedCultureCatalog catalog = new(
            "de-DE",
            [
                new SupportedCultureDefinition("de-DE", "Language.German", 10),
                new SupportedCultureDefinition("es-ES", "Language.Spanish", 20),
                new SupportedCultureDefinition("it-IT", "Language.Italian", 30)
            ]);
        ResourceManagerLocalizationService service = ResourceManagerLocalizationServiceTestFactory.Create("it-IT", catalog);

        IReadOnlyList<SupportedCultureOption> options = service.CreateSupportedCultureOptions();

        Assert.Equal(["de-DE", "es-ES", "it-IT"], options.Select(option => option.Code));
        Assert.Equal("Language.Italian", options.Single(option => option.Code == "it-IT").DisplayName);
        Assert.True(options.Single(option => option.Code == "it-IT").IsSelected);
    }
}

file sealed class CultureScope : IDisposable
{
    private readonly CultureInfo currentCulture = CultureInfo.CurrentCulture;
    private readonly CultureInfo currentUiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? defaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentCulture;
    private readonly CultureInfo? defaultThreadCurrentUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = defaultThreadCurrentCulture;
        CultureInfo.DefaultThreadCurrentUICulture = defaultThreadCurrentUiCulture;
        CultureInfo.CurrentCulture = currentCulture;
        CultureInfo.CurrentUICulture = currentUiCulture;
    }
}

file static class ResourceManagerLocalizationServiceTestFactory
{
    public static ResourceManagerLocalizationService Create(string cultureName)
    {
        return Create(cultureName, CreateTestCatalog());
    }

    public static ResourceManagerLocalizationService Create(string cultureName, SupportedCultureCatalog catalog)
    {
        ResourceManager resourceManager = new(
            "Foundry.Localization.Tests.Strings.Resources",
            typeof(ResourceManagerLocalizationServiceTests).Assembly);

        return new ResourceManagerLocalizationService(
            resourceManager,
            CultureInfo.GetCultureInfo(cultureName),
            catalog);
    }

    private static SupportedCultureCatalog CreateTestCatalog()
    {
        return new SupportedCultureCatalog(
            "en-US",
            [
                new SupportedCultureDefinition("en-US", "Language.English", 10),
                new SupportedCultureDefinition("fr-FR", "Language.French", 20)
            ]);
    }
}
