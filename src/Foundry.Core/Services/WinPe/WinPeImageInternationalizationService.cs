// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Core.Services.WinPe;

public sealed class WinPeImageInternationalizationService : IWinPeImageInternationalizationService
{
    private static readonly string[] RequiredOptionalComponents =
    [
        "WinPE-WMI",
        "WinPE-NetFX",
        "WinPE-Scripting",
        "WinPE-PowerShell",
        "WinPE-WinReCfg",
        "WinPE-DismCmdlets",
        "WinPE-StorageWMI",
        "WinPE-Dot3Svc",
        "WinPE-EnhancedStorage",
        "WinPE-SecureStartup"
    ];
    private static readonly string[] BlockingOptionalComponents =
    [
        "WinPE-SecureStartup"
    ];

    private readonly IWinPeProcessRunner _processRunner;

    public WinPeImageInternationalizationService()
        : this(new WinPeProcessRunner())
    {
    }

    internal WinPeImageInternationalizationService(IWinPeProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<WinPeResult> ApplyAsync(
        WinPeImageInternationalizationOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        WinPeDiagnostic? validationError = ValidateOptions(options);
        if (validationError is not null)
        {
            return WinPeResult.Failure(validationError);
        }

        WinPeToolPaths tools = options.Tools!;
        string normalizedLocale = WinPeLanguageUtility.Normalize(options.WinPeLanguage);
        if (!WinPeLanguageUtility.TryResolveInputLocale(normalizedLocale, out string canonicalLocale, out string inputLocale))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.ValidationFailed,
                "The selected WinPE language cannot be converted to a keyboard layout.",
                $"Language: '{options.WinPeLanguage}'.");
        }

        string optionalComponentsRoot = GetOptionalComponentsRootPath(tools.KitsRootPath, options.Architecture);
        if (!Directory.Exists(optionalComponentsRoot))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.ToolNotFound,
                "The WinPE optional components folder was not found.",
                $"Expected path: '{optionalComponentsRoot}'.");
        }

        WinPeResult packageResult = await AddRequiredOptionalComponentsAsync(
            options.MountedImagePath,
            tools.DismPath,
            optionalComponentsRoot,
            normalizedLocale,
            options.WorkingDirectoryPath,
            options.DismProgress,
            cancellationToken).ConfigureAwait(false);

        if (!packageResult.IsSuccess)
        {
            return packageResult;
        }

        return await ApplyInternationalSettingsAsync(
            options.MountedImagePath,
            tools.DismPath,
            canonicalLocale,
            inputLocale,
            options.WorkingDirectoryPath,
            options.DismProgress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WinPeResult> AddRequiredOptionalComponentsAsync(
        string mountedImagePath,
        string dismPath,
        string optionalComponentsRoot,
        string normalizedLocale,
        string workingDirectoryPath,
        IProgress<WinPeDismProgress>? dismProgress,
        CancellationToken cancellationToken)
    {
        string languagePackPath = Path.Combine(optionalComponentsRoot, normalizedLocale, "lp.cab");
        if (!File.Exists(languagePackPath))
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.ToolNotFound,
                "The selected WinPE language pack was not found.",
                $"Expected path: '{languagePackPath}'.");
        }

        WinPeResult languagePackResult = await AddPackageAsync(
            dismPath,
            mountedImagePath,
            languagePackPath,
            workingDirectoryPath,
            "Failed to add the selected WinPE language pack.",
            "Applying language pack with DISM.",
            allowNonBlockingPackageFailure: false,
            dismProgress,
            cancellationToken).ConfigureAwait(false);

        if (!languagePackResult.IsSuccess)
        {
            return languagePackResult;
        }

        int neutralComponentsFound = 0;
        foreach (string component in RequiredOptionalComponents)
        {
            bool isBlockingComponent = BlockingOptionalComponents.Contains(component, StringComparer.OrdinalIgnoreCase);
            string neutralPackagePath = Path.Combine(optionalComponentsRoot, $"{component}.cab");
            if (File.Exists(neutralPackagePath))
            {
                neutralComponentsFound++;
                WinPeResult neutralResult = await AddPackageAsync(
                    dismPath,
                    mountedImagePath,
                    neutralPackagePath,
                    workingDirectoryPath,
                    $"Failed to add the '{component}' WinPE optional component.",
                    "Applying optional components with DISM.",
                    allowNonBlockingPackageFailure: !isBlockingComponent,
                    dismProgress,
                    cancellationToken).ConfigureAwait(false);

                if (!neutralResult.IsSuccess)
                {
                    return neutralResult;
                }
            }
            else if (isBlockingComponent)
            {
                return WinPeResult.Failure(
                    WinPeErrorCodes.ToolNotFound,
                    $"The required '{component}' WinPE optional component was not found.",
                    $"Expected path: '{neutralPackagePath}'.");
            }

            string localizedPackagePath = Path.Combine(optionalComponentsRoot, normalizedLocale, $"{component}_{normalizedLocale}.cab");
            if (File.Exists(localizedPackagePath))
            {
                WinPeResult localizedResult = await AddPackageAsync(
                    dismPath,
                    mountedImagePath,
                    localizedPackagePath,
                    workingDirectoryPath,
                    $"Failed to add the localized '{component}' WinPE optional component.",
                    "Applying optional components with DISM.",
                    allowNonBlockingPackageFailure: !isBlockingComponent,
                    dismProgress,
                    cancellationToken).ConfigureAwait(false);

                if (!localizedResult.IsSuccess)
                {
                    return localizedResult;
                }
            }
        }

        return neutralComponentsFound > 0
            ? WinPeResult.Success()
            : WinPeResult.Failure(
                WinPeErrorCodes.ToolNotFound,
                "No required WinPE optional components were found.",
                $"Expected at least one required component under: '{optionalComponentsRoot}'.");
    }

    private async Task<WinPeResult> AddPackageAsync(
        string dismPath,
        string mountedImagePath,
        string packagePath,
        string workingDirectoryPath,
        string failureMessage,
        string progressStatus,
        bool allowNonBlockingPackageFailure,
        IProgress<WinPeDismProgress>? dismProgress,
        CancellationToken cancellationToken)
    {
        WinPeProcessExecution execution = await WinPeDismProcessRunner.RunAsync(
            _processRunner,
            dismPath,
            [$"/Image:{mountedImagePath}", "/Add-Package", $"/PackagePath:{packagePath}"],
            workingDirectoryPath,
            progressStatus,
            dismProgress,
            cancellationToken).ConfigureAwait(false);

        if (execution.IsSuccess ||
            IsAlreadyInstalledPackageFailure(execution) ||
            allowNonBlockingPackageFailure && IsNonBlockingPackageFailure(execution))
        {
            return WinPeResult.Success();
        }

        return WinPeResult.Failure(
            WinPeErrorCodes.BuildFailed,
            failureMessage,
            execution.ToDiagnosticText());
    }

    private async Task<WinPeResult> ApplyInternationalSettingsAsync(
        string mountedImagePath,
        string dismPath,
        string canonicalLocale,
        string inputLocale,
        string workingDirectoryPath,
        IProgress<WinPeDismProgress>? dismProgress,
        CancellationToken cancellationToken)
    {
        string[][] arguments =
        [
            [$"/Image:{mountedImagePath}", $"/Set-AllIntl:{canonicalLocale}"],
            [$"/Image:{mountedImagePath}", $"/Set-InputLocale:{inputLocale}"]
        ];

        foreach (string[] args in arguments)
        {
            WinPeProcessExecution execution = await WinPeDismProcessRunner.RunAsync(
                _processRunner,
                dismPath,
                args,
                workingDirectoryPath,
                "Applying international settings with DISM.",
                dismProgress,
                cancellationToken).ConfigureAwait(false);

            if (!execution.IsSuccess)
            {
                return WinPeResult.Failure(
                    WinPeErrorCodes.BuildFailed,
                    "Failed to apply WinPE international settings.",
                    execution.ToDiagnosticText());
            }
        }

        return WinPeResult.Success();
    }

    private static WinPeDiagnostic? ValidateOptions(WinPeImageInternationalizationOptions? options)
    {
        if (options is null)
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Internationalization options are required.",
                "Provide a non-null WinPeImageInternationalizationOptions instance.");
        }

        if (string.IsNullOrWhiteSpace(options.MountedImagePath) || !Directory.Exists(options.MountedImagePath))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Mounted image path is required.",
                $"Path: '{options.MountedImagePath}'.");
        }

        if (!Enum.IsDefined(options.Architecture))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Architecture value is invalid.",
                $"Value: '{options.Architecture}'.");
        }

        if (options.Tools is null)
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "WinPE tool paths are required.",
                "Set WinPeImageInternationalizationOptions.Tools.");
        }

        if (string.IsNullOrWhiteSpace(options.Tools.KitsRootPath))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "ADK kits root path is required.",
                "Set WinPeToolPaths.KitsRootPath.");
        }

        if (string.IsNullOrWhiteSpace(options.Tools.DismPath))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "DISM path is required.",
                "Set WinPeToolPaths.DismPath.");
        }

        if (string.IsNullOrWhiteSpace(options.WinPeLanguage))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "WinPE language is required.",
                "Set WinPeImageInternationalizationOptions.WinPeLanguage.");
        }

        if (string.IsNullOrWhiteSpace(options.WorkingDirectoryPath))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Working directory path is required.",
                "Set WinPeImageInternationalizationOptions.WorkingDirectoryPath.");
        }

        return null;
    }

    private static string GetOptionalComponentsRootPath(string kitsRootPath, WinPeArchitecture architecture)
    {
        return Path.Combine(
            kitsRootPath,
            "Assessment and Deployment Kit",
            "Windows Preinstallation Environment",
            architecture.ToCopypeArchitecture(),
            "WinPE_OCs");
    }

    private static bool IsAlreadyInstalledPackageFailure(WinPeProcessExecution execution)
    {
        execution.EnsureCompleteOutput();
        string diagnostic = $"{execution.StandardOutput}{Environment.NewLine}{execution.StandardError}";
        return diagnostic.Contains("already installed", StringComparison.OrdinalIgnoreCase) ||
               diagnostic.Contains("already exists", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNonBlockingPackageFailure(WinPeProcessExecution execution)
    {
        execution.EnsureCompleteOutput();
        string diagnostic = $"{execution.StandardOutput}{Environment.NewLine}{execution.StandardError}";
        return diagnostic.Contains("0x800f081e", StringComparison.OrdinalIgnoreCase) ||
               diagnostic.Contains("not applicable", StringComparison.OrdinalIgnoreCase);
    }
}
