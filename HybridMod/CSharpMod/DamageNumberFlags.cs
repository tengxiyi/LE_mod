// DamageNumberFlags.cs - interpretation of the game's `HitEvents` flags.
//
// The flags arrive as the second argument of `ProtectionClass.damageNumberEvent`:
//
//     delegate void DamagerNumbeAction(Single damage, HitEvents hitEvents);
//
// The values below were read from the game's own assembly with tools/DumpEnum
// (Il2Cpp.HitEvents, underlying Int32):
//
//     0  = None                          32  = Block
//     1  = Hit                           64  = MeleeHit
//     2  = Crit                         128  = Parry
//     4  = Kill                         256  = SuperCrit
//     8  = Freeze                        31  = RequiresDetailedAbilityEvent (mask)
//    16  = Stun
//
// They are hard-coded rather than discovered at runtime because a wrong guess here is silent: a
// missed crit flag would just make the crit rate read low, with nothing to indicate why. Pinning the
// observed values and asserting them from the harness is the honest alternative to guessing.
//
// IMPORTANT LIMITATION
// --------------------
// `HitEvents` carries no "dealt vs taken" flag. There is no Incoming/Taken/Received member, and the
// event name does not disambiguate either: the same `damageNumberEvent` fires for numbers shown over
// enemies and over the player. Therefore `IsIncoming` cannot be answered from the event alone and is
// deliberately not attempted here. The DPS figures that matter (total damage, hit count, crit rate)
// need only the crit flags.
//
// Kept free of MelonLoader/Unity so the harness can exercise it.

namespace HybridMod.Capture
{
    /// <summary>Reads the game's <c>HitEvents</c> flag values.</summary>
    public static class DamageNumberFlags
    {
        // Values from Il2Cpp.HitEvents, read via tools/DumpEnum.
        public const int None = 0;
        public const int Hit = 1;
        public const int Crit = 2;
        public const int Kill = 4;
        public const int Freeze = 8;
        public const int Stun = 16;
        public const int Block = 32;
        public const int MeleeHit = 64;
        public const int Parry = 128;
        public const int SuperCrit = 256;

        /// <summary>Union of every crit-like flag: an ordinary crit and a "super" crit.</summary>
        public const int AnyCrit = Crit | SuperCrit;

        /// <summary>
        /// True when the flags contain a critical-hit indicator.
        /// </summary>
        /// <remarks>
        /// Zero is explicitly not a crit: "no flags" means an ordinary hit, and an ungated
        /// <c>(flags &amp; mask) != 0</c> test would be fine here but the explicit zero check documents
        /// the intent and protects against a future mask change.
        /// </remarks>
        public static bool IsCrit(int flags) => flags != 0 && (flags & AnyCrit) != 0;

        /// <summary>
        /// Always false: the game's flags cannot express direction.
        /// </summary>
        /// <remarks>
        /// Kept as an explicit, documented "no" so callers do not silently invent a heuristic. Damage
        /// taken versus dealt would have to be determined from context (which actor's protection was
        /// hit), not from these flags.
        /// </remarks>
        public static bool IsIncoming(int flags) => false;

        /// <summary>Human-readable decoding of a flag value, for logging.</summary>
        public static string Describe(int flags)
        {
            if (flags == 0) return "None";

            var parts = new System.Collections.Generic.List<string>();
            void Add(int bit, string name) { if ((flags & bit) == bit) parts.Add(name); }

            Add(Hit, "Hit");
            Add(Crit, "Crit");
            Add(Kill, "Kill");
            Add(Freeze, "Freeze");
            Add(Stun, "Stun");
            Add(Block, "Block");
            Add(MeleeHit, "MeleeHit");
            Add(Parry, "Parry");
            Add(SuperCrit, "SuperCrit");

            return parts.Count == 0 ? $"0x{flags:X} (未识别)" : string.Join("|", parts);
        }
    }
}
