// AirbaseTaxi: AI planes getting stuck on the ground at airbases (owner 2026-10-10).
//
// Parts of "AI Plane Unstuck" by Appulcake (https://github.com/Appulcake/NO_AIPlaneUnstuck, commit 33281ed), rebuilt on
// this server's own game code (Assembly-CSharp df5bed594dd8) with only the parts the owner picked:
//   - the game's taxi obstacle check measures each plane against ITSELF (AIPilotTaxiState.RefreshObstacles:
//     InRange(item.position, item.position, 300)), so every plane the airbase controls counts as "in the way";
//     fixed to measure against our own plane, as the game clearly meant.
//   - a plane first in the takeoff queue keeps its place when it brakes (the game dropped it from the queue).
//   - taxiing AI only react to planes in a narrower cone ahead (TaxiConeDegrees; the game: 180).
//   - a landing plane only blocks a runway for takeoff while it is on the runway or on approach within
//     LandingBlockRange metres (the game: any plane registered to land, ~2-2.5 km out).
//   - AggressiveTakeoff: the plane first in the takeoff queue stops braking for other planes and does not wait at
//     runway crossings, so it commits to the runway.
//   - extra spawns: a side short of AI may spawn up to MaxSpawnsPerCheck aircraft in one 5-second deployment check
//     (owner: "That's going to be good add that too"); each goes through the game's own spawn and our AiAirportRotation.
// Left out on the owner's word: the stuck timer (removing AI would cost frames) and the mod's own airbase rotation (ours,
// AiAirportRotation, stays). Everything here only changes AI taxi/takeoff/spawn timing choices.
//
// The parts from that mod are used under its MIT licence:
//   MIT License. Copyright (c) 2026 Appulcake
//   Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
//   documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
//   rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
//   persons to whom the Software is furnished to do so, subject to the following conditions: The above copyright notice
//   and this permission notice shall be included in all copies or substantial portions of the Software.
//   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
//   WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
//   COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
//   OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NukeStats
{
    internal static class AirbaseTaxi
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> LandingBlockRange;
        internal static ConfigEntry<float> TaxiConeDegrees;
        internal static ConfigEntry<bool> AggressiveTakeoff;
        internal static ConfigEntry<int> MaxSpawnsPerCheck;
        internal static ConfigEntry<int> MissingPerExtraSpawn;
        internal static ConfigEntry<int> ExtraSpawnBusyLimit;

        // the game's private fields
        static readonly AccessTools.FieldRef<AIPilotTaxiState, float> FLastObstacleRefresh = AccessTools.FieldRefAccess<AIPilotTaxiState, float>("lastObstacleRefresh");
        static readonly AccessTools.FieldRef<AIPilotTaxiState, float> FLastObstacleCheck = AccessTools.FieldRefAccess<AIPilotTaxiState, float>("lastObstacleCheck");
        static readonly AccessTools.FieldRef<AIPilotTaxiState, List<Obstacle>> FObstacles = AccessTools.FieldRefAccess<AIPilotTaxiState, List<Obstacle>>("obstacles");
        static readonly AccessTools.FieldRef<AIPilotTaxiState, Airbase> FAirbase = AccessTools.FieldRefAccess<AIPilotTaxiState, Airbase>("airbase");
        static readonly AccessTools.FieldRef<AIPilotTaxiState, float> FBrakeUrgency = AccessTools.FieldRefAccess<AIPilotTaxiState, float>("brakeUrgency");
        static readonly AccessTools.FieldRef<AIPilotTaxiState, Vector3> FObstacleAvoid = AccessTools.FieldRefAccess<AIPilotTaxiState, Vector3>("obstacleAvoidVector");
        static readonly AccessTools.FieldRef<AIPilotTaxiState, bool> FYielding = AccessTools.FieldRefAccess<AIPilotTaxiState, bool>("yielding");
        static readonly AccessTools.FieldRef<AIPilotTaxiState, GlobalPosition> FYieldPosition = AccessTools.FieldRefAccess<AIPilotTaxiState, GlobalPosition>("yieldPosition");
        static readonly AccessTools.FieldRef<AIPilotTaxiState, bool> FTakeoffQueued = AccessTools.FieldRefAccess<AIPilotTaxiState, bool>("takeoffQueued");
        static readonly AccessTools.FieldRef<AIPilotTaxiState, Airbase.Runway> FTakeoffRunway = AccessTools.FieldRefAccess<AIPilotTaxiState, Airbase.Runway>("takeoffRunway");
        static readonly AccessTools.FieldRef<AIPilotTaxiState, bool> FWaitingAtCrossing = AccessTools.FieldRefAccess<AIPilotTaxiState, bool>("waitingAtRunwayCrossing");
        static readonly AccessTools.FieldRef<PilotBaseState, Aircraft> FStateAircraft = AccessTools.FieldRefAccess<PilotBaseState, Aircraft>("aircraft");
        static readonly AccessTools.FieldRef<Airbase.Runway, Queue<Aircraft>> FTakeoffQueue = AccessTools.FieldRefAccess<Airbase.Runway, Queue<Aircraft>>("takeoffQueue");
        static readonly AccessTools.FieldRef<Airbase.Runway, List<Airbase.Runway>> FCrossingRunways = AccessTools.FieldRefAccess<Airbase.Runway, List<Airbase.Runway>>("crossingRunways");

        internal static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind("AirbaseTaxi", "Enabled", true,
                "Help AI planes get off the ground at busy airbases (parts of Appulcake's AI Plane Unstuck): the game's taxi "
                + "obstacle check is fixed to measure from the plane itself, a plane first in the takeoff queue keeps its place "
                + "when it brakes, and the settings below. Off = the game's own taxiing. Takes effect at once.");
            LandingBlockRange = config.Bind("AirbaseTaxi", "LandingBlockRange", 750f,
                "A landing plane only stops AI taking off from that runway while it is on the runway or lined up within this "
                + "many metres of it. 0 = the game's own (any plane registered to land, about 2 to 2.5 km out).");
            TaxiConeDegrees = config.Bind("AirbaseTaxi", "TaxiConeDegrees", 120f,
                "Width of the cone ahead in which a taxiing AI plane (before take-off) brakes for other planes. 180 = the game's own.");
            AggressiveTakeoff = config.Bind("AirbaseTaxi", "AggressiveTakeoff", true,
                "The AI plane first in a runway's takeoff queue stops braking for other planes and does not wait at runway "
                + "crossings, so it commits to the runway and takes off.");
            MaxSpawnsPerCheck = config.Bind("AirbaseTaxi", "MaxSpawnsPerCheck", 3,
                "Most AI aircraft a side may spawn in one of its 5-second deployment checks while it is short of AI (the game: 1). "
                + "Every spawn still goes through the game's own spawn and our AiAirportRotation, and the AI limit still applies. 1 = off.");
            MissingPerExtraSpawn = config.Bind("AirbaseTaxi", "MissingPerExtraSpawn", 5,
                "One extra spawn per check for about every this many AI aircraft the side is short of.");
            ExtraSpawnBusyLimit = config.Bind("AirbaseTaxi", "ExtraSpawnBusyLimit", 4,
                "Extra spawns only while at least one of the side's airbases has this many or fewer AI-controlled aircraft on it, "
                + "so a jammed airfield does not get more.");
            NukeStatsPlugin.Log?.LogInfo($"[taxi] airbase taxi: {(Enabled.Value ? "on" : "off")}, landing block {LandingBlockRange.Value:0} m, "
                + $"cone {TaxiConeDegrees.Value:0} deg, aggressive takeoff {(AggressiveTakeoff.Value ? "on" : "off")}, "
                + $"up to {MaxSpawnsPerCheck.Value} spawns per check");
        }

        // ---- extra spawns: after the game's own DeployUnits (one AI aircraft per 5 s check), a side short of AI may try a few more
        static readonly AccessTools.FieldRef<FactionHQ, List<Aircraft>> FActiveAI = AccessTools.FieldRefAccess<FactionHQ, List<Aircraft>>("activeAIAircraft");
        static readonly System.Reflection.FieldInfo FAirbasesSorted = AccessTools.Field(typeof(FactionHQ), "airbasesSorted");
        static readonly System.Reflection.MethodInfo MDeployAIAircraft = AccessTools.Method(typeof(FactionHQ), "DeployAIAircraft");

        // the game's own AI limit: AIAircraftLimit + enemy players * add - friendly players * reduce, minus AI already up
        static int MissingAI(FactionHQ hq)
        {
            int friendly = hq.factionPlayers.Count, enemy = 0;
            foreach (FactionHQ other in FactionRegistry.GetAllHQs())
                if (other != hq) enemy += other.GetPlayers(false).Count;
            float target = hq.AIAircraftLimit + enemy * hq.addAIPerEnemyPlayer - friendly * hq.reduceAIPerFriendlyPlayer;
            var active = FActiveAI(hq);
            return Mathf.Max(0, Mathf.CeilToInt(target - (active != null ? active.Count : 0)));
        }

        static bool HasQuietAirbase(FactionHQ hq)
        {
            int limit = Mathf.Max(ExtraSpawnBusyLimit.Value, 0);
            if (!(FAirbasesSorted?.GetValue(hq) is List<(Airbase airbase, float distance)> sorted)) return false;
            foreach (var entry in sorted)
            {
                Airbase ab = entry.airbase;
                if (ab == null || ab.disabled || !ab.AnyHangarsAvailable()) continue;
                if ((ab.ControlledAircraft?.Count ?? 0) <= limit) return true;
            }
            return false;
        }

        [HarmonyPatch(typeof(FactionHQ), "DeployUnits")]
        internal static class ExtraSpawnsPatch
        {
            static void Postfix(FactionHQ __instance)
            {
                if (!On || MDeployAIAircraft == null) return;
                try
                {
                    int max = Mathf.Max(1, MaxSpawnsPerCheck.Value);
                    if (max <= 1) return;
                    int missing = MissingAI(__instance);
                    if (missing <= 0) return;
                    int extra = Mathf.Clamp(1 + missing / Mathf.Max(1, MissingPerExtraSpawn.Value), 1, max) - 1;   // DeployUnits already tried one
                    var active = FActiveAI(__instance);
                    for (int i = 0; i < extra && active != null; i++)
                    {
                        if (!HasQuietAirbase(__instance) || MissingAI(__instance) <= 0) break;
                        int before = active.Count;
                        MDeployAIAircraft.Invoke(__instance, null);   // the game's own spawn (it checks the AI limit again)
                        if (active.Count <= before) break;            // nothing could spawn this check
                    }
                }
                catch (Exception e) { NukeStatsPlugin.Log?.LogError("[taxi] extra spawns: " + e.GetBaseException().Message); }
            }
        }

        static bool On => Enabled != null && Enabled.Value;

        static bool HasTakeoffPriority(AIPilotTaxiState s, Aircraft aircraft)
        {
            if (!FTakeoffQueued(s)) return false;
            var runway = FTakeoffRunway(s);
            if (runway == null || aircraft == null) return false;
            var q = FTakeoffQueue(runway);
            return q != null && q.Count > 0 && q.Peek() == aircraft;
        }

        // a landing plane that blocks the runway: on it, or lined up on approach within LandingBlockRange
        static bool HasBlockingLanding(Airbase.Runway runway, Aircraft checker)
        {
            float range = Mathf.Max(LandingBlockRange.Value, 0f);
            foreach (Aircraft landing in runway.GetLandingList())
            {
                if (landing == null || landing == checker || landing.disabled) continue;
                if (runway.AircraftOnRunway(landing)) return true;
                if (range > 0f && runway.AircraftOnApproach(landing, range, false)) return true;
            }
            return false;
        }

        static bool RangeOn => On && LandingBlockRange != null && LandingBlockRange.Value > 0f;

        // ---- obstacle list: the game's own code with the range measured from our plane (it measured each plane to itself)
        [HarmonyPatch(typeof(AIPilotTaxiState), "RefreshObstacles")]
        internal static class RefreshObstaclesPatch
        {
            static bool Prefix(AIPilotTaxiState __instance)
            {
                if (!On) return true;
                try
                {
                    ref float last = ref FLastObstacleRefresh(__instance);
                    if (Time.timeSinceLevelLoad - last < 4f) return false;
                    var airbase = FAirbase(__instance);
                    var aircraft = FStateAircraft(__instance);
                    if (airbase == null || aircraft == null) return true;          // the game's own handling
                    last = Time.timeSinceLevelLoad;
                    var obstacles = FObstacles(__instance);
                    obstacles.Clear();
                    Vector3 mine = aircraft.transform.position;
                    foreach (Aircraft item in airbase.ControlledAircraft)
                    {
                        if (item == null || item == aircraft) continue;
                        if (!FastMath.InRange(item.transform.position, mine, 300f)) continue;
                        obstacles.Add(new Obstacle(item.transform, item.maxRadius, item.obstacleTop));
                    }
                    return false;
                }
                catch { return true; }
            }
        }

        // ---- obstacle braking: the game's own code with the narrower cone (before take-off only) and the first-in-queue rule
        [HarmonyPatch(typeof(AIPilotTaxiState), "CheckObstacles")]
        internal static class CheckObstaclesPatch
        {
            static bool Prefix(AIPilotTaxiState __instance)
            {
                if (!On) return true;
                try
                {
                    var aircraft = FStateAircraft(__instance);
                    if (aircraft == null) return true;
                    if (AggressiveTakeoff.Value && HasTakeoffPriority(__instance, aircraft))
                    {
                        FBrakeUrgency(__instance) = 0f;
                        FObstacleAvoid(__instance) = Vector3.zero;
                        return false;
                    }
                    ref float lastCheck = ref FLastObstacleCheck(__instance);
                    if (Time.timeSinceLevelLoad - lastCheck < 1f) return false;
                    ref float brake = ref FBrakeUrgency(__instance);
                    brake = 0f;
                    lastCheck = Time.timeSinceLevelLoad;
                    FObstacleAvoid(__instance) = Vector3.zero;
                    bool takenOff = aircraft.pilots[0].flightInfo.HasTakenOff;
                    float coneDot = takenOff ? 0f : Mathf.Cos(Mathf.Clamp(TaxiConeDegrees.Value, 0f, 360f) * 0.5f * Mathf.Deg2Rad);
                    foreach (Obstacle obstacle in FObstacles(__instance))
                    {
                        if (obstacle.Transform == null) continue;
                        Vector3 vector = obstacle.Transform.position - aircraft.transform.position;
                        Vector3 normalized = vector.normalized;
                        float num = Vector3.Dot(normalized, aircraft.transform.forward);
                        float num2 = Vector3.Dot(-normalized, obstacle.Transform.forward);
                        if (num < coneDot) continue;
                        float magnitude = vector.magnitude - (obstacle.Radius + aircraft.maxRadius);
                        if (magnitude < 50f && num2 > 0f)
                        {
                            ref bool yielding = ref FYielding(__instance);
                            if (!yielding && takenOff)
                            {
                                yielding = true;
                                Vector3 vector2 = Vector3.RotateTowards(aircraft.transform.forward, -normalized, (float)Math.PI / 2f, 0f);
                                FYieldPosition(__instance) = aircraft.GlobalPosition() + vector2 * 50f;
                                break;
                            }
                            if (!(num > num2)) break;
                        }
                        float num3 = Mathf.Sqrt(Mathf.Max(magnitude - 10f, 0f));
                        brake += (aircraft.speed > num3) ? 1 : 0;
                    }
                    return false;
                }
                catch { return true; }
            }
        }

        // ---- a plane queued for takeoff keeps its place when it brakes (the game dequeued it, and both planes waited)
        [HarmonyPatch(typeof(AIPilotTaxiState), "WaitingForTakeoffClearance", new[] { typeof(float) })]
        internal static class KeepQueueSlotPatch
        {
            static void Prefix(AIPilotTaxiState __instance, out bool __state)
            {
                __state = false;
                try { __state = On && FTakeoffQueued(__instance); } catch { }
            }

            static void Postfix(AIPilotTaxiState __instance, bool __state)
            {
                if (!__state) return;
                try
                {
                    if (FTakeoffQueued(__instance)) return;
                    var runway = FTakeoffRunway(__instance);
                    var aircraft = FStateAircraft(__instance);
                    if (runway == null || aircraft == null || aircraft.disabled) return;
                    var q = FTakeoffQueue(runway);
                    if (q != null && !q.Contains(aircraft)) runway.QueueTakeoff(aircraft);
                    FTakeoffQueued(__instance) = true;
                }
                catch { }
            }
        }

        // ---- the first plane in the takeoff queue does not stop at runway crossings
        [HarmonyPatch(typeof(AIPilotTaxiState), "WaitingAtRunwayCrossing", new[] { typeof(Airbase) })]
        internal static class CrossingPatch
        {
            static bool Prefix(AIPilotTaxiState __instance, ref bool __result)
            {
                if (!On || !AggressiveTakeoff.Value) return true;
                try
                {
                    if (!HasTakeoffPriority(__instance, FStateAircraft(__instance))) return true;
                    FWaitingAtCrossing(__instance) = false;
                    __result = false;
                    return false;
                }
                catch { return true; }
            }
        }

        // ---- takeoff clearance: the game's own code with landings only counting within LandingBlockRange
        [HarmonyPatch(typeof(Airbase.Runway), nameof(Airbase.Runway.IsAvailableForTakeoff))]
        internal static class TakeoffAvailablePatch
        {
            static bool Prefix(Airbase.Runway __instance, Aircraft querier, ref bool __result)
            {
                if (!RangeOn) return true;
                try
                {
                    if (HasBlockingLanding(__instance, querier)) { __result = false; return false; }
                    var q = FTakeoffQueue(__instance);
                    if (q != null && q.Count > 0)
                    {
                        Aircraft first = q.Peek();
                        if (first == null || first.disabled) q.Dequeue();
                        __result = first == querier;
                        return false;
                    }
                    if (__instance.OtherAircraftUsingRunway(querier)) { __result = false; return false; }
                    var crossing = FCrossingRunways(__instance);
                    if (crossing != null)
                        foreach (Airbase.Runway r in crossing)
                            if (r != null && r.OtherAircraftUsingRunway(querier)) { __result = false; return false; }
                    __result = true;
                    return false;
                }
                catch { return true; }
            }
        }

        // ---- "is anyone else using this runway": landings only count within LandingBlockRange (used by takeoff
        //      clearance and by taxiing AI deciding whether to wait at a runway crossing)
        [HarmonyPatch(typeof(Airbase.Runway), nameof(Airbase.Runway.OtherAircraftUsingRunway))]
        internal static class OtherUsingPatch
        {
            static bool Prefix(Airbase.Runway __instance, Aircraft checker, ref bool __result)
            {
                if (!RangeOn) return true;
                try
                {
                    if (HasBlockingLanding(__instance, checker)) { __result = true; return false; }
                    var q = FTakeoffQueue(__instance);
                    __result = q != null && q.Count > 0 && q.Peek() != null && q.Peek() != checker;
                    return false;
                }
                catch { return true; }
            }
        }
    }
}
