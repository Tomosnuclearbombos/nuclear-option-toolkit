using System;
using System.Reflection;
using HarmonyLib;

namespace NukeStats
{
    // 2026-10-13 (owner: "Make sure the issue never occurs again"): NEVER hook a method declared on a generic type.
    //
    // On this Mono, a generic class's method runs as ONE compiled copy for every type argument that is a class, and a
    // Harmony hook on one instantiation replaces that copy for all of them, specialised for the type it was built for. The
    // 1.9.2/1.9.2 mission timers hooked CompleteOrderObjective<T>.OnStart for three T; the last hook built the Waypoint
    // version for every objective, a DestroyUnits objective starting mid-mission built the wrong list and Mono aborted.
    // S2 crashed 13 times on 10-13 and every player was disconnected each time.
    //
    // Removing such a hook does NOT undo it: Harmony rebuilds an empty wrapper for each instantiation and the shared copy
    // stays replaced (proven on the test server 10-13: hook removed, server still crashed; S2 crashed again at 16:33 after
    // timing was switched off). So the guard has to stop the hook before it goes in:
    //   InstallGuard (first thing in Awake): a prefix on Harmony.Patch itself refuses any hook of ours (an instance id
    //     starting with our GUID) on a generic type's method, logs an error, and patches nothing. Every manual hook in this
    //     plugin goes through Harmony.Patch.
    //   IsShared: the same test, used by the code that picks its hooks (PerfMission, ObjectiveThrottle).
    //   Audit (at start and whenever timing installs): reads every method Harmony has patched and reports any hook on a
    //     generic type's method (ours, e.g. an attribute patch, or another mod's) as an error that needs a restart.
    internal static class PatchSafety
    {
        static bool _guardOn;

        internal static void InstallGuard()
        {
            try
            {
                var pre = new HarmonyMethod(typeof(PatchSafety).GetMethod(nameof(GuardPre), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
                var h = new Harmony(NukeStatsPlugin.Guid + ".patchsafety");
                int n = 0;
                foreach (var target in typeof(Harmony).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    var ps = target.GetParameters();
                    if (target.Name != nameof(Harmony.Patch) || ps.Length == 0 || ps[0].ParameterType != typeof(MethodBase) || ps[0].Name != "original") continue;
                    h.Patch(target, prefix: pre);   // these targets are not generic, so the guard lets them through
                    n++;
                }
                if (n == 0) { NukeStatsPlugin.Log?.LogWarning("[patchsafety] Harmony.Patch not found: no hook guard"); return; }
                _guardOn = true;
                NukeStatsPlugin.Log?.LogInfo($"[patchsafety] hook guard on ({n} Harmony.Patch overloads): no hook of ours can go on a generic type's method");
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[patchsafety] hook guard could not start: " + e.Message); }
        }

        // runs in front of every Harmony.Patch call; false = patch nothing
        static bool GuardPre(Harmony __instance, MethodBase original, ref MethodInfo __result)
        {
            try
            {
                if (!_guardOn || __instance == null || original == null) return true;
                if (__instance.Id == null || !__instance.Id.StartsWith(NukeStatsPlugin.Guid, StringComparison.Ordinal)) return true;
                if (!IsShared(original)) return true;
                NukeStatsPlugin.Log?.LogError($"[patchsafety] REFUSED a hook ({__instance.Id}) on {Name(original)}: a generic type's method is shared by every type and hooking it can crash the server");
                __result = null;
                return false;
            }
            catch { return true; }
        }
        internal static bool IsShared(MethodBase m)
        {
            if (m == null) return false;
            if (m.IsGenericMethod || m.ContainsGenericParameters) return true;
            for (var t = m.DeclaringType; t != null; t = t.DeclaringType)   // a type nested in a generic type is generic too
            {
                if (t.IsGenericType || t.ContainsGenericParameters) return true;
            }
            return false;
        }

        static bool _startDone;

        // once, a few seconds after start (every Awake hook is in by then)
        internal static void AuditOnce()
        {
            if (_startDone) return;
            _startDone = true;
            Audit("start");
        }

        internal static int Audit(string when)
        {
            int removed = 0, checkedN = 0;
            try
            {
                foreach (var m in Harmony.GetAllPatchedMethods())
                {
                    checkedN++;
                    if (!IsShared(m)) continue;
                    var info = Harmony.GetPatchInfo(m);
                    if (info == null) continue;
                    foreach (var owner in info.Owners)
                    {
                        if (owner == null || !owner.StartsWith(NukeStatsPlugin.Guid, StringComparison.Ordinal))
                        {
                            NukeStatsPlugin.Log?.LogWarning($"[patchsafety] another mod ({owner}) hooks {Name(m)}, a generic type's method: that can crash the server");
                            continue;
                        }
                        new Harmony(owner).Unpatch(m, HarmonyPatchType.All, owner);
                        removed++;
                        NukeStatsPlugin.Log?.LogError($"[patchsafety] UNSAFE hook of ours ({owner}) on {Name(m)}, a generic type's method: removed, but the shared copy stays replaced until the game restarts - RESTART THE SERVER when it is empty");
                    }
                }
                NukeStatsPlugin.Log?.LogInfo($"[patchsafety] {when}: {checkedN} hooked method(s) checked, {removed} unsafe hook(s) removed");
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[patchsafety] audit failed: " + e.Message); }
            return removed;
        }

        static string Name(MethodBase m) => (m.DeclaringType != null ? m.DeclaringType.FullName : "?") + "." + m.Name;
    }
}
