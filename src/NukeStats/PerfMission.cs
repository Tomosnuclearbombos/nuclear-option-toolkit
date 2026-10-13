using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace NukeStats
{
    // 1.9.2 (owner 2026-10-13: "build it all as part of the update"): WHICH part of a mission made a hitch.
    //
    // The 0.76 s frame on S2 10-12 was all inside MissionRunner.Update, the game's objective runner. Every frame it asks
    // each running objective whether it is done (UpdateAndCheck); a finished objective runs its outcomes (Outcome.Complete:
    // SpawnUnitOutcome spawns every unit of the outcome in that one frame) and those start the next objectives (OnStart).
    // These timers put a name on it: each objective's check, start and completion (MissionRunner.CompleteObjective, which
    // includes its outcomes) and each outcome, with the unit count for a spawn. A hitch line gets that frame's biggest ones
    // ("objectives"), the minute perf line the minute's top five. Installed and removed with Perf.Timing; never throws into
    // the game (a name lookup that fails just leaves the name out).
    //
    // 2026-10-13 CRASH FIX: the first build hooked each objective type's UpdateAndCheck/OnStart where the type inherits
    // them, and for DestroyUnits, CaptureAirbase and ReachWaypoints that is the GENERIC base CompleteOrderObjective<T>.
    // Mono runs one compiled copy of such a method for every T that is a class, and a Harmony hook on one T replaces that
    // copy for all of them: OnStart then built the Waypoint list for a DestroyUnits objective and Mono aborted (S2, 13
    // crashes, every player disconnected each time). Nothing declared on a generic type is ever hooked now
    // (PatchSafety.IsShared, also enforced by PatchSafety.Audit): starts are timed through MissionRunner.StartObjective, and
    // those three types' checks through their own CheckComplete and TryGetPosition, which the concrete types declare.
    internal static class PerfMission
    {
        const byte KCheck = 0, KStart = 1, KComplete = 2, KOutcome = 3;
        static readonly string[] KindNames = { "check", "start", "complete", "outcome" };
        const double FrameMinMs = 0.5;           // a frame keeps the mission work over this
        const int FrameMax = 64;

        static bool _on;
        static FieldInfo _fUnitsToSpawn;         // SpawnUnitOutcome.UnitsToSpawn (the class is internal to the game)
        static readonly double _msPerTick = 1000.0 / Stopwatch.Frequency;

        struct Item { public byte kind; public object who; public double ms; }
        static readonly List<Item> _frame = new List<Item>(16);

        sealed class Agg { public byte kind; public object who; public long ticks, max; public int calls; }

        sealed class SameObject : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        static readonly Dictionary<object, Agg>[] _minute =
        {
            new Dictionary<object, Agg>(new SameObject()), new Dictionary<object, Agg>(new SameObject()),
            new Dictionary<object, Agg>(new SameObject()), new Dictionary<object, Agg>(new SameObject()),
        };

        // from Perf.Install: patches go on Perf's own Harmony and into its list, so Perf.Uninstall takes them off again
        internal static void Install(Harmony h, List<MethodBase> patched, List<string> missing)
        {
            try
            {
                var objT = AccessTools.TypeByName("NuclearOption.SavedMission.Objective");
                var outT = AccessTools.TypeByName("NuclearOption.SavedMission.Outcome");
                var runT = AccessTools.TypeByName("MissionRunner");
                if (objT == null || outT == null || runT == null) { missing.Add("mission objective types"); return; }
                var pre = new HarmonyMethod(typeof(PerfMission).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
                var done = new HashSet<MethodBase>();
                int n = 0;
                foreach (var t in AccessTools.GetTypesFromAssembly(objT.Assembly))
                {
                    if (t == null || t.IsAbstract || t.IsInterface || t.ContainsGenericParameters) continue;
                    if (objT.IsAssignableFrom(t))
                    {
                        // the check a concrete objective actually runs (it may live on a base class: patch it once), unless
                        // that base is generic: then the type's own per-target work, declared on the type itself
                        var check = AccessTools.Method(t, "UpdateAndCheck", Type.EmptyTypes);
                        if (check != null && !PatchSafety.IsShared(check))
                            n += Patch(h, check, pre, nameof(CheckPost), done, patched, missing);
                        else
                        {
                            n += Patch(h, AccessTools.DeclaredMethod(t, "CheckComplete"), pre, nameof(CheckPost), done, patched, missing);
                            n += Patch(h, AccessTools.DeclaredMethod(t, "TryGetPosition"), pre, nameof(CheckPost), done, patched, missing);
                        }
                    }
                    else if (outT.IsAssignableFrom(t))
                    {
                        n += Patch(h, AccessTools.Method(t, "Complete", new[] { objT }), pre, nameof(OutcomePost), done, patched, missing);
                        if (t.Name == "SpawnUnitOutcome") _fUnitsToSpawn = AccessTools.Field(t, "UnitsToSpawn");
                    }
                }
                n += Patch(h, AccessTools.DeclaredMethod(runT, "CompleteObjective", new[] { objT }), pre, nameof(CompletePost), done, patched, missing);
                // every objective start (it runs the objective's OnStart), on the runner instead of each OnStart
                n += Patch(h, AccessTools.DeclaredMethod(runT, "StartObjective", new[] { objT, typeof(bool) }), pre, nameof(StartPost), done, patched, missing);
                Clear();
                _on = n > 0;
                NukeStatsPlugin.Log?.LogInfo($"[perf] mission timers on: {n} objective/outcome method(s)");
            }
            catch (Exception e) { missing.Add("mission timers (" + e.Message + ")"); }
        }

        static int Patch(Harmony h, MethodInfo m, HarmonyMethod pre, string post, HashSet<MethodBase> done, List<MethodBase> patched, List<string> missing)
        {
            if (m == null || m.IsAbstract || !done.Add(m)) return 0;
            if (PatchSafety.IsShared(m)) { missing.Add(m.DeclaringType?.Name + "." + m.Name + " (generic, never hooked)"); return 0; }
            try
            {
                h.Patch(m, prefix: pre, postfix: new HarmonyMethod(typeof(PerfMission).GetMethod(post, BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
                patched.Add(m);
                return 1;
            }
            catch (Exception e) { missing.Add(m.DeclaringType?.Name + "." + m.Name + " (" + e.Message + ")"); return 0; }
        }

        // from Perf.Uninstall
        internal static void Off() { _on = false; Clear(); }

        static void Clear()
        {
            _frame.Clear();
            foreach (var d in _minute) d.Clear();
        }

        static void Pre(out long __state) { __state = Stopwatch.GetTimestamp(); }
        static void CheckPost(long __state, object __instance) { Note(KCheck, __instance, __state); }
        static void StartPost(long __state, object __0) { Note(KStart, __0, __state); }   // MissionRunner.StartObjective(obj, ...)
        static void OutcomePost(long __state, object __instance) { Note(KOutcome, __instance, __state); }
        static void CompletePost(long __state, object __0) { Note(KComplete, __0, __state); }

        // runs inside the game's mission code: it must never throw
        static void Note(byte kind, object who, long start)
        {
            try
            {
                if (!_on || who == null) return;
                long dt = Stopwatch.GetTimestamp() - start;
                var d = _minute[kind];
                if (!d.TryGetValue(who, out var a)) { a = new Agg { kind = kind, who = who }; d[who] = a; }
                a.ticks += dt;
                a.calls++;
                if (dt > a.max) a.max = dt;
                double ms = dt * _msPerTick;
                if (ms >= FrameMinMs && _frame.Count < FrameMax) _frame.Add(new Item { kind = kind, who = who, ms = ms });
            }
            catch { }
        }

        // from Perf.FrameBoundary, after the hitch check
        internal static void FrameReset() { if (_frame.Count > 0) _frame.Clear(); }

        // a hitch line: this frame's biggest mission work
        internal static void AppendFrame(StringBuilder sb)
        {
            if (!_on || _frame.Count == 0) return;
            try
            {
                var items = new List<Item>(_frame);
                items.Sort((x, y) => y.ms.CompareTo(x.ms));
                sb.Append(",\"objectives\":[");
                for (int k = 0; k < items.Count && k < 6; k++)
                {
                    if (k > 0) sb.Append(',');
                    Entry(sb, items[k].kind, items[k].who, items[k].ms, -1, 0);
                }
                sb.Append(']');
            }
            catch { }
        }

        // the minute perf line: the five objectives/outcomes that cost most this minute (total ms, worst single call, calls)
        internal static void Append(StringBuilder sb)
        {
            if (!_on) return;
            try
            {
                var all = new List<Agg>();
                foreach (var d in _minute) all.AddRange(d.Values);
                all.Sort((x, y) => y.ticks.CompareTo(x.ticks));
                sb.Append(",\"objectives\":[");
                int n = 0;
                foreach (var a in all)
                {
                    double ms = a.ticks * _msPerTick;
#if PRETEND_TEST
                    if (n >= 12 || ms < 1.0) break;   // test builds: a longer list
#else
                    if (n >= 5 || ms < 1.0) break;
#endif
                    if (n > 0) sb.Append(',');
                    Entry(sb, a.kind, a.who, ms, a.max * _msPerTick, a.calls);
                    n++;
                }
                sb.Append(']');
            }
            catch { }
            foreach (var d in _minute) d.Clear();
        }

        static void Entry(StringBuilder sb, byte kind, object who, double ms, double maxMs, int calls)
        {
            sb.Append("{\"k\":\"").Append(KindNames[kind]).Append('"');
            string name = null, type = null;
            int units = -1;
            try { Describe(who, out name, out type, out units); } catch { }
            if (name != null) sb.Append(",\"n\":\"").Append(Esc(name)).Append('"');
            if (type != null) sb.Append(",\"ty\":\"").Append(Esc(type)).Append('"');
            if (units >= 0) sb.Append(",\"u\":").Append(units);
            sb.Append(",\"ms\":").Append(ms.ToString("0.##", CultureInfo.InvariantCulture));
            if (maxMs >= 0) sb.Append(",\"max\":").Append(maxMs.ToString("0.##", CultureInfo.InvariantCulture)).Append(",\"calls\":").Append(calls);
            sb.Append('}');
        }

        // separate from the timers on purpose: if a game member here ever changes, only the name goes missing
        static void Describe(object who, out string name, out string type, out int units)
        {
            name = null; type = null; units = -1;
            if (who is NuclearOption.SavedMission.Objective o)
            {
                name = o.SavedObjective?.UniqueName;
                type = o.SavedObjective?.ObjectiveTypeEnum.ToString();
            }
            else if (who is NuclearOption.SavedMission.Outcome oc)
            {
                name = oc.SavedOutcome?.UniqueName;
                type = oc.SavedOutcome?.OutcomeTypeEnum.ToString();
                if (_fUnitsToSpawn != null && _fUnitsToSpawn.DeclaringType != null && _fUnitsToSpawn.DeclaringType.IsInstanceOfType(who))
                    units = (_fUnitsToSpawn.GetValue(who) as ICollection)?.Count ?? -1;
            }
        }

        static string Esc(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
