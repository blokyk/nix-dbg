using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Protocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Serialization;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading.Channels;

namespace Blokyk.NixDebugAdapter;

internal class NixDebugAdapter : DebugAdapterBase
{
    private TextWriter Log;

    public NixDebugAdapter(Stream stdin, Stream stdout) {
        Log = Console.Error;
    //   Log = new StreamWriter(File.OpenWrite("/home/blokyk/dev/lab/nix-dbg/log.txt")) { AutoFlush = true };
    //   Log = new StreamWriter(File.OpenWrite("/dev/pts/4")) { AutoFlush = true };
      InitializeProtocolClient(stdin, stdout);

      LogLine("NixDebugAdapter ctor called");
    }

    public void Run() {
        LogLine("NixDebugAdapter running...");
        Protocol.Run();
    }

    protected override InitializeResponse HandleInitializeRequest(InitializeArguments arguments) {
        LogLine($"Connection initialized with '{arguments.ClientName}' ({arguments.ClientID}), with args:");
        LogLine(JsonConvert.SerializeObject(arguments));

        return new() {
            // todo: SupportsCancelableEvaluate = true,
            // todo: SupportsEvaluateForHovers = true,
            // todo: SupportsValueFormattingOptions = true,

            SupportsTerminateRequest = true,

            // This request indicates that the client has finished initialization of the debug adapter
            SupportsConfigurationDoneRequest = true,
        };
    }

    protected override ConfigurationDoneResponse HandleConfigurationDoneRequest(ConfigurationDoneArguments arguments)
        => new(); // ACK

    public NixDebugger? Debugger { get; set; }

    internal void LogLine(object obj) {
        Log.WriteLine(obj);
        try {
            Protocol.SendEvent(new OutputEvent(obj?.ToString() + "\n") { Category = OutputEvent.CategoryValue.Console, Data = obj });
        } catch {};
    }

    protected override LaunchResponse HandleLaunchRequest(LaunchArguments arguments) {
        var filename = arguments.ConfigurationProperties.GetValueAsString("program");
        if (filename is null or "")
            throw new ProtocolException("launch failed: launch config did not specify 'program'");
        var filepath = Path.GetFullPath(filename);
        if (!File.Exists(filepath))
            throw new ProtocolException($"launch failed: 'program' file did not exist ({filename})");

        var nixFlags = arguments.ConfigurationProperties.GetValue<List<string>?>("nixFlags", JTokenType.Array) ?? [];

        // todo: config options:
        //         [ ] pure [default: false, except if filename is `flake.nix`] (i.e. not --impure)
        //         [ ] breakOnTrace [default: false] (i.e. --option debugger-on-trace true)
        //         [ ] ignoreTry [default: true] (i.e. --ignore-try)

        LogLine($"launching file '{filename}' with flags {String.Join(' ', nixFlags)}...");

        Debugger = new(filepath, nixFlags, LogLine);

        // Debuggee.ErrorDataReceived +=
        //     (sender, e) => OnErrorOutput(e.Data);
        // Debuggee.OutputDataReceived +=
        //     (sender, e) => OnOutput(e.Data);

        // Debuggee.Exited += (_, _) => FlushDebuggeeOutput();
        Debugger.OnExit +=
            (_) => LogLine("nix eval exited");
        Debugger.OnExit +=
            (exitCode) => Protocol.SendEvent(new ExitedEvent(exitCode));
        Debugger.OnExit +=
            (_) => Protocol.SendEvent(new TerminatedEvent());

        Debugger.OnErrorOutput +=
            (msg) => Protocol.SendEvent(new OutputEvent() { Output = msg, Category = OutputEvent.CategoryValue.Stderr });
        Debugger.OnOutput +=
            (msg) => Protocol.SendEvent(new OutputEvent() { Output = msg, Category = OutputEvent.CategoryValue.Stdout });

        Debugger.OnBreak +=
            () => Protocol.SendEvent(new StoppedEvent() {
                AllThreadsStopped = true,
                Reason = StoppedEvent.ReasonValue.Breakpoint,
                Description = "Breakpoint reached",
            });

        Debugger.OnError +=
            (msg) => Protocol.SendEvent(new StoppedEvent() {
                AllThreadsStopped = true,
                Reason = StoppedEvent.ReasonValue.Exception,
                Description = "Error reached",
                Text = msg
            });

        _ = Debugger.Start();

        Protocol.SendEvent(new ProcessEvent() {
            Name = Debugger.Process.ProcessName,
            SystemProcessId = Debugger.Process.Id,
            IsLocalProcess = true,
            StartMethod = ProcessEvent.StartMethodValue.Launch,
            PointerSize = 64
        });

        return new(); // ACK
    }

    protected override void HandleStackTraceRequestAsync(IRequestResponder<StackTraceArguments, StackTraceResponse> responder)
        => _ = Task.Run(async () => {
            var args = responder.Arguments;

            var trace = await Debugger!.GetStackTrace();
            var allFrames = trace.Frames;

            var reqStart = args.StartFrame ?? 0;
            // if `Levels` is 0 or null, then we should return all the frames; otherwise, we only return a limited number of frames
            var reqCount = args.Levels is 0 or null ? allFrames.Length : args.Levels.Value;

            // clamp the start idx and count so it doesn't exceed the real frame count
            var start = Math.Min(reqStart, allFrames.Length - 1);
            var count = Math.Min(reqCount, allFrames.Length - start);

            var requestedFrames = allFrames.AsSpan(start, count);
            responder.SetResponse(new([..requestedFrames]));
        });

    protected override ThreadsResponse HandleThreadsRequest(ThreadsArguments arguments)
        => new([new(0, "main")]);

    protected override ScopesResponse HandleScopesRequest(ScopesArguments arguments)
        => new([]);

    protected override ContinueResponse HandleContinueRequest(ContinueArguments arguments) {
        Debugger?.Continue();
        // apparently, DAs should not send `ContinuedEvent` when it's as a result of a `Continue` or `Launch` event
        //Protocol.SendEvent(new ContinuedEvent())
        return new() { AllThreadsContinued = true };
    }

    protected override StepInResponse HandleStepInRequest(StepInArguments arguments) {
        Debugger?.Step();
        return new(); // ACK
    }

    protected override StepOutResponse HandleStepOutRequest(StepOutArguments arguments) {
        Debugger?.Step();
        return new(); // ACK
    }

    protected override NextResponse HandleNextRequest(NextArguments arguments) {
        Debugger?.Step();
        return new(); // ACK
    }

    protected override TerminateResponse HandleTerminateRequest(TerminateArguments arguments) {
        Debugger?.Stop();
        return new(); // ACK
    }

    protected override DisconnectResponse HandleDisconnectRequest(DisconnectArguments arguments)
        => new(); // ACK
}