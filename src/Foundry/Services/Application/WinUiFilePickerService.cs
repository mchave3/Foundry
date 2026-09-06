// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Services.Application;
using Microsoft.Windows.Storage.Pickers;

namespace Foundry.Services.Application;

/// <summary>
/// Implements file and folder picking through WinUI picker controls bound to the main window.
/// </summary>
public sealed class WinUiFilePickerService : IFilePickerService
{
    /// <inheritdoc />
    public async Task<string?> PickOpenFileAsync(FileOpenPickerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id)
        {
            Title = request.Title
        };

        foreach (string filter in NormalizeFileTypeFilters(request.FileTypeFilters))
        {
            picker.FileTypeFilter.Add(filter);
        }

        PickFileResult? result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    /// <inheritdoc />
    public async Task<string?> PickSaveFileAsync(FileSavePickerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var picker = new FileSavePicker(App.MainWindow.AppWindow.Id)
        {
            Title = request.Title,
            SuggestedFileName = request.SuggestedFileName,
            DefaultFileExtension = request.DefaultFileExtension ?? ResolveDefaultExtension(request.FileTypeChoices)
        };

        foreach (FilePickerTypeChoice choice in request.FileTypeChoices)
        {
            picker.FileTypeChoices.Add(choice.Name, choice.Extensions.Select(NormalizeExtension).ToList());
        }

        PickFileResult? result = await picker.PickSaveFileAsync();
        return result?.Path;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> PickOpenFilesAsync(FileOpenPickerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id)
        {
            Title = request.Title
        };
        foreach (string filter in NormalizeFileTypeFilters(request.FileTypeFilters))
        {
            picker.FileTypeFilter.Add(filter);
        }

        var results = await picker.PickMultipleFilesAsync();
        return results.Select(result => result.Path).ToArray();
    }

    /// <inheritdoc />
    public async Task<string?> PickFolderAsync(FolderPickerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var picker = new FolderPicker(App.MainWindow.AppWindow.Id)
        {
            Title = request.Title
        };

        PickFolderResult? result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }

    private static IReadOnlyList<string> NormalizeFileTypeFilters(IReadOnlyList<string> filters)
    {
        if (filters.Count == 0)
        {
            return (string[])["*"];
        }

        return filters.Select(NormalizeExtension).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension) || extension.Trim() == "*")
        {
            return "*";
        }

        string trimmed = extension.Trim();
        return trimmed.StartsWith(".", StringComparison.Ordinal) ? trimmed : $".{trimmed}";
    }

    private static string? ResolveDefaultExtension(IReadOnlyList<FilePickerTypeChoice> choices)
    {
        return choices.FirstOrDefault()?.Extensions.FirstOrDefault() is { } extension
            ? NormalizeExtension(extension)
            : null;
    }
}
