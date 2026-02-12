using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;

namespace Blokyk.NixDebugAdapter;

public record Scope(int Level, ImmutableArray<string> Variables)
{
    /// <summary>
    /// Parse an environment listing from the REPL.
    /// (The <c>:env</c> command must have already been triggered.)
    /// </summary>
    public static async Task<ImmutableArray<Scope>> CreateListFromEnv(NixDebugger dbg, CancellationToken ct) {
        var stdout = dbg.Stdout!;

        // await Task.Delay(10000);

        var scopes = ImmutableArray.CreateBuilder<Scope>();
        do {
            var scope = await CreateFromEnvLevel(dbg, ct);
            scopes.Add(scope);
            await stdout.EatLine(ct); // eat the empty line
        } while (!await isNextReadEmptyLine());

        return scopes.DrainToImmutable();

        async Task<bool> isNextReadEmptyLine() {
            var res = await stdout.ReadAtLeastAsync(1, ct);
            var buf = res.Buffer;
            var cond = buf.IsEmpty || buf.FirstSpan[0] == '\n';
            // if it is an empty line, consume it, otherwise don't
            if (cond)
                stdout.AdvanceTo(buf.GetPosition(1));
            else
                stdout.AdvanceTo(buf.Start);
            return cond;
        }
    }

    /// <summary>
    /// Parse a single environment level from the REPL.
    /// (The <c>:env</c> command must have already been triggered.)
    /// </summary>
    public static async Task<Scope> CreateFromEnvLevel(NixDebugger dbg, CancellationToken ct) {
        var stdout = dbg.Stdout!;
        // format:
        //      Env level $n
        //      $foo $bar $baz $truc $muche ...
        //      <empty line>
        // OR
        //      Env level $n
        //      static: $foo $bar $baz $truc $muche ...
        //      <empty line>

        var level = await ParseEnvHeader(stdout, ct);
        var vars = await ParseVarList(stdout, ct);
        return new Scope(level, vars);
    }

    private static readonly SearchValues<char> _digits = SearchValues.Create('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
    private static async Task<int> ParseEnvHeader(PipeReader stdout, CancellationToken ct) {
        var res = await stdout.ReadLineAsync(ct);
        var buf = res.Buffer;

        // .AsSpan makes sure that line[..numEnd] is .Split and not .Substring
        // and [..^1] allows us to exclude the final newline (buf.Slice is more expansive)
        var line = Encoding.UTF8.GetString(buf).AsSpan(..^1);
        line = Utils.StripANSI(line);

        var levelIdx = line.IndexOfAny(_digits);
        var level = Int32.Parse(line[levelIdx..]);

        stdout.AdvanceTo(buf.End);
        return level;
    }

    private static async Task<ImmutableArray<string>> ParseVarList(PipeReader stdout, CancellationToken ct) {
        var res = await stdout.ReadLineAsync(ct);
        var buf = res.Buffer;

        // we can't/don't use .AsSpan here because in this case we *want* a string-y .Split(), so line should be a `string`
        var line = Encoding.UTF8.GetString(buf);
        // however, here we take advantage of the fact that StripANSI wants a span to
        // cut off the \n we don't want, without having to add more locals or allocate a new string
        line = Utils.StripANSI(line.AsSpan(..^1));

        var vars = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).AsSpan();

        // sometimes, the variable list will have 'static: ' printed at the beginning;
        // in that case, it'll end up being the first element, so remove it if that's
        // the case
        if (vars.Length > 0 && vars[0] == "static:")
            vars = vars[1..];

        stdout.AdvanceTo(buf.End);
        return [.. vars];
    }
}