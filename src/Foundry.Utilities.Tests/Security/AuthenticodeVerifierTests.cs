// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Utilities.Processes;
using Foundry.Utilities.Security;

namespace Foundry.Utilities.Tests.Security;

public sealed class AuthenticodeVerifierTests
{
    private static readonly IReadOnlySet<string> ExpectedSubjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CN=Expected Test Publisher" };

    [Theory]
    [InlineData("{\"Status\":\"NotSigned\",\"Subject\":null}")]
    [InlineData("{\"Status\":\"HashMismatch\",\"Subject\":\"CN=Expected Test Publisher\"}")]
    [InlineData("{\"Status\":\"Valid\",\"Subject\":\"CN=Unexpected Test Publisher\"}")]
    [InlineData("{\"Status\":\"Valid\",\"Subject\":\"cn=expected test publisher\"}")]
    [InlineData("{\"Status\":\"Valid\",\"Subject\":null}")]
    [InlineData("{\"Status\":\"Valid\",\"Subject\":\"CN=Expected Test Publisher\",\"Status\":\"NotSigned\"}")]
    [InlineData("not json")]
    public async Task VerifyAsync_UntrustedMetadata_IsRejected(string metadata)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Verify(new ProcessExecutionResult { StandardOutput = metadata }));
    }

    [Theory]
    [InlineData(true, false, 0, "")]
    [InlineData(false, true, 0, "")]
    [InlineData(false, false, 1, "")]
    [InlineData(false, false, 0, "signature provider error")]
    public async Task VerifyAsync_IncompleteOrFailedTrustCheck_IsRejected(bool outputTruncated, bool errorTruncated, int exitCode, string error)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Verify(new ProcessExecutionResult
        {
            StandardOutput = "{\"Status\":\"Valid\",\"Subject\":\"CN=Expected Test Publisher\"}",
            StandardOutputTruncated = outputTruncated,
            StandardErrorTruncated = errorTruncated,
            ExitCode = exitCode,
            StandardError = error
        }));
    }

    [Fact]
    public async Task VerifyAsync_ExactTrustedSubject_IsAcceptedWithBoundedAbsoluteProcess()
    {
        await AuthenticodeVerifier.VerifyAsync(Path.GetFullPath("payload.exe"), ExpectedSubjects, (request, _) =>
        {
            Assert.True(Path.IsPathFullyQualified(request.FileName));
            Assert.EndsWith("WindowsPowerShell\\v1.0\\powershell.exe", request.FileName);
            Assert.Equal(TimeSpan.FromMinutes(2), request.ExecutionTimeout);
            Assert.True(request.MaxCapturedOutputCharacters <= 16_384);
            Assert.Contains("-EncodedCommand", request.ArgumentList!);
            return Task.FromResult(new ProcessExecutionResult { StandardOutput = "{\"Status\":\"Valid\",\"Subject\":\"CN=Expected Test Publisher\"}" });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task VerifyAsync_CancellationAndDeadlineArePreserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AuthenticodeVerifier.VerifyAsync(Path.GetFullPath("payload.exe"), ExpectedSubjects,
                (_, token) => Task.FromCanceled<ProcessExecutionResult>(token), cancellation.Token));
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        var timeout = new TimeoutException("fixture deadline");
        Assert.Same(timeout, await Assert.ThrowsAsync<TimeoutException>(() =>
            AuthenticodeVerifier.VerifyAsync(Path.GetFullPath("payload.exe"), ExpectedSubjects,
                (_, _) => Task.FromException<ProcessExecutionResult>(timeout), TestContext.Current.CancellationToken)));
    }

    private static Task Verify(ProcessExecutionResult result)
        => AuthenticodeVerifier.VerifyAsync(Path.GetFullPath("payload.exe"), ExpectedSubjects, (_, _) => Task.FromResult(result));
}
