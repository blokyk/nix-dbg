namespace Blokyk.NixDebugAdapter;

public enum NixType { String, Path, Number, AttrSet, Array, Function, Derivation, Null }

public static class NixTypeExtensions
{
    extension(NixType type) {
        public string ToDisplayString() => type switch {
            NixType.String     => "string",
            NixType.Path       => "path",
            NixType.Number     => "number",
            NixType.AttrSet    => "attribute set",
            NixType.Array      => "array",
            NixType.Function   => "lambda", // maybe this should be "function"? `builtins.typeOf` returns "lambda", so..
            NixType.Derivation => "derivation",
            NixType.Null       => "null",
            _ => "???"
        };
    }
}