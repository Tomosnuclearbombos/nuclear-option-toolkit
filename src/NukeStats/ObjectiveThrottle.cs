using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NukeStats
{
    // Owner 2026-10-13 ("Yep make those fixes"): DestroyUnits objectives check every target unit every frame.
    //
    // The mission timers showed it on S2 overnight: on Escalation the "DamageHighValueTargets" objective spent about
    // 10 ms of every second just asking whether each of its targets was dead, and a few "Threaten..." ones 1-2 ms a second
    // each (Absolute Escalation's "Secure Island" 6-7 ms on the test server). Each frame the game looks every remaining
    // target up by name and rebuilds the objective's list of target positions (the HQ sorts its airbases and depots by
    // those every few seconds; players' markers are worked out on their own machines).
    //
    // A dead unit stays dead, so the answer cannot be missed by asking less often. This runs a DestroyUnits check at most
    // every Perf.DestroyCheckSeconds while nothing is changing. The moment a check finds a target destroyed, the next frame
    // checks again, and so on while targets keep dying: the game marks only ONE target per check, so a nuke that kills 30
    // still registers them on 30 frames in a row, as before. A finished objective completes within that time of its last
    // target dying. Only DestroyUnits objectives (exact type); everything else runs every frame as before.
    internal static class ObjectiveThrottle
    {
        internal static ConfigEntry<float> Seconds;
        static Type _destroyT;
        static readonly Dictionary<object, float> _next = new Dictionary<object, float>(new SameObject());

        sealed class SameObject : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        internal static void Initialize(ConfigFile config)
        {
            Seconds = config.Bind("Perf", "DestroyCheckSeconds", 0.25f,
                "Destroy-units objectives: check the targets at most this often (seconds) while none are dying, instead of "
                + "every frame. A check that finds a target destroyed is followed by another the next frame, so kills register "
                + "as fast as before. 0 = every frame (the game's own behaviour). Applies live.");
            try
            {
                _destroyT = AccessTools.TypeByName("NuclearOption.SavedMission.Objectives.DestroyUnitObjective");
                var objT = AccessTools.TypeByName("NuclearOption.SavedMission.Objective");
                var m = _destroyT != null ? AccessTools.Method(_destroyT, "UpdateAndCheck", Type.EmptyTypes) : null;
                if (m == null || objT == null) { NukeStatsPlugin.Log?.LogWarning("[objcheck] DestroyUnitObjective.UpdateAndCheck not found: objectives run as before"); return; }
                // The check lives on the generic base CompleteOrderObjectiveWithPositions<T>. Mono runs ONE compiled copy of it
                // for every T that is a class (DestroyUnits <SavedUnit>, CaptureAirbase <SavedAirbase>, ReachWaypoints
                // <Waypoint>), and a hook on any one of them replaces that shared copy - so the mission timers hooking the
                // others afterwards dropped this one (test server 10-13: no effect). Hook every one of them: whichever is
                // rebuilt last still carries this. The exact-type test below keeps it to DestroyUnits.
                var targets = new List<System.Reflection.MethodInfo> { m };
                var gdef = m.DeclaringType != null && m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericTypeDefinition() : null;
                if (gdef != null)
                {
                    foreach (var t in AccessTools.GetTypesFromAssembly(objT.Assembly))
                    {
                        if (t == null || t.IsAbstract || t.ContainsGenericParameters || !objT.IsAssignableFrom(t)) continue;
                        var mm = AccessTools.Method(t, "UpdateAndCheck", Type.EmptyTypes);
                        if (mm != null && !targets.Contains(mm) && mm.DeclaringType != null && mm.DeclaringType.IsGenericType
                            && mm.DeclaringType.GetGenericTypeDefinition() == gdef) targets.Add(mm);
                    }
                }
                var h = new Harmony(NukeStatsPlugin.Guid + ".objcheck");
                var pre = new HarmonyMethod(typeof(ObjectiveThrottle).GetMethod(nameof(Pre), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)) { priority = Priority.High };
                var post = new HarmonyMethod(typeof(ObjectiveThrottle).GetMethod(nameof(Post), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
                int n = 0;
                foreach (var t in targets)
                {
                    try { h.Patch(t, prefix: pre, postfix: post); n++; }
                    catch (Exception e) { NukeStatsPlugin.Log?.LogWarning("[objcheck] " + t.DeclaringType?.Name + ": " + e.Message); }
                }
                NukeStatsPlugin.Log?.LogInfo($"[objcheck] destroy-units objectives checked every {Seconds.Value:0.##} s while idle (0 = every frame); {n} shared method(s) hooked");
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogWarning("[objcheck] not applied, objectives run as before: " + e.Message); }
        }

        // the shared base method (CompleteOrderObjectiveWithPositions<T>.UpdateAndCheck) also runs other objective types:
        // only DestroyUnits is ever held back. __state = the objective's progress before a real check, NaN = not ours/held.
        static bool Pre(object __instance, ref bool __result, out float __state)
        {
            __state = float.NaN;
            try
            {
                float s = Seconds != null ? Seconds.Value : 0f;
                if (s <= 0f || __instance == null || __instance.GetType() != _destroyT) return true;
                float now = Time.realtimeSinceStartup;
                if (_next.TryGetValue(__instance, out float t) && now < t)
                {
                    __result = false;           // not complete yet: asked again soon
                    return false;
                }
                __state = Progress(__instance);
            }
            catch { __state = float.NaN; }
            return true;
        }

        static void Post(object __instance, bool __result, float __state)
        {
            if (float.IsNaN(__state)) return;
            try
            {
                if (__result) { _next.Remove(__instance); return; }
                float now = Time.realtimeSinceStartup;
                // a target died in this check: look again next frame (the game marks one per check), else wait
                _next[__instance] = Progress(__instance) != __state ? now : now + (Seconds != null ? Seconds.Value : 0f);
                if (_next.Count > 512) _next.Clear();   // objectives from old missions (a new mission builds new ones)
            }
            catch { }
        }

        // the game's own progress figure for the objective (share of targets marked destroyed)
        static float Progress(object o) => ((NuclearOption.SavedMission.Objective)o).CompletePercent;
    }
}
