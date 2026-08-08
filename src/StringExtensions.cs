using System;
using System.Runtime.CompilerServices;

namespace MyAiGen;

internal static partial class StringExtensions
{
    /// <summary>Build [HH:mm:ss] message\n in a single allocation via String.Create.</summary>
    public static string FormatLogLine(string message)
    {
        var now = DateTime.Now;
        return string.Create(12 + message.Length, (message, now), static (span, state) =>
        {
            span[0] = '[';
            WriteTwoDigits(span[1..], state.now.Hour);
            span[3] = ':';
            WriteTwoDigits(span[4..], state.now.Minute);
            span[6] = ':';
            WriteTwoDigits(span[7..], state.now.Second);
            span[9] = ']';
            span[10] = ' ';
            state.message.AsSpan().CopyTo(span[11..]);
            span[^1] = '\n';
        });
    }

    /// <summary>
    /// Copy existing_prefix + [HH:mm:ss] new_message\n into one allocation.
    /// Eliminates the text[..range] substring and the intermediate interpolation
    /// that LogReplaceLast otherwise creates as two separate strings before
    /// concatenating them.
    /// </summary>
    public static string ReplaceLastLogLine(string text, int prefixLength, string message)
    {
        var now = DateTime.Now;
        return string.Create(
            prefixLength + 12 + message.Length,
            (text, message, now, prefixLength),
            static (span, state) =>
            {
                state.text.AsSpan()[..state.prefixLength].CopyTo(span);
                var dest = span[state.prefixLength..];
                dest[0] = '[';
                WriteTwoDigits(dest[1..], state.now.Hour);
                dest[3] = ':';
                WriteTwoDigits(dest[4..], state.now.Minute);
                dest[6] = ':';
                WriteTwoDigits(dest[7..], state.now.Second);
                dest[9] = ']';
                dest[10] = ' ';
                state.message.AsSpan().CopyTo(dest[11..]);
                dest[^1] = '\n';
            });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteTwoDigits(Span<char> dest, int value)
    {
        dest[0] = (char)('0' + value / 10);
        dest[1] = (char)('0' + value % 10);
    }
}
