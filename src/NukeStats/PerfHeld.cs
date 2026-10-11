using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace NukeStats
{
    // 1.9.6 diagnostics (owner 2026-10-08: "sure implement that"). The memory the game still holds after its full clean-up
    // at each mission change grows all day (S2 10-07: 92 MB at 18:23 -> 600 MB at 03:42) and the 100-200 ms "Update loop"
    // hitches look like garbage-collection pauses. Once a minute this adds to the perf line: the managed heap, how many
    // collections ran, and how many entries the plugin's and the Tacview recorder's own lists hold, so a night shows whose
    // data grows. Only the plugin's and the recorder's fields are read (never the game's: reading a field can run code).
    // Nothing here changes how the game plays.
    internal static class PerfHeld
    {
        sealed class Src { public string Name; public FieldInfo Field; public bool Static; public Func<object> Owner; }

        static List<Src> _srcs;
        static int _gcAtLast = -1;

        static bool IsCountable(Type t) =>
            typeof(ICollection).IsAssignableFrom(t) || t.IsArray
            || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HashSet<>));

        static int CountOf(object v)
        {
            if (v == null) return 0;
            if (v is ICollection c) return c.Count;
            var p = v.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);   // HashSet<T>
            if (p != null && p.PropertyType == typeof(int)) return (int)p.GetValue(v, null);
            return 0;
        }

        static void AddFields(Type t, string prefix, bool statics, Func<object> owner)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly
                        | (statics ? BindingFlags.Static : BindingFlags.Instance);
            FieldInfo[] fields;
            try { fields = t.GetFields(flags); } catch { return; }
            foreach (var f in fields)
            {
                if (f.IsLiteral || !IsCountable(f.FieldType)) continue;
                _srcs.Add(new Src { Name = prefix + f.Name, Field = f, Static = statics, Owner = owner });
            }
        }

        static void Build()
        {
            _srcs = new List<Src>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an = asm.GetName().Name;
                if (an != "NukeStats" && an != "NOBlackBox") continue;
                string tag = an == "NukeStats" ? "plugin." : "recorder.";
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types; }
                foreach (var t in types)
                {
                    if (t == null || t.ContainsGenericParameters) continue;
                    AddFields(t, tag + t.Name + ".", true, null);
                }
            }
            // instance fields: the plugin itself and the recorder component (reached through the recorder's own static)
            AddFields(typeof(NukeStatsPlugin), "plugin.NukeStatsPlugin#", false, () => NukeStatsPlugin.Instance);
            var recType = AccessToolsTypeByName("NOBlackBox.Recorder_mono");
            var recPlugin = AccessToolsTypeByName("NOBlackBox.Plugin");
            var recGoField = recPlugin?.GetField("recorderMono", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (recType != null && recGoField != null)
                AddFields(recType, "recorder.Recorder_mono#", false, () =>
                {
                    var go = recGoField.GetValue(null) as GameObject;
                    return go != null ? go.GetComponent(recType) : null;
                });
        }

        static Type AccessToolsTypeByName(string n)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(n, false); if (t != null) return t; } catch { }
            }
            return null;
        }

        static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        internal static double UsedMb() { try { return Profiler.GetMonoUsedSizeLong() / 1048576.0; } catch { return -1; } }
        internal static double HeapMb() { try { return Profiler.GetMonoHeapSizeLong() / 1048576.0; } catch { return -1; } }

        // appended to the minute's perf line: ,"mem":{...}
        internal static void Append(StringBuilder sb, int hitches, int hitchesWithGc, int hitchesSuppressed)
        {
            try
            {
                if (_srcs == null) Build();
                int gc = GC.CollectionCount(0);
                int gcDelta = _gcAtLast < 0 ? 0 : gc - _gcAtLast;
                _gcAtLast = gc;
                sb.Append(",\"mem\":{\"used_mb\":").Append(F(UsedMb()))
                  .Append(",\"heap_mb\":").Append(F(HeapMb()))
                  .Append(",\"gc\":").Append(gcDelta)
                  .Append(",\"gc_total\":").Append(gc)
                  .Append(",\"hitches\":").Append(hitches)
                  .Append(",\"hitches_gc\":").Append(hitchesWithGc)
                  .Append(",\"hitches_unlogged\":").Append(hitchesSuppressed);
                long plugin = 0, recorder = 0;
                var counts = new List<KeyValuePair<string, int>>();
                foreach (var s in _srcs)
                {
                    int n;
                    try
                    {
                        object owner = s.Static ? null : s.Owner?.Invoke();
                        if (!s.Static && owner == null) continue;
                        n = CountOf(s.Field.GetValue(owner));
                    }
                    catch { continue; }
                    if (n <= 0) continue;
                    if (s.Name.StartsWith("plugin.")) plugin += n; else recorder += n;
                    counts.Add(new KeyValuePair<string, int>(s.Name, n));
                }
                counts.Sort((a, b) => b.Value.CompareTo(a.Value));
                sb.Append(",\"held_plugin\":").Append(plugin).Append(",\"held_recorder\":").Append(recorder).Append(",\"held_top\":{");
                for (int i = 0; i < counts.Count && i < 12; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(counts[i].Key).Append("\":").Append(counts[i].Value);
                }
                sb.Append("}}");
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[perf] mem: " + e.Message); }
        }
    }
}
