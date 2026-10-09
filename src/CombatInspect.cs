using System;
using System.Collections.Generic;

namespace CharacterReplacement
{
    /// <summary>
    /// Console (F1) and chat command tmcombat: status, on, off, debug [on|off], set &lt;key&gt; &lt;value&gt;.
    /// </summary>
    internal static class CombatInspect
    {
        public static IEnumerable<string> Run(string args)
        {
            var parts = (args ?? "").Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string mode = parts.Length > 0 ? parts[0].ToLowerInvariant() : "status";
            string rest = parts.Length > 1 ? parts[1] : "";
            if (mode == "on" || mode == "off" || mode == "set" || mode == "status" || mode == "debug") return CombatAI.Command(mode, rest);
            return new[] { "tmcombat status | on | off | debug [on|off] | set <key> <value>" };
        }
    }
}
