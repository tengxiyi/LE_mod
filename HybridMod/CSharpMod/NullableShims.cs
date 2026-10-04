// NullableShims.cs - minimal polyfills so the compiler can emit nullability metadata.
//
// Why this file exists
// --------------------
// This project is built without a .NET SDK, using Roslyn's `csc` directly with `-nostdlib+`
// against the net6.0 *reference* pack. That pack does not carry the compiler-internal types
// `System.Runtime.CompilerServices.NullableAttribute` / `NullableContextAttribute`, which Roslyn
// needs whenever it emits nullable annotations (for example inside a lambda passed where a
// nullable-annotated delegate is expected).
//
// Declaring them as internal types is the documented workaround: the compiler finds them in the
// compilation and stops reporting CS0656. They are metadata only — no runtime behaviour — and are
// never emitted into the output assembly unless something actually needs them.
//
// If this project ever moves to a normal SDK-style build, delete this file.

#if !NET5_0_OR_GREATER

namespace System.Runtime.CompilerServices
{
    /// <summary>Records nullability for a type or type argument.</summary>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method |
        AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Event |
        AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.GenericParameter,
        AllowMultiple = false, Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public readonly byte[] NullableFlags;

        public NullableAttribute(byte flag) => NullableFlags = new[] { flag };

        public NullableAttribute(byte[] flags) => NullableFlags = flags;
    }

    /// <summary>Records the ambient nullability context of a member.</summary>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method |
        AttributeTargets.Interface | AttributeTargets.Delegate | AttributeTargets.Constructor,
        AllowMultiple = false, Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public readonly byte Flag;

        public NullableContextAttribute(byte flag) => Flag = flag;
    }
}

#endif
