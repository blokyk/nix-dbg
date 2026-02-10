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
            StartInfo = new ProcessStartInfo(NIX_BIN, [
                "--extra-experimental-features", "repl-automation", // note: lix exclusive
                "eval",
                "--file", EntryFile,
                "--debugger",
                ..Flags
            ]) {
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
        Stdout = PipeReader.Create(Process.StandardOutput.BaseStream, new(minimumReadSize: 1)); // avoids hangs for prompt and small reads
        Stderr = PipeReader.Create(Process.StandardError.BaseStream, new(minimumReadSize: 1));
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

    private async Task<bool> _DetectPromptOrReadOutput(CancellationToken ct) {
        if (await IsAtPrompt(consumeIfPresent: true, ct)) {
            OnReplPrompt();
            return await _OnBreak(ct);
        } else {
            // if there's no prompt, just treat everything as raw output
            // note: we DON'T advance the reader, so PrintOutputLine() can just read the same thing again
            return await _PrintOutputLine(ct);
        }
    }

    /// <summary>
    /// Type a line into the REPL. The command should not end with a newline.
    /// </summary>
    /// <param name="cmd">The command that should be typed into the repl, without the final newline</param>
    public Task TypeLine(string cmd, CancellationToken? ct = default)
        // todo: add a locking mechanism to ensure no one tries to write when there's no repl
        // OR when there's multiple things wanting to write (e.g. backtrace parser & user expression)
        => Process.StandardInput.WriteAsync((cmd + "\n").AsMemory(), ct ?? _cancelSource?.Token ?? default);

    public StackTrace CurrentStackTrace { get; private set; }

    public event Action<StackTrace> OnBreak = (_) => {};
    public event Action<StackTrace> OnStep = (_) => {};
    public event Action<StackTrace, string> OnError = (_, _) => {};

    private async Task<bool> _OnBreak(CancellationToken ct) {
        CurrentStackTrace = await StackTrace.CreateFromDebugger(this, ct);

        OnBreak(CurrentStackTrace);

        System.Threading.Thread.Sleep(1000000000);

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

    internal async Task<bool> IsAtPrompt(bool consumeIfPresent = false, CancellationToken? ct = default) {
        ct ??= _cancelSource?.Token ?? default;

        var read = await Stdout!.ReadAtLeastAsync(1, ct.Value);
        var buf = read.Buffer;

        if (buf.IsEmpty)
            return false;

        var present = buf.First.Span[0] == (byte)'\u0005';

        if (consumeIfPresent && present)
            Stdout.AdvanceTo(buf.GetPosition(1));

        return present;
    }
}