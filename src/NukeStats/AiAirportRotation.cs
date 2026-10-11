using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;

namespace NukeStats
{
    // Only deployment ordering is changed. Native aircraft supply, reserve/cap checks,
    // loadout selection, hangar selection, and the successful-deployment return stay intact.
    internal static class AiAirportRotation
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> PreferredAirbases;
        internal static ConfigEntry<bool> LogSelections;
        internal static bool PatchActive { get; private set; }

        private static readonly FieldInfo SortedAirbases = AccessTools.Field(typeof(FactionHQ), "airbasesSorted");
        private static readonly MethodInfo NativeSpawn = AccessTools.Method(typeof(Airbase), "TrySpawnAircraft",
            new[] { typeof(Player), typeof(AircraftDefinition), typeof(LiveryKey), typeof(Loadout), typeof(float) });
        private static readonly MethodInfo WrapperSpawn = AccessTools.Method(typeof(AiAirportRotation), nameof(TrySpawn));
        private static ConditionalWeakTable<FactionHQ, FactionState> states = new ConditionalWeakTable<FactionHQ, FactionState>();
        private static object missionRunner;
        private static bool patchWarningLogged;
        private static bool planningWarningLogged;

        private sealed class FactionState
        {
            internal readonly AiAirportRotationPolicy<Airbase> Usage = new AiAirportRotationPolicy<Airbase>(ReferenceComparer<Airbase>.Instance);
            internal readonly HashSet<AircraftDefinition> AttemptedAircraft = new HashSet<AircraftDefinition>(ReferenceComparer<AircraftDefinition>.Instance);
            internal bool PlanningFailed;
        }

        internal static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind("AiAirportRotation", "Enabled", true,
                "Distribute AI departures between the closest compatible friendly airbases. Player spawns are unchanged.");
            PreferredAirbases = config.Bind("AiAirportRotation", "PreferredAirbases", 3,
                new ConfigDescription("Number of compatible airbases in the preferred rotation (1-8). Busy hangars do not change membership. Other compatible bases are fallback only.",
                    new AcceptableValueRange<int>(1, 8)));
            LogSelections = config.Bind("AiAirportRotation", "LogSelections", false,
                "Log each accepted AI departure, its airbase, and whether it used fallback. Enable for rollout verification.");
        }

        // The native outer loop can revisit the spawn call for several bases of the same
        // aircraft type. One wrapper invocation exhausts that type's ordered candidates;
        // this pass-scoped guard prevents repeating those failed attempts.
        internal static void BeginPass(FactionHQ hq)
        {
            if (!PatchActive || Enabled == null || !Enabled.Value || hq == null) return;
            try
            {
                object currentRunner = MissionManager.Runner;
                if (!ReferenceEquals(missionRunner, currentRunner))
                {
                    states = new ConditionalWeakTable<FactionHQ, FactionState>();
                    missionRunner = currentRunner;
                }
                FactionState state = states.GetValue(hq, _ => new FactionState());
                state.AttemptedAircraft.Clear();
                state.PlanningFailed = false;
                state.Usage.Prune(airbase => airbase != null && ReferenceEquals(airbase.CurrentHQ, hq));
            }
            catch (Exception ex)
            {
                // An unavailable mission/reference must not prevent vanilla deployment.
                states.Remove(hq);
                WarnPlanning(ex);
            }
        }

        internal static Airbase.TrySpawnResult TrySpawn(Airbase originalAirbase, Player player,
            AircraftDefinition definition, LiveryKey livery, Loadout loadout, float fuelLevel, FactionHQ hq)
        {
            NukeStatsPlugin.Trace("AiAirportRotation.TrySpawn");
            if (!PatchActive || Enabled == null || !Enabled.Value || player != null || hq == null ||
                !states.TryGetValue(hq, out FactionState state) || state.PlanningFailed)
                return originalAirbase.TrySpawnAircraft(player, definition, livery, loadout, fuelLevel);

            if (state.AttemptedAircraft.Contains(definition)) return default;

            List<Airbase> ordered;
            HashSet<Airbase> preferred;
            try
            {
                var ranked = (List<(Airbase airbase, float distance)>)SortedAirbases.GetValue(hq);
                if (ranked == null) throw new InvalidOperationException("FactionHQ.airbasesSorted was null.");

                var compatible = new List<Airbase>();
                var seen = new HashSet<Airbase>(ReferenceComparer<Airbase>.Instance);
                foreach (var entry in ranked)
                {
                    Airbase airbase = entry.airbase;
                    if (airbase != null && seen.Add(airbase) && IsCompatible(airbase, hq, definition))
                        compatible.Add(airbase);
                }

                int count = Math.Min(Math.Max(1, Math.Min(8, PreferredAirbases.Value)), compatible.Count);
                preferred = new HashSet<Airbase>(compatible.Take(count), ReferenceComparer<Airbase>.Instance);
                ordered = state.Usage.Order(compatible, count);
                // The pool above deliberately includes busy compatible bases. Readiness
                // removes attempts, never promotes a distant base into that pool.
                ordered.RemoveAll(airbase => !airbase.CanSpawnAircraft(definition));
                state.AttemptedAircraft.Add(definition);
            }
            catch (Exception ex)
            {
                // No spawn has been attempted yet, so this call can safely stay native.
                state.PlanningFailed = true;
                WarnPlanning(ex);
                return originalAirbase.TrySpawnAircraft(player, definition, livery, loadout, fuelLevel);
            }

            foreach (Airbase airbase in ordered)
            {
                if (airbase == null || !ReferenceEquals(airbase.CurrentHQ, hq) || airbase.disabled ||
                    !airbase.CanSpawnAircraft(definition)) continue;

                // Do not catch this call and retry. A native/plugin exception can occur
                // after an aircraft has spawned or a carrier launch has been queued.
                Airbase.TrySpawnResult result = airbase.TrySpawnAircraft(player, definition, livery, loadout, fuelLevel);
                if (!result.Allowed) continue;

                // Allowed includes delayed carrier launches: acceptance consumes supply
                // now and must advance rotation once, before the aircraft appears.
                try
                {
                    state.Usage.Accepted(airbase);
                    NukeStatsPlugin.Trace("AiAirportRotation.Accepted");
                    if (LogSelections != null && LogSelections.Value)
                        NukeStatsPlugin.Log?.LogInfo("[AI airports] accepted faction=" + hq.name +
                            " aircraft=" + definition.name + " airbase=" + airbase.name +
                            " selection=" + (preferred.Contains(airbase) ? "preferred" : "fallback"));
                }
                catch (Exception ex) { WarnPlanning(ex); }
                return result;
            }
            return default;
        }

        private static bool IsCompatible(Airbase airbase, FactionHQ hq, AircraftDefinition definition)
        {
            if (!ReferenceEquals(airbase.CurrentHQ, hq) || airbase.disabled || airbase.hangars == null) return false;
            foreach (Hangar hangar in airbase.hangars)
            {
                if (hangar == null || hangar.Disabled) continue;
                AircraftDefinition[] supported = hangar.GetAvailableAircraft();
                if (supported == null) continue;
                foreach (AircraftDefinition aircraft in supported)
                    if (ReferenceEquals(aircraft, definition)) return true;
            }
            return false;
        }

        internal static IEnumerable<CodeInstruction> ApplyTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = instructions.ToList();
            PatchActive = false;
            bool signatureMatches = original is MethodInfo method && method.DeclaringType == typeof(FactionHQ) &&
                method.Name == "DeployAIAircraft" && !method.IsStatic && method.ReturnType == typeof(void) &&
                method.GetParameters().Length == 0 && NativeSpawn != null && !NativeSpawn.IsStatic &&
                NativeSpawn.ReturnType == typeof(Airbase.TrySpawnResult) && WrapperSpawn != null &&
                SortedAirbases != null && SortedAirbases.FieldType == typeof(List<(Airbase, float)>);
            var matches = new List<int>();
            if (signatureMatches)
                for (int i = 0; i < code.Count; i++)
                    if ((code[i].opcode == OpCodes.Call || code[i].opcode == OpCodes.Callvirt) &&
                        Equals(code[i].operand, NativeSpawn)) matches.Add(i);

            if (matches.Count != 1 || code[matches[0]].blocks.Count != 0)
            {
                if (!patchWarningLogged)
                {
                    patchWarningLogged = true;
                    try { NukeStatsPlugin.Log?.LogWarning("[AI airports] Rotation disabled: deployment method/signature/callsite did not match the verified game version. Vanilla deployment retained."); } catch { }
                }
                return code;
            }

            int index = matches[0];
            var output = new List<CodeInstruction>(code.Count + 1);
            for (int i = 0; i < code.Count; i++)
            {
                if (i != index) { output.Add(code[i]); continue; }
                // The original receiver and five args are already on the stack. Append
                // this HQ, then call our static wrapper. Move incoming labels onto the
                // extra load; no original instruction or shared airport list is mutated.
                // Copy through Harmony to preserve labels without exposing its legacy
                // mscorlib Label type to this netstandard plugin's compiler.
                var loadHq = new CodeInstruction(code[i]) { opcode = OpCodes.Ldarg_0, operand = null };
                output.Add(loadHq);
                output.Add(new CodeInstruction(OpCodes.Call, WrapperSpawn));
            }
            PatchActive = true;
            try { NukeStatsPlugin.Log?.LogInfo("[AI airports] Deployment rotation callsite verified and patched."); } catch { }
            return output;
        }

        private static void WarnPlanning(Exception ex)
        {
            if (planningWarningLogged) return;
            planningWarningLogged = true;
            try { NukeStatsPlugin.Log?.LogWarning("[AI airports] Rotation bookkeeping/planning unavailable; no spawn is retried after an exception: " + ex.GetType().Name + ": " + ex.Message); } catch { }
        }

        [HarmonyPatch(typeof(FactionHQ), "DeployAIAircraft")]
        private static class DeploymentPatch
        {
            private static void Prefix(FactionHQ __instance)
            {
                NukeStatsPlugin.Trace("AiAirportRotation.DeploymentPrefix");
                BeginPass(__instance);
            }
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
            {
                NukeStatsPlugin.Trace("AiAirportRotation.DeploymentTranspiler");
                return ApplyTranspiler(instructions, __originalMethod);
            }
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }

    // Shared by runtime and deterministic tests. Input is the distinct compatible bases
    // in native objective-distance order, including bases that are temporarily busy.
    // History belongs to one faction, across aircraft types; only acceptance updates it.
    internal sealed class AiAirportRotationPolicy<T> where T : class
    {
        private readonly Dictionary<T, long> lastAccepted;
        private long sequence;

        internal AiAirportRotationPolicy(IEqualityComparer<T> comparer = null)
        {
            lastAccepted = new Dictionary<T, long>(comparer ?? EqualityComparer<T>.Default);
        }

        internal List<T> Order(IReadOnlyList<T> compatibleInRankOrder, int preferredCount)
        {
            int count = Math.Max(0, Math.Min(preferredCount, compatibleInRankOrder.Count));
            var indexes = new List<int>(count);
            for (int i = 0; i < count; i++) indexes.Add(i);
            indexes.Sort((a, b) =>
            {
                lastAccepted.TryGetValue(compatibleInRankOrder[a], out long aUse);
                lastAccepted.TryGetValue(compatibleInRankOrder[b], out long bUse);
                int comparison = aUse.CompareTo(bUse);
                return comparison != 0 ? comparison : a.CompareTo(b);
            });
            var result = new List<T>(compatibleInRankOrder.Count);
            foreach (int index in indexes) result.Add(compatibleInRankOrder[index]);
            for (int i = count; i < compatibleInRankOrder.Count; i++) result.Add(compatibleInRankOrder[i]);
            return result;
        }

        internal void Accepted(T airbase) => lastAccepted[airbase] = ++sequence;

        internal void Prune(Func<T, bool> keep)
        {
            var remove = new List<T>();
            foreach (T airbase in lastAccepted.Keys)
                if (!keep(airbase)) remove.Add(airbase);
            foreach (T airbase in remove) lastAccepted.Remove(airbase);
        }

        internal void Reset()
        {
            lastAccepted.Clear();
            sequence = 0;
        }
    }
}
