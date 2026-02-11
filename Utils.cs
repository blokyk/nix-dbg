using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using System.Buffers;
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

    extension(StackFrame frame) {
        public string Display()
            => $"{frame.Id}: {frame.Name} @ {frame.Source.Name}:{frame.Line}:{frame.Column}";
    }
}