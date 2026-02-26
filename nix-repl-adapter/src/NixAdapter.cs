using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Protocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Serialization;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Utilities;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using DAPScope = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.Scope;
using DAPVariable = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.Variable;

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
            // todo: SupportsCancelableEvaluate = true,     // using ^C during eval
            // todo: SupportsEvaluateForHovers = true,      // might not be a good idea, because eval can take a lot of time
            // todo: SupportsValueFormattingOptions = true, // just a matter of writing more c#

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
        try {
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
            //         [ ] allowIFD  [default: false] (i.e. --option allow-import-from-derivation false)

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
                (_) => Protocol.SendEvent(new TerminatedEvent());
            Debugger.OnExit +=
                (exitCode) => Protocol.SendEvent(new ExitedEvent(exitCode));

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
        } catch (Exception e) {
            throw AsProtocolException(e);
        }
    }

    protected override ThreadsResponse HandleThreadsRequest(ThreadsArguments arguments)
        => new([new(0, "main")]);

    protected override void HandleStackTraceRequestAsync(IRequestResponder<StackTraceArguments, StackTraceResponse> responder)
        => _ = Task.Run(async () => {
            try {
                var args = responder.Arguments;

                var trace = await Debugger!.GetStackTrace();

                var requestedFrames = trace.Frames.ClampedSpan(
                    args.StartFrame,
                    args.Levels,
                    zeroCountIsMax: true // if `Levels` is 0, then we should return all the frames;
                                         // otherwise, we only return the specified number of frames
                );

                responder.SetResponse(new([..requestedFrames]));
            } catch (Exception e) {
                responder.SetError(AsProtocolException(e));
            }
        });

    private Lock _scopesLock = new();
    private Dictionary<int, Scope> Scopes;
    protected override void HandleScopesRequestAsync(IRequestResponder<ScopesArguments, ScopesResponse> responder)
        => _ = Task.Run(async () => {
            try {
                var scopes = await Debugger!.GetScopes();
                var scopesDict = scopes.ToDictionary(s => s.Level);
                lock (_scopesLock) {
                    Scopes = scopesDict;
                }

                responder.SetResponse(new([.. scopes.Select(ToDAPScope)]));
            } catch (Exception e) {
                responder.SetError(AsProtocolException(e));
            }
        });

    private static DAPScope ToDAPScope(Scope scope) {
        try {
            return new() {
                // maybe we should instead number them in reverse, so that they stay stable across steps
                // (i.e. if a user opens "level 1" on one step, and then steps into a deeper scope,
                // "level 1" still have the same stuff (and in fact, vscode leaves the scope open between steps))
                Name = $"Level {scope.Level}",
                VariablesReference = scope.Level,
                NamedVariables = scope.Variables.Length,
                Expensive = scope.Variables.Length > 10,
                PresentationHint = DAPScope.PresentationHintValue.Locals,
            };
        } catch (Exception e) {
            throw AsProtocolException(e);
        }
    }

    protected override void HandleVariablesRequestAsync(IRequestResponder<VariablesArguments, VariablesResponse> responder)
        => _ = Task.Run(async () => {
            try {
                var args = responder.Arguments;
                // todo: remove this when implementing arrays exploration
                Debug.Assert(args.Filter is not VariablesArguments.FilterValue.Indexed);

                var scope = Scopes[args.VariablesReference];

                var dapVars = scope.Variables
                    .Skip(args.Start ?? 0)
                    .Take(args.Count is 0 or null ? scope.Variables.Length : args.Count.Value)
                    .Select(v => new DAPVariable() { Name = v, Value = "", EvaluateName = v })
                    .ToList();

                var timeout = args.Timeout is null or -1 ? TimeSpan.MaxValue.Milliseconds : args.Timeout.Value;
                foreach (var var in dapVars) {
                    var ctSource = new CancellationTokenSource(timeout);
                    var.Type = await Debugger!.GetType(var.EvaluateName, ctSource.Token);
                }

                responder.SetResponse(new(dapVars));
            } catch (Exception e) {
                responder.SetError(AsProtocolException(e));
            }
        });

    protected override EvaluateResponse HandleEvaluateRequest(EvaluateArguments arguments) {
        return new("todo: eval", 0);
    }

    protected override ContinueResponse HandleContinueRequest(ContinueArguments arguments) {
        try {
            Debugger?.Continue();
            // apparently, DAs should not send `ContinuedEvent` when it's as a result of a `Continue` or `Launch` event
            //Protocol.SendEvent(new ContinuedEvent())
            return new() { AllThreadsContinued = true };
        } catch (Exception e) {
            throw AsProtocolException(e);
        }
    }

    protected override StepInResponse HandleStepInRequest(StepInArguments arguments) {
        try {
            Debugger?.Step();
            return new(); // ACK
        } catch (Exception e) {
            throw AsProtocolException(e);
        }
    }

    protected override StepOutResponse HandleStepOutRequest(StepOutArguments arguments) {
        try {
            Debugger?.Step();
            return new(); // ACK
        } catch (Exception e) {
            throw AsProtocolException(e);
        }
    }

    protected override NextResponse HandleNextRequest(NextArguments arguments) {
        try {
            Debugger?.Step();
            return new(); // ACK
        } catch (Exception e) {
            throw AsProtocolException(e);
        }
    }

    protected override TerminateResponse HandleTerminateRequest(TerminateArguments arguments) {
        try {
            Debugger?.Stop();
            return new(); // ACK
        } catch (Exception e) {
            throw AsProtocolException(e);
        }
    }

    protected override DisconnectResponse HandleDisconnectRequest(DisconnectArguments arguments) {
        try {
            if (Debugger?.Process.HasExited is false)
                Debugger?.Stop();

            return new(); // ACK
        } catch (Exception e) {
            throw AsProtocolException(e);
        }
    }

    private static ProtocolException AsProtocolException(Exception e, [CallerMemberName] string callerName = "unknown")
        => new($"In {callerName}: " + e.Message + "\n" + e.StackTrace, e);
}