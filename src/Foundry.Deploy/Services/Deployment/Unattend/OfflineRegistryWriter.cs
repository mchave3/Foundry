// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Services.System;
using Foundry.Utilities.Processes;

namespace Foundry.Deploy.Services.Deployment.Unattend;

/// <summary>
/// Provides shared primitives for loading offline Windows registry hives and writing values with reg.exe.
/// </summary>
internal sealed class OfflineRegistryWriter
{
    private readonly IProcessRunner _processRunner;

    /// <summary>
    /// Initializes an offline registry writer backed by the deployment process runner.
    /// </summary>
    /// <param name="processRunner">The process runner used for reg.exe operations.</param>
    public OfflineRegistryWriter(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    /// <summary>
    /// Loads a hive, runs the supplied action, and unloads the hive even when the action fails.
    /// </summary>
    /// <param name="mountName">Temporary registry mount name, such as <c>HKLM\FoundrySoftware</c>.</param>
    /// <param name="hivePath">Path to the offline hive file.</param>
    /// <param name="workingDirectory">Directory used for command execution.</param>
    /// <param name="action">Action that writes values under the loaded hive.</param>
    /// <param name="cancellationToken">Token that cancels hive loading and write operations.</param>
    /// <returns>A task that completes after the hive is unloaded.</returns>
    public async Task WithLoadedHiveAsync(
        string mountName,
        string hivePath,
        string workingDirectory,
        Func<OfflineRegistryHive, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        await RunRequiredAsync("reg.exe", ["LOAD", mountName, hivePath], workingDirectory, cancellationToken).ConfigureAwait(false);
        try
        {
            var hive = new OfflineRegistryHive(this, mountName, workingDirectory);
            await action(hive, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await RunRequiredAsync("reg.exe", ["UNLOAD", mountName], workingDirectory, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private Task AddDwordAsync(
        string keyPath,
        string valueName,
        int value,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        return RunRequiredAsync(
            "reg.exe",
            ["ADD", keyPath, "/v", valueName, "/t", "REG_DWORD", "/d", value.ToString(), "/f"],
            workingDirectory,
            cancellationToken);
    }

    private async Task RunRequiredAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ProcessExecutionResult result = await _processRunner
            .RunAsync(fileName, arguments, workingDirectory, cancellationToken, TimeSpan.FromMinutes(2))
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new DeploymentProcessException(
                $"{fileName} failed.{Environment.NewLine}{result.ToDiagnosticText()}",
                result.ExitCode);
        }
    }

    /// <summary>
    /// Represents a loaded offline registry hive and exposes value writes relative to its mount point.
    /// </summary>
    internal sealed class OfflineRegistryHive
    {
        private readonly OfflineRegistryWriter _writer;
        private readonly string _workingDirectory;

        /// <summary>
        /// Initializes a loaded hive context.
        /// </summary>
        /// <param name="writer">The writer that owns reg.exe execution.</param>
        /// <param name="mountName">Temporary registry mount name.</param>
        /// <param name="workingDirectory">Directory used for command execution.</param>
        public OfflineRegistryHive(OfflineRegistryWriter writer, string mountName, string workingDirectory)
        {
            _writer = writer;
            MountName = mountName;
            _workingDirectory = workingDirectory;
        }

        /// <summary>
        /// Gets the temporary registry mount name used for this hive.
        /// </summary>
        public string MountName { get; }

        /// <summary>
        /// Writes a REG_DWORD value below this loaded hive.
        /// </summary>
        /// <param name="relativeKeyPath">Registry key path relative to the hive mount point.</param>
        /// <param name="valueName">Registry value name.</param>
        /// <param name="value">DWORD value to write.</param>
        /// <param name="cancellationToken">Token that cancels the write operation.</param>
        /// <returns>A task that completes after reg.exe writes the value.</returns>
        public Task AddDwordAsync(
            string relativeKeyPath,
            string valueName,
            int value,
            CancellationToken cancellationToken)
        {
            string trimmedPath = relativeKeyPath.TrimStart('\\');
            string keyPath = string.IsNullOrWhiteSpace(trimmedPath)
                ? MountName
                : $@"{MountName}\{trimmedPath}";

            return _writer.AddDwordAsync(keyPath, valueName, value, _workingDirectory, cancellationToken);
        }
    }
}
