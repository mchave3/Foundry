// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Core.Services.Application;

/// <summary>
/// Abstracts file and folder picker interactions for UI-independent services and view models.
/// </summary>
public interface IFilePickerService
{
    /// <summary>
    /// Shows an open-file picker.
    /// </summary>
    /// <param name="request">The picker request.</param>
    /// <returns>The selected file path, or <see langword="null"/> when canceled.</returns>
    Task<string?> PickOpenFileAsync(FileOpenPickerRequest request);

    /// <summary>
    /// Shows an open-file picker that permits multiple selections.
    /// </summary>
    /// <param name="request">The picker request.</param>
    /// <returns>Selected paths, or an empty list when canceled.</returns>
    Task<IReadOnlyList<string>> PickOpenFilesAsync(FileOpenPickerRequest request);

    /// <summary>
    /// Shows a save-file picker.
    /// </summary>
    /// <param name="request">The picker request.</param>
    /// <returns>The selected file path, or <see langword="null"/> when canceled.</returns>
    Task<string?> PickSaveFileAsync(FileSavePickerRequest request);

    /// <summary>
    /// Shows a folder picker.
    /// </summary>
    /// <param name="request">The picker request.</param>
    /// <returns>The selected folder path, or <see langword="null"/> when canceled.</returns>
    Task<string?> PickFolderAsync(FolderPickerRequest request);
}
