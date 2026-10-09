using System;
using System.Collections.Generic;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Gameplay;
using SonsSdk;

namespace CharacterReplacement
{
    /// <summary>
    /// Scales the search range of Kelvin's orders (shared RobbyOrderData asset, so it affects Kelvin and the soldiers).
    /// Original values are remembered so repeated applies never stack.
    /// </summary>
    internal static class OrderRange
    {
        private static readonly Dictionary<IntPtr, float> OrigRange = new();
        private static readonly Dictionary<IntPtr, float> OrigClear = new();

        public static void Apply(float rangeMul, float clearMul)
        {
            float structMul = Math.Clamp(Config.HolderRangeMultiplier.Value, 0.25f, 10f);
            rangeMul = Math.Clamp(rangeMul, 0.25f, 10f);
            clearMul = Math.Clamp(clearMul, 0.25f, 5f);
            var seen = new HashSet<IntPtr>();
            foreach (VailActorTypeId type in new[] { CharacterReplacement.RobbyType, CharacterReplacement.PlayerRobbyType })
            {
                try
                {
                    VailActor prefab = ActorTools.GetPrefab(type);
                    Robby robby = prefab ? prefab.GetComponentInChildren<Robby>(true) : null;
                    RobbyOrderData data = robby ? robby._orderData : null;
                    if (!data || !seen.Add(data.Pointer)) continue;
                    int n = 0;
                    if (data._getItemOrders != null) foreach (var o in data._getItemOrders) { Scale(o, rangeMul, $"get {o._itemType}"); n++; }
                    if (data._clearAreaOrders != null) foreach (var o in data._clearAreaOrders)
                    {
                        Scale(o, rangeMul, $"clear {o._itemName}");
                        if (!OrigClear.TryGetValue(o.Pointer, out float c)) { c = o._clearRadius; OrigClear[o.Pointer] = c; }
                        o._clearRadius = c * clearMul;
                        RLog.Msg($"{CharacterReplacement.Tag} Order clear {o._itemName}: radius {c} -> {o._clearRadius}");
                        n++;
                    }
                    if (data._maintainOrders != null) foreach (var o in data._maintainOrders) { Scale(o, structMul, $"maintain {o._itemName}"); n++; }
                    if (data._buildItemOrders != null) foreach (var o in data._buildItemOrders) { Scale(o, structMul, $"build {o._itemName} ({o._buildStimuli})"); n++; }
                    RLog.Msg($"{CharacterReplacement.Tag} Order ranges applied to {data.name} ({type}): {n} orders, range x{rangeMul}, clear radius x{clearMul}, structures x{structMul}");
                }
                catch (Exception e)
                {
                    RLog.Error($"{CharacterReplacement.Tag} Applying order ranges for {type} failed: {e}");
                }
            }
        }

        private static void Scale(Robby.RobbyOrder o, float mul, string label)
        {
            if (o == null) return;
            if (!OrigRange.TryGetValue(o.Pointer, out float r)) { r = o._maxStimuliRange; OrigRange[o.Pointer] = r; }
            o._maxStimuliRange = r * mul;
            RLog.Msg($"{CharacterReplacement.Tag} Order {label}: search range {r} -> {o._maxStimuliRange}");
        }
    }
}
