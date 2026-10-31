using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;

namespace NukeStats
{
    // In-game score changes (2.1.0). Changes the IN-GAME score for these actions; funds stay exactly as the game pays
    // them (only rewardScore is touched, never rewardAllocation), and the bot banks the score as usual.
    //   downed pilots   +10 per pilot collected (rescue or capture), then +40 per pilot returned: the collecting aircraft
    //                   lands at a friendly airbase or carrier (the game has no "pilots aboard" state, so the plugin counts)
    //   repairs         +25 per completed repair (one ReportRepairAction = one building repaired by a player's truck)
    //   unit deploys    +25 per unit dropped from cargo, only from DeployGateSeconds (120) after that aircraft spawned
    //                   (shown with the game's kill-style popup, which names the unit: "M12 Jackknife +25.0")
    //   supply drops    +25 per naval supply or ammo drop (cargo with rearmShip / rearmGround), same 2 minute gate
    //   spotting        +5 per spotting bonus, jamming +15 per jamming bonus (the game pays them at its own threshold)
    //   base capture    +150 per credited player, replacing the game's share
    // Kills, refuel and the game's own rearm reward keep the game's values. Every amount is a [Points] setting and
    // only applies while Points.Enabled is on. Off by default; setcfg works live.
    internal static class Points
    {
        internal static ConfigEntry<bool>  Enabled;
        internal static ConfigEntry<float> PilotCollected, PilotReturned, Repair, DeployUnit, SupplyDrop, ReconBonus, JammingBonus, Capture;
        internal static ConfigEntry<int>   DeployGateSeconds;

        // our own RewardPlayer calls already carry our score; the RewardPlayer prefix must leave them alone
        [ThreadStatic] static bool _own;

        // pilots collected and not yet returned, per collecting aircraft (instance id)
        sealed class Load { internal Aircraft ac; internal Player p; internal int rescued, captured; }
        static readonly Dictionary<int, Load> _loads = new Dictionary<int, Load>();
        static readonly HashSet<int> _pilotsSeen = new HashSet<int>();      // a pilot is counted once, even if Capture runs twice
        static readonly Dictionary<string, (int acId, float t)> _spawned = new Dictionary<string, (int, float)>(StringComparer.Ordinal);
        static float _nextTick;
        static bool _saidOn;

        internal static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind("Points", "Enabled", false,
                "Change the in-game score for pilots, repairs, deploys, supply drops, spotting/jamming bonuses and base "
                + "captures to the values below. Funds stay as the game pays them. OFF (default) = the game's own points "
                + "(vanilla), and none of the other [Points] values do anything. Live.");
            PilotCollected = config.Bind("Points", "PilotCollected", 10f, "Score per downed pilot collected (rescued or captured), replacing the game's 2 or 3 x (1 + rank). Only used while Points.Enabled is on.");
            PilotReturned  = config.Bind("Points", "PilotReturned", 40f, "Score per collected pilot returned: the collecting aircraft lands at a friendly airbase or carrier. Only used while Points.Enabled is on.");
            Repair         = config.Bind("Points", "Repair", 25f, "Score per completed repair, replacing the game's value. No limit. Only used while Points.Enabled is on.");
            DeployUnit     = config.Bind("Points", "DeployUnit", 25f, "Score per unit deployed from cargo (gated by DeployGateSeconds). Only used while Points.Enabled is on.");
            SupplyDrop     = config.Bind("Points", "SupplyDrop", 25f, "Score per naval supply or ammo drop (gated by DeployGateSeconds). Only used while Points.Enabled is on.");
            DeployGateSeconds = config.Bind("Points", "DeployGateSeconds", 120, "Deploys and supply drops only score from this many seconds after the dropping aircraft spawned. Only used while Points.Enabled is on.");
            ReconBonus     = config.Bind("Points", "ReconBonus", 5f, "Score per spotting bonus (the game pays it when the spotting threshold is reached), replacing the game's value. Only used while Points.Enabled is on.");
            JammingBonus   = config.Bind("Points", "JammingBonus", 15f, "Score per jamming bonus, replacing the game's value. Only used while Points.Enabled is on.");
            Capture        = config.Bind("Points", "Capture", 150f, "Score per player credited with a base capture, replacing the game's share. Only used while Points.Enabled is on.");
        }

        internal static bool On
        {
            get { try { return Enabled != null && Enabled.Value; } catch { return false; } }
        }

        // Every mission change: nothing carries over.
        internal static void Reset()
        {
            _loads.Clear(); _pilotsSeen.Clear(); _spawned.Clear();
        }

        // Player.SetAircraft postfix: the new aircraft's spawn time, for the deploy gate.
        internal static void OnSpawn(Player p)
        {
            try
            {
                if (p == null || p.Aircraft == null) return;
                string sid = NukeStatsPlugin.Sid(p);
                if (string.IsNullOrEmpty(sid) || sid == "0") return;
                _spawned[sid] = (p.Aircraft.GetInstanceID(), Time.time);
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[points] OnSpawn: " + e); }
        }

        // FactionHQ.RewardPlayer prefix, on the paid path only (after the recon breaker check).
        internal static void Rescore(Player player, Unit target, ref float rewardScore, int mt)
        {
            if (_own || !On) return;
            try
            {
                if (!_saidOn) { _saidOn = true; NukeStatsPlugin.Log?.LogInfo($"[points] in-game score changes on (pilot {PilotCollected.Value:0.#}+{PilotReturned.Value:0.#}, repair {Repair.Value:0.#}, deploy {DeployUnit.Value:0.#}, supply {SupplyDrop.Value:0.#}, gate {DeployGateSeconds.Value}s, spotting {ReconBonus.Value:0.#}, jamming {JammingBonus.Value:0.#}, capture {Capture.Value:0.#})"); }
                switch (mt)
                {
                    case 2: rewardScore = ReconBonus.Value; break;                      // Recon
                    case 3: rewardScore = JammingBonus.Value; break;                    // Jamming
                    case 6: rewardScore = Repair.Value; break;                          // Repair
                    // RescuePilots / CapturePilots: +PilotCollected once per pilot. The winch can run Capture twice for one
                    // pilot in a frame with two physics steps (SlingloadHook.RescuePilotState); the repeat scores nothing.
                    case 7: rewardScore = NotePilot(player, target, false) ? PilotCollected.Value : 0f; break;
                    case 8: rewardScore = NotePilot(player, target, true) ? PilotCollected.Value : 0f; break;
                    case 9: rewardScore = Capture.Value; break;                         // CaptureLocation
                }
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[points] Rescore: " + e); }
        }

        // false = this pilot was already collected (a repeat Capture)
        static bool NotePilot(Player player, Unit pilot, bool captured)
        {
            if (pilot != null && !_pilotsSeen.Add(pilot.GetInstanceID())) return false;
            if (player == null || pilot == null) return true;
            Aircraft ac = player.Aircraft;
            if (ac == null || ac.disabled) return true;
            int id = ac.GetInstanceID();
            if (!_loads.TryGetValue(id, out var l) || l.ac != ac || l.p != player) _loads[id] = l = new Load { ac = ac, p = player };
            if (captured) l.captured++; else l.rescued++;
            return true;
        }

        // 1 Hz from PeriodicTick: an aircraft carrying collected pilots that is landed at a friendly airbase or carrier
        // pays PilotReturned per pilot, once. A destroyed or abandoned aircraft (or one its player left) loses them.
        internal static void Tick()
        {
            if (_loads.Count == 0) return;
            float now = Time.time;
            if (now < _nextTick) return;
            _nextTick = now + 1f;
            try
            {
                List<int> done = null;
                foreach (var kv in _loads)
                {
                    var l = kv.Value;
                    bool gone = l.ac == null || l.ac.disabled || l.p == null || l.ac.Player != l.p;
                    if (!gone && !AtFriendlyBase(l.ac)) continue;
                    if (!gone) PayReturned(l, "landed at a friendly base");
                    else NukeStatsPlugin.Log?.LogInfo($"[points] {l.rescued + l.captured} collected pilot(s) not returned (aircraft lost or left)");
                    (done ??= new List<int>()).Add(kv.Key);
                }
                if (done != null) foreach (int k in done) _loads.Remove(k);
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[points] Tick: " + e); }
        }

        // Aircraft.ReturnToInventory prefix: the crew got out at a friendly base before the 1 Hz check saw the landing.
        internal static void OnReturnToInventory(Aircraft ac)
        {
            try
            {
                if (ac == null || _loads.Count == 0) return;
                int id = ac.GetInstanceID();
                if (!_loads.TryGetValue(id, out var l)) return;
                _loads.Remove(id);
                if (l.ac == ac && l.p != null && ac.Player == l.p) PayReturned(l, "recovered at a friendly base");
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[points] OnReturnToInventory: " + e); }
        }

        // The game's own "landed at a friendly base" test (Aircraft.cs:2155): landed, not on water, inside one of its
        // own HQ's airbase radii (carriers carry an Airbase too).
        static bool AtFriendlyBase(Aircraft ac)
        {
            if (!ac.IsLanded() || NukeStatsPlugin.IsOnWater(ac)) return false;
            FactionHQ hq = ac.NetworkHQ;
            return hq != null && hq.AnyNearAirbase(ac.transform.position, out _);
        }

        static void PayReturned(Load l, string how)
        {
            if (!On) return;
            float each = PilotReturned.Value;
            if (l.rescued > 0) Pay(l.p, null, each * l.rescued, FactionHQ.RewardType.RescuePilots);
            if (l.captured > 0) Pay(l.p, null, each * l.captured, FactionHQ.RewardType.CapturePilots);
            NukeStatsPlugin.Log?.LogInfo($"[points] +{each * (l.rescued + l.captured):0.#} {NukeStatsPlugin.RawNameOf(l.p)}: {l.rescued} rescued + {l.captured} captured pilot(s) returned ({how})");
        }

        // MountedCargo.ActivateCargoVehicle postfix: runs once per item that leaves a cargo aircraft, on the server.
        internal static void OnCargoDeployed(MountedCargo mc, Unit cargoUnit)
        {
            if (!On || mc == null || cargoUnit == null) return;
            try
            {
                Aircraft ac = mc.attachedUnit as Aircraft;
                Player p = ac != null ? ac.Player : null;
                if (p == null) return;                                   // AI transports use the same path
                WeaponInfo info = mc.info;
                bool supply = info != null && (info.rearmGround || info.rearmShip);
                bool unit = !supply && cargoUnit is GroundVehicle;
                if (!supply && !unit) return;                            // fuel containers are not on the list
                string what = supply ? (info.rearmShip ? "naval supply drop" : "ammo drop") : "unit deployed";
                string sid = NukeStatsPlugin.Sid(p);
                float age = -1f;
                if (!string.IsNullOrEmpty(sid) && _spawned.TryGetValue(sid, out var s) && s.acId == ac.GetInstanceID()) age = Time.time - s.t;
                int gate = DeployGateSeconds.Value;
                if (age < gate)
                {
                    NukeStatsPlugin.Log?.LogInfo($"[points] no score for {NukeStatsPlugin.RawNameOf(p)}'s {what}: {(age < 0f ? "spawn time unknown" : $"{age:0}s after spawning")}, scores from {gate}s");
                    return;
                }
                float pts = supply ? SupplyDrop.Value : DeployUnit.Value;
                // The client picks the popup words per reward type and has none for a deploy. Supply drops show the game's
                // "Resupplied units +25.0"; a unit deploy uses the kill style, which names the unit ("M12 Jackknife +25.0")
                // and plays the kill sound. Only the popup: our kill tracking hooks ReportKillAction, which this never calls.
                Pay(p, cargoUnit, pts, supply ? FactionHQ.RewardType.Supply : FactionHQ.RewardType.Kill);
                NukeStatsPlugin.Log?.LogInfo($"[points] +{pts:0.#} {NukeStatsPlugin.RawNameOf(p)}: {what} ({cargoUnit.unitName}, {age:0}s after spawning)");
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[points] OnCargoDeployed: " + e); }
        }

        // Score only: no funds (allocation 0), through RewardPlayer so the score popup, the sortie score and our own
        // score event to the bot all happen the way they do for the game's rewards.
        static void Pay(Player p, Unit target, float score, FactionHQ.RewardType type)
        {
            FactionHQ hq = p != null ? p.HQ : null;
            if (hq == null || score <= 0f) return;
            _own = true;
            try { hq.RewardPlayer(p, target, 0f, score, type); }
            finally { _own = false; }
        }
    }

    // Unit deploys and supply drops. ActivateCargoVehicle is the end of PushCargoOut, which only the server branch
    // of MountedCargo.RailLaunch starts, once per cargo item (MountedCargo.cs:358-417).
    [HarmonyPatch(typeof(MountedCargo), "ActivateCargoVehicle")]
    internal static class CargoPointsPatch
    {
        static bool _fired;
        static void Postfix(MountedCargo __instance, Unit cargoUnit)
        {
            NukeStatsPlugin.Trace("CargoPointsPatch");
            if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] ActivateCargoVehicle hooked (deploy and supply drop points)"); }
            Points.OnCargoDeployed(__instance, cargoUnit);
        }
    }
}
