using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using System.Buffers;
using System.Collections.Immutable;
using System.Text;

namespace Blokyk.NixDebugAdapter;

internal static class Utils
{
    public static string StripANSI(ReadOnlySpan<char> str) {
        var sb = new StringBuilder();

        // first append the starting part of the string that doesn't have
        // any escape, so that the loop always starts with an escape sequence
        var firstEscape = str.IndexOf('\e');

        // if there's no escape in the string, just return it raw
        if (firstEscape < 0)
            return str.ToString();

        sb.Append(str[..firstEscape]);

        foreach (var splitInfo in str[firstEscape..].Split('\e')) {
            var part = str[splitInfo.Start..splitInfo.End];
            var escapeLength = part.IndexOf('m') + 1;
            sb.Append(part[escapeLength..]);
        }

        return sb.ToString();
    }

    extension<T>(ImmutableArray<T> arr) {
        /// <summary>
        /// Given a start index and length, take a slice out of an <see cref="ImmutableArray{T}"/>,
        /// while avoiding out-of-bounds errors by clamping it to the bounds of the array.
        /// </summary>
        public ReadOnlySpan<T> ClampedSpan(int? start = null, int? count = null, bool zeroCountIsMax = true) {
            var realStart = Math.Min(start ?? 0, arr.Length - 1);
            var tmpCount = zeroCountIsMax
                ? (count is null or 0 ? arr.Length : count.Value)
                : (count is null ? arr.Length : count.Value);
            var realCount = Math.Min(tmpCount, arr.Length - realStart);
            return arr.AsSpan(realStart, realCount);
        }
    }

    extension(StackFrame frame) {
        public string Display()
            => $"{frame.Id}: {frame.Name} @ {frame.Source.Name}:{frame.Line}:{frame.Column}";
    }
}