using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using System.Buffers;
using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;

public record StackTrace(
    ImmutableArray<StackFrame> Frames
) {
    private static readonly byte[] _prompt = "nix-repl> "u8.ToArray();
    public static async Task<StackTrace> CreateFromDebugger(NixDebugger dbg, CancellationToken ct = default) {
        var stdout = dbg.Stdout!; // copy to avoid null checks

        await dbg.TypeLine(":bt");
        // output is something like this:
        //      <empty line>
        // vvvv repeat vvvv
        //      $n: $msg
        //      $file:$line:$column
        //      <empty line>
        // many <line of source code>
        //      <empty line>
        // ^^^^ repeat ^^^^
        //      $prompt

        var frames = ImmutableArray.CreateBuilder<StackFrame>();

        // <empty line>
        await EatLine(stdout, ct);

        do {
            var (num, msg) = await ParseStackNumLine(stdout, ct);
            var (file, line, column) = await ParseSourceLocation(stdout, ct);

            // eat lines that don't start with a non-space character (they're either empty or source code)
            await EatUselessLines(stdout, ct);

            var source = new Source() {
                Name = Path.GetFileName(file),
                Path = File.Exists(file) ? file : null,
            };

            frames.Add(new StackFrame() {
                Id = num,
                Name = msg,
                Line = line,
                Column = column,
                Source = source,
                // PresentationHint = StackFrame.PresentationHintValue.Unknown,
                CanRestart = false,
            });

            Console.WriteLine(frames[^1].Display());
        } while (!await dbg.IsAtPrompt(consumeIfPresent: false, ct));

        // todo: collapse consecutive lines that point to the exact same location (file+line+column)

        return new StackTrace(frames.DrainToImmutable());
    }

    private static async Task EatLine(PipeReader stdout, CancellationToken ct) {
        var res = await stdout.ReadLineAsync(ct);
        stdout.AdvanceTo(res.Buffer.End);
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
    private static async Task EatUselessLines(PipeReader stdout, CancellationToken ct) {
        while (true) {
            var read = await stdout.ReadAtLeastAsync(1, ct);
            var buf = read.Buffer;

            if (!buf.IsEmpty && buf.FirstSpan[0] is not ((byte)' ' or (byte)'\n'))
                break;

            read = await stdout.ReadLineAsync(ct);
            stdout.AdvanceTo(read.Buffer.End);
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