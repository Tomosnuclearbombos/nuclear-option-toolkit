using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using NuclearOption.SavedMission;
using UnityEngine;

namespace NukeStats
{
    // Owner 2026-10-13 ("Yep make those fixes"): DestroyUnits objectives check every target unit every frame.
    //
    // The mission timers showed it on S2: on Escalation the "DamageHighValueTargets" objective spent about 10 ms of every
    // second just asking whether each of its targets was dead, and a few "Threaten..." ones 1-2 ms a second each. Each
    // frame the game looks every remaining target up by name and rebuilds the objective's list of target positions (the
    // HQ sorts its airbases and depots by those every few seconds; players' markers are worked out on their own machines).
    //
    // A dead unit stays dead, so the answer cannot be missed by asking less often. This runs a DestroyUnits check at most
    // every Perf.DestroyCheckSeconds while nothing is changing. The moment a check finds a target destroyed, the next frame
    // checks again, and so on while targets keep dying: the game marks only ONE target per check, so a nuke that kills 30
    // still registers them on 30 frames in a row, as before. Only DestroyUnits objectives (exact type).
    //
    // HOW (rebuilt 2026-10-13 after the S2 crashes): nothing of the game's objective code is hooked or replaced. Just
    // before MissionRunner.Update runs, the DestroyUnits objectives that are not due this frame are taken out of its
    // ActiveObjectives list; a finaliser (it runs even if the game throws) puts them back where they were straight after,
    // unless the frame stopped or completed them. The game's own loop does everything else. The first build hooked the
    // check itself, which lives on the generic CompleteOrderObjectiveWithPositions<T>: Mono runs one compiled copy of it for
    // every T, and a Harmony hook on one T replaces that copy for all of them. Fine for UpdateAndCheck by luck, but the
    // mission timers' identical hook on CompleteOrderObjective<T>.OnStart built the wrong T's list and crashed the server
    // 13 times on 10-13. Never hook a method declared on a generic type.
    internal static class ObjectiveThrottle
    {
        internal static ConfigEntry<float> Seconds;
        static Type _destroyT;
        static bool _on;
        static readonly Dictionary<object, float> _next = new Dictionary<object, float>(new SameObject());
        static readonly Dictionary<object, float> _before = new Dictionary<object, float>(new SameObject());

        // this frame: the objectives held out (with their place in the list) and the DestroyUnits ones that ran
        static readonly List<KeyValuePair<int, Objective>> _held = new List<KeyValuePair<int, Objective>>();
        static readonly List<Objective> _ran = new List<Objective>();
        static bool _failed;

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
                var m = AccessTools.DeclaredMethod(typeof(MissionRunner), "Update", Type.EmptyTypes);
                if (_destroyT == null || m == null) { NukeStatsPlugin.Log?.LogWarning("[objcheck] DestroyUnitObjective or MissionRunner.Update not found: objectives run as before"); return; }
                var h = new Harmony(NukeStatsPlugin.Guid + ".objcheck");
                h.Patch(m,
                    prefix: new HarmonyMethod(typeof(ObjectiveThrottle).GetMethod(nameof(Hold), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)) { priority = Priority.Low },
                    finalizer: new HarmonyMethod(typeof(ObjectiveThrottle).GetMethod(nameof(Restore), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
                _on = true;
                NukeStatsPlugin.Log?.LogInfo($"[objcheck] destroy-units objectives checked every {Seconds.Value:0.##} s while idle (0 = every frame); MissionRunner.Update list hold");
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogWarning("[objcheck] not applied, objectives run as before: " + e.Message); }
        }

        // before the game's loop: take out the DestroyUnits objectives that are not due; note the progress of the ones that run
        static void Hold(MissionRunner __instance)
        {
            _held.Clear();
            _ran.Clear();
            try
            {
                float s = Seconds != null ? Seconds.Value : 0f;
                if (!_on || _failed || s <= 0f || __instance == null) return;
                var list = __instance.ActiveObjectives;
                float now = Time.realtimeSinceStartup;
                for (int i = 0; i < list.Count; i++)
                {
                    var o = list[i];
                    if (o == null || o.GetType() != _destroyT) continue;
                    if (_next.TryGetValue(o, out float t) && now < t) _held.Add(new KeyValuePair<int, Objective>(i, o));
                    else { _ran.Add(o); _before[o] = o.CompletePercent; }
                }
                for (int k = _held.Count - 1; k >= 0; k--) list.RemoveAt(_held[k].Key);   // from the back: indices stay valid
            }
            catch (Exception e) { Fail(e); }
        }

        // after the game's loop (always, even if it threw): put the held ones back where they were, unless this frame
        // stopped or completed them; schedule each DestroyUnits objective that ran
        static Exception Restore(MissionRunner __instance, Exception __exception)
        {
            try
            {
                if (__instance != null && _held.Count > 0)
                {
                    var list = __instance.ActiveObjectives;
                    foreach (var kv in _held)
                    {
                        var o = kv.Value;
                        if (o.Status != ObjectiveStatus.Running || list.Contains(o)) { _next.Remove(o); continue; }
                        list.Insert(Math.Min(kv.Key, list.Count), o);
                    }
                }
                float now = Time.realtimeSinceStartup;
                float s = Seconds != null ? Seconds.Value : 0f;
                foreach (var o in _ran)
                {
                    if (o.Status != ObjectiveStatus.Running) { _next.Remove(o); _before.Remove(o); continue; }
                    float before = _before.TryGetValue(o, out float b) ? b : -1f;
                    // a target died in this check: look again next frame (the game marks one per check), else wait
                    _next[o] = o.CompletePercent != before ? now : now + s;
                }
                if (_next.Count > 512) { _next.Clear(); _before.Clear(); }   // objectives from old missions
            }
            catch (Exception e) { Fail(e); }
            finally { _held.Clear(); _ran.Clear(); }
            return __exception;          // the game's own exception, if any, untouched
        }

        static void Fail(Exception e)
        {
            if (_failed) return;
            _failed = true;               // from now on the game's own behaviour (every frame)
            NukeStatsPlugin.Log?.LogError("[objcheck] error, destroy-units objectives back to every frame: " + e);
        }
    }
}
