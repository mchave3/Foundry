// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text;

namespace Foundry.Utilities.Networking;

/// <summary>Reads HTTP metadata within an explicit byte budget and the caller's operation deadline.</summary>
public static class BoundedHttpContent
{
    public static async Task<string> ReadStringAsync(
        HttpResponseMessage response,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (maximumBytes is <= 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpResponseException(response.StatusCode,
                response.Headers.RetryAfter?.Delta, response.Headers.RetryAfter?.Date);
        }

        long? declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > maximumBytes)
        {
            throw new InvalidDataException("HTTP metadata exceeds the permitted size.");
        }

        Stream source;
        try
        {
            source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or HttpRequestException)
        {
            throw new TransferReadException(error);
        }

        Exception? failure = null;
        try
        {
            return await ReadStreamAsync(source, maximumBytes, declaredLength,
                response.Content.Headers.ContentType?.CharSet?.Trim('"'), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            try
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
            catch when (failure is not null)
            {
                // Preserve the body failure that determines whether this request may be retried.
            }
        }
    }

    private static async Task<string> ReadStreamAsync(Stream source, long maximumBytes,
        long? declaredLength, string? charset, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[64 * 1024];
        while (true)
        {
            int read;
            try
            {
                read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                throw new TransferReadException(error);
            }

            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException("HTTP metadata exceeds the permitted size.");
            }

            buffer.Write(chunk, 0, read);
        }

        if (declaredLength is { } expected && buffer.Length != expected)
        {
            throw new InvalidDataException("HTTP metadata length does not match the declared length.");
        }

        Encoding encoding;
        try
        {
            encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("HTTP metadata uses an unsupported character encoding.", error);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, encoding, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
