// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using Foundry.Utilities.Processes;

namespace Foundry.Deploy.Services.System;

/// <summary>Runs deployment tools with a four-hour default deadline; callers can choose a shorter operation budget.</summary>
public interface IProcessRunner
{
    /// <summary>Preserves an explicitly raw installer or shell argument contract without guessing its token boundaries.</summary>
    Task<ProcessExecutionResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null);

    /// <summary>Runs a direct executable with independent argument tokens.</summary>
    Task<ProcessExecutionResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null);

    /// <summary>Runs a direct executable while forwarding bounded progress output.</summary>
    Task<ProcessExecutionResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        Action<string>? onOutputData,
        Action<string>? onErrorData,
        CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null);
}
