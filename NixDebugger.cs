using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Microsoft.VisualStudio.Threading;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Text;

namespace Blokyk.NixDebugAdapter;

public sealed class NixDebugger
{
    private const string NIX_BIN = "/nix/var/nix/profiles/default/bin/nix";

    // fixme: this is only for debugging
    internal readonly Action<string> logLine;

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
    }

    public event Action<int> OnExit = (_) => {};

    internal PipeReader? Stdout { get; private set; }
    internal PipeReader? Stderr { get; private set; }
    private CancellationTokenSource _cancelSource = new();
    public async Task Start() {
        Process.Exited += (_, _) => {
            _cancelSource.CancelAfter(333);
            OnExit(Process.ExitCode);
        };

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

        _cancelSource.CancelAfter(333); // give a little bit of time to print the output
    }

    public event Action<string> OnErrorOutput = (_) => {};
    public event Action<string> OnOutput = (_) => {};

    private async Task _ListenToStdout() {
        // [x] breakpoint
        // [ ] stacktrace
        // [ ] env
        // [ ] user/debug console/watch expression
        // [x] suppress echo from stdin
        // [x] send the rest to the `OnOutput` event

        var cancellationToken = _cancelSource.Token;
        do {
            await _DetectPromptOrReadOutput(cancellationToken);
        } while(!Stdout!.TryIsCompleted());
    }

    private async Task _PrintOutputLine(CancellationToken ct) {
        var read = await Stdout!.ReadLineAsync(ct);
        var buf = read.Buffer;
        var bufWithoutNewline = buf.Slice(buf.Start, buf.Length-1);
        var line = Encoding.UTF8.GetString(bufWithoutNewline);
        Stdout!.AdvanceTo(buf.End); // use base `buf` so we can move to *after* the newline
        OnOutput(line);
    }

    private async Task _DetectPromptOrReadOutput(CancellationToken ct) {
        if (await RefreshAndCheckPromptStatus(consumeIfPresent: true, ct)) {
            await _OnBreak(ct);
        } else {
            // if there's no prompt, just treat everything as raw output
            // note: we DON'T advance the reader, so PrintOutputLine() can just read the same thing again
            await _PrintOutputLine(ct);
        }
    }

    /// <summary>
    /// Type a line into the REPL. The command should not end with a newline.
    /// This method will only return once the command has started execution.
    /// </summary>
    /// <param name="cmd">The command that should be typed into the repl, without the final newline</param>
    public async Task TypeLine(string cmd, CancellationToken? ct = default) {
        if (ct != _cancelSource.Token)
            ct = _cancelSource.Token.CombineWith(ct ?? default).Token;

        // wait for the prompt to be available
        await _promptEvent.WaitAsync(ct.Value);
        // todo: add a locking mechanism to ensure no one tries to write when there's no repl
        // OR when there's multiple things wanting to write (e.g. backtrace parser & user expression)
        await Process.StandardInput.WriteAsync(
            (cmd + "\n").AsMemory(),
            ct.Value
        );
    }

    public event Action OnBreak = () => {};
    public event Action<string> OnError = (_) => {};

    private async Task _OnBreak(CancellationToken ct) {
        _ = Task.Run(() => OnBreak(), ct);
        await _WaitForContinueOrStep(ct);
    }

    public async Task<StackTrace> GetStackTrace(CancellationToken? ct = default) {
        ct = _cancelSource.Token.CombineWith(ct ?? default).Token;
        await TypeLine(":bt");
        return await StackTrace.CreateFromBacktrace(this, ct.Value);
    }

    private async Task _WaitForContinueOrStep(CancellationToken ct) {
        // create a token we'll cancel when the race is done and one of them has won
        var raceFinishedTokenSource = new CancellationTokenSource();
        var raceFinishedToken = raceFinishedTokenSource.Token;

        var continueTask = _WaitAndContinue(ct).WithCancellation(raceFinishedToken);
        var stepTask = _WaitAndStep(ct).WithCancellation(raceFinishedToken);

        // actually run both tasks in a race, and see which one wins first
        // (double await because it returns a Task<Task>, where the inner task is completed)
        await await Task.WhenAny(continueTask, stepTask);

        // tell the losing task to stop
        await raceFinishedTokenSource.CancelAsync();
    }

    private AsyncAutoResetEvent _continueEvent = new();
    public void Continue() => _continueEvent.Set();
    private async Task _WaitAndContinue(CancellationToken ct) {
        await _continueEvent.WaitAsync(ct);
        await TypeLine(":c", ct);
        logLine("continued");
    }

    private AsyncAutoResetEvent _stepEvent = new();
    public void Step() => _stepEvent.Set();
    private async Task _WaitAndStep(CancellationToken ct) {
        await _stepEvent.WaitAsync(ct);
        await TypeLine(":s", ct);
        logLine("stepped");
    }

    private async Task _ListenToStderr() {
        // todo:
        // 1. detect (non-breakpoint) errors
        // 2. suppress repl-related messages (e.g. 'info: breakpoint reached' or nix version or 'type :? for help')
        // 3. (?) parse (error) backtraces
        // 4. detect error in repl expression (e.g. typed in debug console, watch list, or when expanding values)

        // discard any stuff from stderr
        ReadResult read;
        do {
            read = await Stderr!.ReadAsync();
            var buf = read.Buffer;
            var str = Encoding.UTF8.GetString(buf);
            _ = Task.Run(() => OnErrorOutput(str));
            Stderr.AdvanceTo(buf.End);
        } while (!read.IsCompleted);
    }

    private AsyncAutoResetEvent _promptEvent = new();
    internal async Task<bool> RefreshAndCheckPromptStatus(
        bool consumeIfPresent = false,
        CancellationToken? ct = default
    ) {
        ct = _cancelSource.Token.CombineWith(ct ?? default).Token;

        var read = await Stdout!.ReadAtLeastAsync(1, ct.Value);
        var buf = read.Buffer;

        if (buf.IsEmpty)
            return false;

        var present = buf.First.Span[0] == (byte)'\u0005';

        if (present) {
            if (consumeIfPresent)
                Stdout.AdvanceTo(buf.GetPosition(1));
            _promptEvent.Set();
            return true;
        } else {
            return false;
        }
    }
}