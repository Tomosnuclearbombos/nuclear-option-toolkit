using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;

namespace NukeStats
{
    // 2026-10-19 (owner: "yeah just make the list far larger within reason of course"). The game's own limit, found when a
    // 250 kt GPO-N on Rising Tsunami "did no damage": AA vehicles 95 m from ground zero survived while hangars 600 m away died.
    //
    // Shockwave.Start collects everything the blast can touch ONCE, with Physics.OverlapSphereNonAlloc(position,
    // blastRadius * 2, colliderBuffer) and no layer mask. colliderBuffer is a private static Collider[4096] shared by every
    // shockwave. blastRadius = yield^(1/3) * 13, so a 250 kt warhead searches a 16.4 km sphere; on Ignus or Heartland the
    // terrain and scenery colliders alone fill 4096 slots. The overlap result is cut off in no particular order (not by
    // distance), and only colliders in the list ever get TakeDamage or a push, so random units anywhere in range take nothing.
    // A 20 kt warhead (7 km sphere) stays under the limit, which is why the smaller nukes behave.
    //
    // Fix: at plugin load the shared buffer is swapped for a larger one (NukeBlast.ColliderBuffer, default 8192 = double the
    // game's, owner 2026-10-20; 1.9.12 shipped with 65536). The field is
    // a plain static, not readonly (live Assembly-CSharp checked 10-19), so the game's own code reads the new array from the
    // next shockwave on. Nothing else changes: same overlap, same damage code, no per-frame hook. A prefix/postfix pair on
    // Shockwave.Start (a plain instance method, not generic) clears the buffer first, then logs one line per nuclear-size
    // shockwave on the server: yield, place, colliders found, objects kept, time taken, and SATURATED if the list was full again.
    internal static class NukeBlastFix
    {
        internal static ConfigEntry<int> BufferSize;
        static FieldInfo _buffer, _influenced, _yield;
        static readonly Stopwatch _sw = new Stopwatch();

        internal static void Initialize(ConfigFile config)
        {
            BufferSize = config.Bind("NukeBlast", "ColliderBuffer", 8192,   // owner 2026-10-20: "Make it double what it was originally"
                new ConfigDescription("How many objects one nuclear blast can reach. The game's own limit is 4096, which a big nuke "
                    + "fills with terrain and scenery, so random units inside the blast take no damage at all. Larger = every unit "
                    + "in range is hit, at the cost of a slightly longer one-off pause when a big nuke goes off.",
                    new AcceptableValueRange<int>(4096, 262144)));
            try
            {
                _buffer = AccessTools.Field(typeof(Shockwave), "colliderBuffer");
                _influenced = AccessTools.Field(typeof(Shockwave), "influencedObjects");
                _yield = AccessTools.Field(typeof(Shockwave), "yieldKilotons");
                // Collider lives in UnityEngine.PhysicsModule, which this project does not reference; the array is handled as
                // object[] (array covariance) and the new one is made from the field's own element type
                if (_buffer == null || !_buffer.IsStatic || _buffer.IsInitOnly || !_buffer.FieldType.IsArray
                    || _buffer.FieldType.GetElementType()?.Name != "Collider")
                {
                    NukeStatsPlugin.Log?.LogError("[nuke] Shockwave.colliderBuffer is missing or not a plain static Collider[] in this game build: blast list left as the game has it");
                    _buffer = null;
                    return;
                }
                int want = Mathf.Clamp(BufferSize.Value, 4096, 262144);
                var cur = _buffer.GetValue(null) as object[];
                int was = cur != null ? cur.Length : 0;
                if (was < want) _buffer.SetValue(null, Array.CreateInstance(_buffer.FieldType.GetElementType(), want));
                NukeStatsPlugin.Log?.LogInfo($"[nuke] blast target list {was} -> {Math.Max(was, want)} (NukeBlast.ColliderBuffer)");
            }
            catch (Exception e)
            {
                NukeStatsPlugin.Log?.LogError("[nuke] could not enlarge the blast target list: " + e.Message);
                _buffer = null;
            }
        }

        [HarmonyPatch]
        internal static class StartPatch
        {
            static MethodBase TargetMethod()
            {
                MethodInfo m = AccessTools.DeclaredMethod(typeof(Shockwave), "Start");
                if (m != null && PatchSafety.IsShared(m))
                {
                    NukeStatsPlugin.Log?.LogError("[nuke] Shockwave.Start is a generic type's method in this game build: not hooked");
                    return null;
                }
                return m;
            }

            // the game's overlap writes from slot 0 up; clearing first makes the count of filled slots exact
            static void Prefix()
            {
                if (_buffer == null) return;
                try
                {
                    if (_buffer.GetValue(null) is object[] b) Array.Clear(b, 0, b.Length);
                    _sw.Restart();
                }
                catch { }
            }

            static void Postfix(Shockwave __instance)
            {
                if (_buffer == null) return;
                try
                {
                    _sw.Stop();
                    var nm = NetworkManagerNuclearOption.i;
                    if (nm == null || !nm.Server.Active) return;
                    float kt = _yield != null ? (float)_yield.GetValue(__instance) : 0f;
                    if (kt < 1f) return;   // conventional-size shockwaves: no line
                    var b = _buffer.GetValue(null) as object[];
                    int found = 0;
                    if (b != null) while (found < b.Length && !ReferenceEquals(b[found], null)) found++;
                    int kept = (_influenced?.GetValue(__instance) as ICollection)?.Count ?? -1;
                    var gp = __instance.transform.GlobalPosition();
                    bool full = b != null && found >= b.Length;
                    NukeStatsPlugin.Log?.LogInfo($"[nuke] {kt:0.##} kt blast at x {(int)gp.x} z {(int)gp.z} alt {(int)gp.y} m: {found} colliders found, "
                        + $"{kept} objects kept, {_sw.Elapsed.TotalMilliseconds:0.0} ms"
                        + (full ? $" - SATURATED at {b.Length}: some objects in range take no damage, raise NukeBlast.ColliderBuffer" : ""));
                }
                catch { }
            }
        }
    }
}
