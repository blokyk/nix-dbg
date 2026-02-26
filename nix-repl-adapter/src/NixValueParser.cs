namespace Blokyk.NixDebugAdapter;

public abstract partial class NixValue
{
    public static async Task<NixValue> CreateFromEval(NixDebugger dbg, CancellationToken ct) {
        var stdout = dbg.Stdout!;
        return null!;
    }
}