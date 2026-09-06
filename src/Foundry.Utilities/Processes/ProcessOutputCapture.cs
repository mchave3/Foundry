// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text;

namespace Foundry.Utilities.Processes;

/// <summary>Captures a bounded stream tail and delivers bounded progress-line segments.</summary>
internal sealed class ProcessOutputCapture(int capacity, Action<string>? callback)
{
    private const int MaximumCallbackLength = 16_384;
    private readonly char[] _tail = new char[capacity];
    private readonly StringBuilder _line = new();
    private int _next;
    private int _count;
    private bool _previousCarriageReturn;
    private bool _lineWasSegmented;
    private volatile bool _callbacksStopped;

    public bool Truncated { get; private set; }

    public async Task ReadAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        char[] chunk = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            Append(chunk.AsSpan(0, read));
        }

        if (_line.Length > 0)
        {
            DeliverLine();
        }
    }

    public void StopCallbacks() => _callbacksStopped = true;

    public override string ToString()
    {
        if (_count < _tail.Length)
        {
            return new string(_tail, 0, _count);
        }

        return string.Create(_count, this, static (destination, capture) =>
        {
            int firstLength = capture._tail.Length - capture._next;
            capture._tail.AsSpan(capture._next, firstLength).CopyTo(destination);
            capture._tail.AsSpan(0, capture._next).CopyTo(destination[firstLength..]);
        });
    }

    private void Append(ReadOnlySpan<char> value)
    {
        Truncated |= value.Length > _tail.Length - _count;
        ReadOnlySpan<char> retained = value.Length >= _tail.Length ? value[^_tail.Length..] : value;
        if (value.Length >= _tail.Length)
        {
            retained.CopyTo(_tail);
            _next = 0;
            _count = _tail.Length;
        }
        else
        {
            int firstLength = Math.Min(retained.Length, _tail.Length - _next);
            retained[..firstLength].CopyTo(_tail.AsSpan(_next));
            retained[firstLength..].CopyTo(_tail);
            _next = (_next + retained.Length) % _tail.Length;
            _count = Math.Min(_tail.Length, _count + retained.Length);
        }

        if (callback is null || _callbacksStopped)
        {
            return;
        }

        foreach (char character in value)
        {
            if (character is '\r' or '\n')
            {
                if (character != '\n' || !_previousCarriageReturn)
                {
                    if (_line.Length > 0 || !_lineWasSegmented)
                    {
                        DeliverLine();
                    }

                    _lineWasSegmented = false;
                }

                _previousCarriageReturn = character == '\r';
                continue;
            }

            _previousCarriageReturn = false;
            _line.Append(character);
            if (_line.Length == MaximumCallbackLength)
            {
                DeliverLine();
                _lineWasSegmented = true;
            }
        }
    }

    private void DeliverLine()
    {
        string text = _line.ToString();
        _line.Clear();
        if (_callbacksStopped)
        {
            return;
        }

        try
        {
            callback?.Invoke(text);
        }
        catch
        {
            // Consumer progress reporting cannot interrupt process capture.
        }
    }
}
