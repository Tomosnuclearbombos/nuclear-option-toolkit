using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NukeStats
{
    // 2026-10-15 (owner: fix the game's NL-98 bug). The game's own bug, in every build of ours and in the plain game:
    //
    // Missile.Motor.Burnout(forceStopEffects) calls Stop() on every particle system of the motor and StopTrail() on every
    // trail emitter, with no null check. The NL-98 (ARH1) sustainer smoke trail has stop action Destroy and its
    // TrailEmitter has no rb in the prefab, so the trail never holds a particle and removes its own object (emitter
    // included) the moment Burnout(false) stops it at 10.0 s. The missile keeps the burned-out motor as its current motor
    // for two more physics steps, and the ARH seeker's miss check opens right then. A missile that goes off in that window
    // runs Missile.Detonate -> RpcDetonate -> Motor.Destruct -> Burnout(true), which throws on the removed trail
    // (Burnout+0x22 ParticleSystem.Stop, or Burnout+0x41 TrailEmitter.StopTrail). UserCode_RpcDetonate stops at its first
    // line: no warhead detonation, no RpcDetonate to the clients, never destroyed, so the missile stays on the server and
    // on every client until the mission ends. 2 to 12 per 10 minutes of Rising Tsunami.
    //
    // Fix: a prefix on Missile.Motor.Burnout drops removed entries (Unity's null) from that motor's own particle-system
    // and trail-emitter arrays, then the game's code runs unchanged on what is left, so the detonation goes through.
    // The arrays belong to the missile instance (Motor is a [Serializable] class in the missile's serialized motors
    // array, so Instantiate gives each missile its own Motor objects and arrays); the field is pointed at a new array
    // anyway, never written into, so a shared array would stay untouched. A motor whose systems are all alive costs one
    // alive check per entry and nothing else: same array, no allocation.
    //
    // Hook safety (PatchSafety, never a generic type's method): Missile is a plain class, Motor a plain nested class and
    // Burnout(bool) a plain instance method (live Assembly-CSharp df5bed594dd8). TargetMethod refuses it anyway if that
    // ever changes, so the class is skipped instead of hooked.
    internal static class MissileBurnoutFix
    {
        internal static ConfigEntry<bool> Enabled;
        static int _psDropped, _trailsDropped;   // this perf window
        static bool _logged;

        internal static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind("MissileBurnoutFix", "Enabled", true,
                "Fix the game's own missile bug (NL-98 and the like): when a missile goes off right after its motor burns out "
                + "and a smoke trail of that motor has already removed itself, the game's code fails before the explosion, so "
                + "the missile never explodes and stays on the server and every player's game until the mission ends. On: "
                + "removed trails are left out and the missile explodes normally. Off: the game's own behaviour.");
        }

        [HarmonyPatch]
        internal static class BurnoutPatch
        {
            static MethodBase TargetMethod()
            {
                Type motor = AccessTools.Inner(typeof(Missile), "Motor");
                MethodInfo m = motor != null ? AccessTools.DeclaredMethod(motor, "Burnout", new[] { typeof(bool) }) : null;
                if (m != null && PatchSafety.IsShared(m))
                {
                    NukeStatsPlugin.Log?.LogError("[missile] Missile.Motor.Burnout is a generic type's method in this game build: not hooked");
                    return null;   // Harmony skips the class ("[patch] BurnoutPatch SKIPPED")
                }
                return m;
            }

            // runs before the game's Burnout; the original always runs
            static void Prefix(ref ParticleSystem[] ___particleSystems, ref TrailEmitter[] ___trailEmitters)
            {
                if (Enabled == null || !Enabled.Value || SelfTest.StockStep) return;
                try
                {
                    int ps = Removed(___particleSystems), te = Removed(___trailEmitters);
                    if (ps == 0 && te == 0) return;   // the common case: nothing removed, nothing changed
                    if (ps > 0) { ___particleSystems = Alive(___particleSystems, ps); _psDropped += ps; }
                    if (te > 0) { ___trailEmitters = Alive(___trailEmitters, te); _trailsDropped += te; }
                    if (!_logged)
                    {
                        _logged = true;
                        NukeStatsPlugin.Log?.LogInfo($"[missile] Motor.Burnout: {ps} removed particle system(s) and {te} removed trail emitter(s) "
                            + "left out (the game's NL-98 trail bug), the missile explodes normally (logged once; perf line counts the rest)");
                    }
                }
                catch { }   // could not tell: the game's own code runs as it would without us
            }
        }

        // Unity's null: a destroyed object compares equal to null (a plain C# null too)
        static int Removed(ParticleSystem[] a)
        {
            if (a == null) return 0;
            int n = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] == null) n++;
            return n;
        }
        static int Removed(TrailEmitter[] a)
        {
            if (a == null) return 0;
            int n = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] == null) n++;
            return n;
        }
        static ParticleSystem[] Alive(ParticleSystem[] a, int removed)
        {
            var r = new ParticleSystem[a.Length - removed];
            int k = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != null) r[k++] = a[i];
            return r;
        }
        static TrailEmitter[] Alive(TrailEmitter[] a, int removed)
        {
            var r = new TrailEmitter[a.Length - removed];
            int k = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != null) r[k++] = a[i];
            return r;
        }

        // the perf line (PerfExtra.Append): counts since the last line, then reset
        internal static void Window(out int systemsDropped, out int trailsDropped)
        {
            systemsDropped = _psDropped; trailsDropped = _trailsDropped;
            _psDropped = 0; _trailsDropped = 0;
        }
    }
}
