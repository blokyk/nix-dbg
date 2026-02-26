using Blokyk.NixDebugAdapter;

if (args is ["--no-dap"]) {
    var dbg = new NixDebugger("../vscode/sampleWorkspace/default.nix", [], (s) => Console.Error.WriteLine(s));

    dbg.OnOutput += (s) => Console.WriteLine(s);
    dbg.OnErrorOutput += (s) => Console.Error.WriteLine(s);
    dbg.OnExit += (code) => Console.Error.WriteLine($"debuggee exited with code {code}");
    dbg.OnBreak += () => Console.Error.WriteLine($"breakpoint reached!");

    try {
        await dbg.Start();
        Console.WriteLine("Waiting 2 seconds for debugger to stop...");
        Thread.Sleep(TimeSpan.FromSeconds(2));
    } finally {
        dbg.Stop();
    }

    Environment.Exit(0);
}

try {
    var adapter = new NixDebugAdapter(Console.OpenStandardInput(), Console.OpenStandardOutput());
    adapter.Protocol.LogMessage += (sender, e) => adapter.LogLine($"\e[2m{e.Message}\e[0m");

    // adapter.Log.WriteLine($"Waiting for debugger to attach... (pid: {Environment.ProcessId})");
    // for (int i = 0; !(Debugger.IsAttached || i >= 10); i++) {
    //     Thread.Sleep(1000);
    //     adapter.Log.WriteLine("Seconds left: " + i);
    // }

    Console.Error.WriteLine("Starting debug adapter");
    adapter.Run();
} catch (Exception e) {
    File.WriteAllText("/home/blokyk/dev/lab/nix-dbg/log.txt", e.Message);
    throw;
} finally {
    // File.WriteAllText("/dev/pts/3", "nix-dbg exited");
}
