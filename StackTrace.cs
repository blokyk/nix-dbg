using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Text;

public record StackTrace(
    ImmutableArray<StackFrame> Frames
) {
    /// <summary>
    /// Parse a stack trace (multiple stack traces) out of a backtrace on stdout.
    /// (The <c>:bt</c> command must have already been triggered.)
    /// </summary>
    public static async Task<StackTrace> CreateFromBacktrace(NixDebugger dbg, CancellationToken ct = default) {
        var stdout = dbg.Stdout!; // copy to avoid null checks

        // <empty line>
        await stdout.EatLine(ct);

        var frames = ImmutableArray.CreateBuilder<StackFrame>();
        do {
            var frame = await StackFrame.CreateFromBacktrace(dbg, ct);
            // todo: collapse consecutive lines that point to the exact same location (file+line+column)
            frames.Add(frame);
        } while (!await dbg.RefreshAndCheckPromptStatus(consumeIfPresent: false, ct));

        return new StackTrace(frames.DrainToImmutable());
    }
}

public static class StackFrameUtils
{
    extension(StackFrame) {
        /// <summary>
        /// Parse a single stack frame out of a backtrace on stdout.
        /// (The <c>:st</c> command must have already been triggered.)
        /// </summary>
        public static async Task<StackFrame> CreateFromBacktrace(NixDebugger dbg, CancellationToken ct = default) {
            var stdout = dbg.Stdout!;
            // format is like this:
            //      $n: $msg
            //      $file:$line:$column
            // many <empty line or line of source code>

            var (num, msg) = await ParseStackNumLine(stdout, ct);
            var (file, line, column) = await ParseSourceLocation(stdout, ct);

            dbg.logLine($"read stack frame info {num}, gonna slurp all the useless lines...");

            // eat lines that don't start with a non-space character (they're either empty or source code)
            await EatUselessLines(dbg.logLine, stdout, ct);

            dbg.logLine($"lines slurped!");

            var source = new Source() {
                Name = Path.GetFileName(file),
                Path = File.Exists(file) ? file : null,
            };

            return new StackFrame() {
                Id = num,
                Name = msg,
                Line = line,
                Column = column,
                Source = source,
                // PresentationHint = StackFrame.PresentationHintValue.Unknown,
                CanRestart = false,
            };
        }
    }

    private static async Task<(int num, string msg)> ParseStackNumLine(PipeReader stdout, CancellationToken ct) {
        // $n: $msg
        var res = await stdout.ReadLineAsync(ct);
        var buf = res.Buffer;

        // .AsSpan makes sure that line[..numEnd] is .Split and not .Substring
        // and [..^1] allows us to exclude the final newline (buf.Slice is more expansive)
        var line = Encoding.UTF8.GetString(buf).AsSpan()[..^1];
        line = Utils.StripANSI(line);

        var numEnd = line.IndexOf(':');
        var num = Int32.Parse(line[..numEnd]);

        var msg = line[(numEnd + 2)..].ToString();

        stdout.AdvanceTo(buf.End);
        return (num, msg);
    }

    // get rid of lines we don't care about: empty lines and source code lines
    private static async Task EatUselessLines(Action<string> log, PipeReader stdout, CancellationToken ct) {
        while (true) {
            log("waiting for at least one char...");
            var read = await stdout.ReadAtLeastAsync(1, ct);
            var buf = read.Buffer;

            stdout.AdvanceTo(buf.Start);

            if (!buf.IsEmpty && buf.FirstSpan[0] is not ((byte)' ' or (byte)'\n'))
                break;

            log($"it *is* a useless line (char was 0x{buf.FirstSpan[0]:x} btw)");

            read = await stdout.ReadLineAsyncDebug(log, ct);
            stdout.AdvanceTo(read.Buffer.End);
            log("useless line read, onto the next!");
        }
    }

    private static async Task<(string filepath, int line, int column)> ParseSourceLocation(
        PipeReader stdout, CancellationToken ct
    ) {
        var res = await stdout.ReadLineAsync(ct);
        var buf = res.Buffer;

        var line = Encoding.UTF8.GetString(buf).AsSpan()[..^1]; // AsSpan: get span-y .Split(); ..^1: drop final newline
        line = Utils.StripANSI(line);

        Span<Range> partsPos = stackalloc Range[3];
        _ = line.Split(partsPos, ':');

        var filepath = line[partsPos[0].Start..partsPos[^3].End];
        var lineNumber = Int32.Parse(line[partsPos[^2]]);
        var columnNumber = Int32.Parse(line[partsPos[^1]]);

        stdout.AdvanceTo(buf.End);

        return (filepath.ToString(), lineNumber, columnNumber);
    }
}