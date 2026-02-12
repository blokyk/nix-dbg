using System.Collections.Immutable;

namespace Blokyk.NixDebugAdapter;

public abstract partial class NixValue {
    public abstract NixType Type { get; }
}

public sealed class NixString : NixValue {
    public override NixType Type => NixType.String;
    public required string Value { get; init; }
}

public sealed class NixPath : NixValue {
    public override NixType Type => NixType.Path;
    public required string Value { get; init; }
}

public sealed class NixNumber : NixValue {
    public override NixType Type => NixType.Number;
    public required int Value { get; init; }
}

public sealed class NixSet : NixValue {
    public override NixType Type => NixType.AttrSet;
    public required ImmutableArray<string> Members { get; init; }

    // public override string ToString() {
    //     if (Members.Length == 0)
    //         return "{ }";
    //     if (Members.Length <= 5)
    //         return "{ " + String.Join("; ", Members) + "; }";

    //     return "{ " + String.Join("; ", Members.Take(5)) + "; [" + (Members.Length-5) + " others] }";
    // }
}

public sealed class NixArray : NixValue {
    public override NixType Type => NixType.Array;
    public required ImmutableArray<NixValue> Value { get; init; }

    // public override string ToString()
    //     => Value.Length == 0
    //         ? "[ ]"
    //         : "[ " + String.Join(", ", Value) + " ]";
}

public sealed class NixFunction : NixValue {
    public override NixType Type => NixType.Function;
}

public sealed class NixDerivation : NixValue {
    public override NixType Type => NixType.Derivation;
    public required string Hash { get; init; }
    public required string Name { get; init; }
    public required string? Version { get; init; }
}

public sealed class NixNull : NixValue {
    public override NixType Type => NixType.Null;
}