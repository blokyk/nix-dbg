using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;

public sealed class NixDebugger
{
    private const string NIX_BIN = "/nix/var/nix/profiles/default/bin/nix";

    private readonly Action<string> logLine;

    public string EntryFile;
    public IEnumerable<string> Flags;
    public Process Process { get; private set; }

    public NixDebugger(string entryFile, IEnumerable<string> flags, Action<string> LogLine) {
        EntryFile = entryFile;
        Flags = flags;
        logLine = LogLine;

        Process = new() {
            EnableRaisingEvents = true,
            StartInfo = new ProcessStartInfo(NIX_BIN, ["eval", "--file", EntryFile, "--debugger", ..Flags]) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            },
        };

        OnErrorOutput = (msg) => logLine($"stderr: {msg}");
    }

    public event Action<int> OnExit = (_) => {};

    internal PipeReader? Stdout;
    internal PipeReader? Stderr;
    private CancellationTokenSource _cancelSource = new();
    public async Task Start() {
        Process.Exited += (sender, e) => OnExit(Process.ExitCode);

        Process.Start();

        logLine($"nix eval started, pid {Process.Id}");

        Process.StandardInput.AutoFlush = true; // avoids deadlocks when typing
        Stdout = PipeReader.Create(Process.StandardOutput.BaseStream);
        Stderr = PipeReader.Create(Process.StandardError.BaseStream);
        _cancelSource = new();

        await Task.WhenAny(_ListenToStdout(), _ListenToStderr());
    }

    public void Stop() {
        if (Process.HasExited) {
            logLine("Debugger was not running any nix process");
            return;
        }

        logLine("Stopping the nix eval process");
        Process.Kill();

        _cancelSource.Cancel();
    }

    public event Action<string> OnErrorOutput;
    public event Action<string> OnOutput = (_) => {};

    public event Action OnReplPrompt = () => {};

    private async Task _ListenToStdout() {
        // [x] breakpoint
        // [ ] stacktrace
        // [ ] env
        // [ ] user/debug console/watch expression
        // [x] suppress echo from stdin
        // [x] send the rest to the `OnOutput` event

        var cancellationToken = _cancelSource.Token;
        bool isComplete;
        do {
            isComplete = await _DetectPromptOrReadOutput(cancellationToken);
        } while(!isComplete);
    }

    private async Task<bool> _PrintOutputLine(CancellationToken ct) {
        var read = await Stdout!.ReadLineAsync(ct);
        var buf = read.Buffer;
        var bufWithoutNewline = buf.Slice(buf.Start, buf.Length-1);
        var line = Encoding.UTF8.GetString(bufWithoutNewline);
        Stdout!.AdvanceTo(buf.End); // use base `buf` so we can move to *after* the newline
        OnOutput(line);
        return read.IsCompleted;
    }

    private static readonly byte[] _prompt = "nix-repl> "u8.ToArray();
    private async Task<bool> _DetectPromptOrReadOutput(CancellationToken ct) {
        var read = await Stdout!.ReadAsync(ct);
        var buf = read.Buffer;
        var reader = new SequenceReader<byte>(buf);

        // if there's no prompt, then it's raw eval output stuff (e.g. program result)
        if (!await Stdout.StartsWith(_prompt, ct)) {
            // note: we DON'T advance the reader, so PrintOutputLine() can just read the same thing again
            return await _PrintOutputLine(ct);
        }

        // if there's a prompt, then mark it as consumed, call the prompt callback, and change the state
        Stdout!.AdvanceTo(buf.GetPosition(offset: 10, buf.Start)); // 10 is the number of chars in the prompt
        OnReplPrompt();
        return await _OnBreak(ct);
    }

    private int _typedChars = 0;
    private async Task _DiscardTypingEcho(CancellationToken ct) {
        // the repl prints to stdout the things we type/write into stdin,
        // so we need some way to "eat" those characters we don't care about
        // before we can start reading useful stuff

        ReadResult read;
        do {
            read = await Stdout!.ReadAsync(ct);
            var buf = read.Buffer;

            // consume all the characters we can, but no more than that:
            //   - if `buf` is smaller than the number of chars we typed,
            //     then only consume `buf.Length` characters, and update
            //     the value of `_typedChars`
            //   - if `buf` is bigger than the number of chars we typed,
            //     then only consume `_typedChars` characters, and set it
            //     to 0 (since _typedChars - typedChars = 0)
            var charsToConsume = Math.Min(buf.Length, _typedChars);
            Stdout!.AdvanceTo(buf.GetPosition(charsToConsume));
            _typedChars -= (int)charsToConsume;
        } while (_typedChars != 0 && !read.IsCompleted);
    }

    /// <summary>
    /// Type a line into the REPL.
    /// WARNING: <strong>This should only be used right after <see cref="OnReplPrompt"/>
    /// or <see cref="OnBreak"/> has been raised</strong>, to ensure that no other
    /// methods are trying to read the input. Otherwise, there is a very high chance this
    /// will completely block/botch the debugger's output parsing.
    /// </summary>
    /// <param name="cmd">The command that should be typed into the repl</param>
    public async Task TypeLine(string cmd, CancellationToken? ct = default) {
        // todo: add a locking mechanism to ensure no one tries to write when there's no repl
        // OR when there's multiple things wanting to write (e.g. backtrace parser & user expression)
        _typedChars += cmd.Length + 1; // +1 for the newline
        await Process.StandardInput.WriteAsync(cmd + "\n");
        await _DiscardTypingEcho(ct ?? _cancelSource.Token);
    }

    public event Action<StackTrace> OnBreak = (_) => {};
    public event Action<StackTrace> OnStep = (_) => {};
    public event Action<StackTrace, string> OnError = (_, _) => {};

    private async Task<bool> _OnBreak(CancellationToken ct) {
        var stacktrace = await StackTrace.CreateFromDebugger(this, ct);

        OnBreak(stacktrace);

        // var envList = await _GetEnvList(ct);

        // fixme: how the FUCK do we map environment scopes to stack frames???
        // (this should be `foreach (var frame in stacktrace) { foreach (var varName in envList[frame]) }`)
        //
        // idea: in theory, "all" we need to do is detect which frames are inside of a not-yet-seen lexical scope.
        //       however, in practice this'd probably require parsing :(
        //
        // idea: another way to do this would be to iterate through all frames with `:st <n>` and check the 0th env frame every time

        // var varValues = 
        // foreach (var varname in envList[0]) {
            
        // }

        // todo: detect whether we're on break because of an error or because of a breakpoint/trace

        return false;
    }

    private async Task _ListenToStderr() {
        //! 1. detect (non-breakpoint) errors
        //  2. suppress repl-related messages (e.g. 'info: breakpoint reached' or nix version or 'type :? for help')
        //  3. parse (error) backtraces
        //  4. detect error in repl expression (e.g. typed in debug console, watch list, or when expanding values)
        ReadResult read;
        do {
            read = await Stderr!.ReadAsync();
            var buf = read.Buffer;
            Stderr.AdvanceTo(buf.End);
        } while (!read.IsCompleted);
    }
}