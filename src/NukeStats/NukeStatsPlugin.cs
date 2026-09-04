/*
 * NukeStats - server-side BepInEx plugin for Nuclear Option.
 *
 *  1) Stats sensor: emits each player's real PlayerScore/PlayerRank/Teamkills as
 *     "[NOSTATS] {json}" lines on stdout (-> console.log, which the external bot tails).
 *  2) End-of-game awards: on FactionHQ.DeclareEndGame("Victory") it determines the
 *     winning faction authoritatively (no faction-0 guessing) and emits award events:
 *     +WinPoints to every player on the winning side, and placement bonuses
 *     (1st/2nd/3rd by PlayerScore). The bot applies these to ranks.json.
 *  3) Custom chat (1.1.28 rebuild): the game update deleted the synced Player.PlayerName -
 *     every client now resolves names locally from Steam, so a server plugin can no longer
 *     inject "[TAG] Name" into the game's own name state. While Chat.CustomChat is ON the
 *     rank instead rides every string the PLUGIN composes: player chat is rerouted as
 *     "[TAG] Name: msg" server messages (native guards replicated, fail-open to pure native
 *     chat on any bind failure), join lines are replaced with ranked ones, and the
 *     swap / admin messages carry the tag. Rank label+colour come from
 *     plugin_ranks.txt, which the bot writes to the container.
 *  4) Profanity gate: the in-game filter doesn't work, so before chat broadcasts we
 *     scan it; if any token is a racist slur (leet/spacing/repeat-normalised), the
 *     WHOLE message is replaced with a canned line. Ordinary swearing is left alone.
 *  5) Team control: PvP auto-balance (move the rank-optimal unspawned player when a side
 *     is >MaxDifference ahead) + admin in-game chat commands (!move/!spec/!join/!balance,
 *     authorised by plugin_admins.txt) and a public !autobalance explainer.
 *
 * Member names confirmed by decompiling Assembly-CSharp.dll (ilspycmd). Tunables live
 * in BepInEx/config/anz.nukestats.cfg. Items marked VERIFY are runtime-confirmed at deploy.
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirage;
using Mirage.SocketLayer;
using NuclearOption.Chat;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace NukeStats
{
    [BepInPlugin(Guid, "NukeStats", Version)]
    public class NukeStatsPlugin : BaseUnityPlugin
    {
        public const string Guid = "anz.nukestats";
        public const string Version = "1.4.7";
        internal static ManualLogSource Log;
        internal static NukeStatsPlugin Instance;

        // -------- 1.1.30 runtime-proof trace layer --------
        // One-shot "[trace] <Name> fired" at the entry of EVERY Harmony patch body and every
        // feature pipeline head, so a SINGLE live test session yields a definitive
        // fired / never-fired coverage table straight from BepInEx LogOutput.log
        // (grep '\[trace\]'). Static HashSet gate = each name logs exactly once per boot;
        // fully wrapped so it can never throw or alter behaviour; O(1) per call after the
        // first. NOTE for the fake-null lesson of 1.1.29: this is static state, deliberately
        // independent of Instance / any GameObject lifetime.
        static readonly HashSet<string> _traced = new HashSet<string>(StringComparer.Ordinal);
        internal static void Trace(string name)
        {
            try { if (_traced.Add(name)) Log?.LogInfo("[trace] " + name + " fired"); } catch { }
        }

        // Tunable without rebuilding (BepInEx/config/anz.nukestats.cfg)
        internal static ConfigEntry<int> WinPoints, FirstPlace, SecondPlace, ThirdPlace;
        internal static ConfigEntry<float> SnapshotSeconds;
        internal static ConfigEntry<bool> EnforceBalance;        // PvP team-balance block-join
        internal static ConfigEntry<int> BalanceMaxDiff;
        internal static ConfigEntry<bool> CustomChat;            // server-level: plugin-composed chat/join lines (rank tags ride them)
        internal static ConfigEntry<bool> ProfanityFilter;       // replace whole messages that contain a racist slur
        internal static ConfigEntry<bool>  ReconBreaker, ReconSuppressAll;
        internal static ConfigEntry<float> ReconRateWindow, ReconRatePerWindow, ReconMatchCap;
        // per-player recon meter: sid -> (window start, score in window, match total, tripped)
        static readonly Dictionary<string, (float winStart, float winScore, float matchTotal, bool tripped)> _reconMeter
            = new Dictionary<string, (float, float, float, bool)>(StringComparer.Ordinal);

        // Cleared with the other per-match state so a breaker trip never outlives its match.
        internal static void ResetReconMeters()
        {
            try { _reconMeter.Clear(); } catch { }
        }

        // true = this reward must be BLOCKED. Fail-open: any doubt and the reward is paid.
        internal static bool ReconBlocked(Player player, float rewardScore)
        {
            try
            {
                if (ReconSuppressAll != null && ReconSuppressAll.Value) return true;   // emergency mute
                if (ReconBreaker == null || !ReconBreaker.Value) return false;         // pure vanilla
                string sid = Sid(player);
                if (string.IsNullOrEmpty(sid) || sid == "0") return false;
                float now = Time.time;
                float win = ReconRateWindow != null ? Mathf.Max(5f, ReconRateWindow.Value) : 60f;
                float lim = ReconRatePerWindow != null ? Mathf.Max(1f, ReconRatePerWindow.Value) : 30f;
                float cap = ReconMatchCap != null ? Mathf.Max(1f, ReconMatchCap.Value) : 200f;
                _reconMeter.TryGetValue(sid, out var m);
                if (m.tripped) return true;                                            // already blocked this match
                if (m.winStart <= 0f || now - m.winStart > win) { m.winStart = now; m.winScore = 0f; }
                float add = rewardScore > 0f ? rewardScore : 0f;
                m.winScore += add;
                m.matchTotal += add;
                bool trip = m.winScore > lim || m.matchTotal > cap;
                if (trip)
                {
                    m.tripped = true;
                    string why = m.winScore > lim ? $"rate {m.winScore:0.#}/{win:0}s > {lim:0}" : $"match total {m.matchTotal:0.#} > {cap:0}";
                    Log?.LogWarning($"[recon] breaker tripped for {RawNameOf(player)} ({sid}): {why} - further recon score/funds blocked for this match");
                    try
                    {
                        Out("{\"t\":\"recon\",\"id\":\"" + sid + "\",\"n\":\"" + Esc(RawNameOf(player))
                            + "\",\"win\":" + Num(m.winScore) + ",\"total\":" + Num(m.matchTotal) + ",\"blocked\":true}");
                    }
                    catch { }
                }
                // We have never been able to watch this mechanic on a live server - it was muted
                // before anyone measured it. Log a high-water mark every 500 so the real
                // distribution is known within a day and these thresholds can be set from data.
                if (!trip && (int)(m.matchTotal / 500f) > (int)((m.matchTotal - add) / 500f))
                    Log?.LogInfo($"[recon] {RawNameOf(player)} match recon total ~{m.matchTotal:0} (breaker at {cap:0}, rate {m.winScore:0.#}/{win:0}s of {lim:0})");
                _reconMeter[sid] = m;
                return trip;
            }
            catch (Exception e) { Log?.LogError("ReconBlocked: " + e); return false; }   // FAIL OPEN
        }

        internal static ConfigEntry<int>  ForfeitMinTeam;        // humans needed on YOUR side before a forfeit can be called
        internal static ConfigEntry<bool> HideRepairMessages;    // native "repaired/rearmed/refueled" notification; ON = hide/suppress (cfg-only toggle; no webcc row)
        internal static ConfigEntry<bool> CleanupPilots;         // periodically despawn old dismounted pilots
        internal static ConfigEntry<int> PilotLifetime;          // seconds a dismounted pilot may linger before cleanup
        internal static ConfigEntry<bool> AiLimit;               // AI aircraft limiter (perf precaution)
        internal static ConfigEntry<bool> TimeoutForceDefeat;    // PvE: force human defeat on mission-timer expiry
        internal static ConfigEntry<bool> PvpTimeoutResult;      // PvP: on timeout the higher total in-game score wins (tie = draw)
        internal static ConfigEntry<int>  TimeoutLeadSeconds;    // fire the timeout resolution this many seconds BEFORE MaxTime (so the map vote runs before rotation)
        internal static ConfigEntry<int> AiPerTeamCap, AiTotalCap, AiStuckSeconds, AiStuckRadius;
        internal static ConfigEntry<int> PvpStartingRank;        // PvP (both factions joinable): floor every player's start to this in-game rank (0 = off)
        internal static ConfigEntry<bool> ForfeitEnabled;        // PvP: allow a team to vote to surrender via !forfeit / !f
        internal static ConfigEntry<int>  ForfeitCooldownSeconds; // seconds before a team can START another forfeit vote
        // Annihilate auto-win: side has zero aircraft AND cannot spawn (classic AND; 1.1.11 undoes 1.1.10 OR).
        internal static ConfigEntry<bool>   AnnihilateEnabled;
        internal static ConfigEntry<bool>   AnnihilatePvPOnly;       // false = also PvE co-op (human vs AI)
        internal static ConfigEntry<bool>   AnnihilateRequireNoSpawn; // when true (default): also need !canSpawn (AND with planes==0)
        internal static ConfigEntry<bool>   AnnihilateCountAI;       // include AI aircraft in "planes left"
        internal static ConfigEntry<int>    AnnihilateGraceSeconds;
        internal static ConfigEntry<int>    AnnihilateMinMatchSeconds;
        internal static ConfigEntry<int>    AnnihilateMinPlayers;
        internal static ConfigEntry<string> AnnihilateBothDead;      // noop | draw
        // 1.2.4: FloodEnforce / FloodPerSec / FloodBurst / FloodOrderSpamKickSeconds are GONE with layer A
        // (see the "why layer A was removed" note above FleetOrderFloodPatch).
        internal static ConfigEntry<bool> FloodLogDrops, FloodDropDeadNet;   // LogDrops is a LAYER D control (DropInboundRpc)
        internal static ConfigEntry<int>  FloodDeadNetIdKickStrikes;    // stale-netId (not just-destroyed) RPCs within 10s before a kick (0 = off)
        internal static ConfigEntry<bool>  FloodInboundGuard;     // ROOT-CAUSE guard D: general per-connection inbound-RPC rate limit (ALL rpc types) + auto-kick a sustained flooder
        internal static ConfigEntry<int>   FloodInboundPerSec, FloodInboundBurst;
        internal static ConfigEntry<float> FloodInboundKickSeconds;
        internal static ConfigEntry<bool>  OverflowAbsorb;        // guard E: veto the send-buffer-full disconnect (reason 5) so an overflow can't mass-DC the lobby; genuinely-dead clients still drop via Mirage's own Timeout (reason 1, un-vetoed)
        internal static ConfigEntry<int>   OverflowKickThreshold; // # of DISTINCT victims ONE source must overflow within ~3s before that source is kicked (a legit sender overflows ~none). 0 = absorb-only
        internal static ConfigEntry<bool> MirageRaiseSendBuffer;  // anti mass-DC Layer C: raise the reliable-send-buffer cap
        internal static ConfigEntry<int>  MirageSendBufferLimit;  // target for MaxReliablePacketsInSendBufferPerConnection
        internal static ConfigEntry<bool> LimboAutoRelease;       // 1.2.1: transport-disconnect a wedged (map-load-failed) session so the client can rejoin cleanly
        internal static ConfigEntry<int>  LimboReleaseSeconds;    // 1.2.1: seconds wedged (no SceneReadyMessage, no aircraft) before auto-release
        internal static ConfigEntry<bool> ErrorKickLiftTimeout;   // 1.2.1 guard F: clear the TimeoutManager rejoin lockout an error-kick creates
        internal static ConfigEntry<bool> DiagNetProbe;          // ONE-OFF diagnostic: dump the connection object's fields to LogOutput.log to settle whether per-player RTT is reachable on this Mirage build (OFF by default)
        internal static ConfigEntry<string> CommandPolicy, CommandAllowedJsonKeys;  // restrict which units can be CmdSetDestination'd
        internal static ConfigEntry<bool>   CommandDiagLog;
        internal static ConfigEntry<float> SwapAltitude;         // !swapteam/!forceteamswap: Cricket spawn altitude (world-Y m)
        internal static ConfigEntry<string> SkyDropHeartlandPala, SkyDropHeartlandBdf,           // faction-safe drop points "x,z"
                                            SkyDropIgnusPala, SkyDropIgnusBdf;                   // (swap Cricket spawns)
        internal static ConfigEntry<int> PvpRankCatchupMinutes, PvpRankCatchupMaxRank;   // rank catch-up floor over match time
        internal static ConfigEntry<int> RankFundsPerRank;       // accumulative rank funds (0 = off)
        internal static ConfigEntry<string> RankFundsMode;       // WHEN funds pay: catchup_raised | any_rankup | catchup_all
        internal static ConfigEntry<bool> DamageCalibration;     // [dmgcal] diagnostic log (Teamkill section)
        static ChatManager Cm;                                   // cached ChatManager (1.1.30: STATIC - survives the plugin GameObject's destruction; Unity-null re-resolve in ResolveChatManager)

        // sid -> (short label, hex colour, full rank name), pushed by the bot as plugin_ranks.txt.
        // label (ABBR) goes into plugin-composed strings via Prefixed (1.1.28: the game no longer
        // syncs names, so rank lives ONLY in strings the plugin composes - chat/join/swap).
        static readonly Dictionary<string, (string label, string color, string full)> RankMap =
            new Dictionary<string, (string, string, string)>();
        // 1.1.29: sid -> the bot's LAST-KNOWN display name (rank-file field 6). Server-side Steam
        // often cannot resolve personas at all, so RawNameOf falls back to this before surrendering
        // to the "ID: 7656..." sentinel. Empty/missing field = unknown (old files stay valid).
        static readonly Dictionary<string, string> NameFallback = new Dictionary<string, string>(StringComparer.Ordinal);
        static long _rankFileTicks = -1;
        static string RankFilePath => Path.Combine(Paths.GameRootPath, "plugin_ranks.txt");

        // Name cache (1.1.28): sid -> the player's RESOLVED game-shown name (GetPlayerName().
        // SanitizedName - the game applies SanitizeRichText(32)). Names now resolve per-process
        // and asynchronously from Steam, so this fills from RawNameOf/NameTick the moment the
        // server resolves a player; until then RawNameOf returns the game's own "ID: <steam64>"
        // sentinel. Pruned by PruneLeavers so a rejoin re-registers cleanly.
        static readonly Dictionary<string, string> RawNames = new Dictionary<string, string>();
        // dismounted-pilot cleanup: pilot -> first time we saw it (Time.time)
        static readonly Dictionary<PilotDismounted, float> PilotSeen = new Dictionary<PilotDismounted, float>();
        static float _nextPilotSweep;

        Harmony _harmony;
        static float _lastEnd = -999f;   // 1.1.30: STATIC (was instance) - read by static match-end/draw paths; the old Instance._lastEnd read sat behind a fake-null gate
        readonly Dictionary<string, float> _chatThrottle = new Dictionary<string, float>();

        void Awake()
        {
            Instance = this; Log = Logger;
            _cfgFile = Config;   // cache the ConfigFile NOW — it survives the GameObject being destroyed, whereas Instance.Config later reads as Unity-null
            OpenStaleNetGrace(60f);   // never strike stale netIds during boot/first scene load (AdvanceGame re-opens it per mission)
            try { DontDestroyOnLoad(gameObject); } catch { }   // try to survive scene loads on the dedicated server
            WinPoints       = Config.Bind("Scoring", "WinPoints", 200, "Points to each player on the winning side.");
            FirstPlace      = Config.Bind("Scoring", "FirstPlace", 500, "Bonus to the top scorer of the match.");
            SecondPlace     = Config.Bind("Scoring", "SecondPlace", 250, "Bonus to 2nd place.");
            ThirdPlace      = Config.Bind("Scoring", "ThirdPlace", 100, "Bonus to 3rd place.");
            RankFundsPerRank = Config.Bind("Scoring", "RankFundsPerRank", 30,
                "In-game Allocation granted PER RANK on a rank lift (Player.AddAllocation units = millions of display funds; "
                + "3 = AddAllocation(3), not 3,000,000). 0 = off. Does NOT pay for PvpStartingRank / mission start-floor "
                + "itself (everyone starts equal). Pays mid-match catch-up ranks: CatchupTick raises, OR late-join "
                + "CatchupFloor above that join floor; any_rankup also pays natural rank-ups in play. "
                + "amount = ranks_gained x this value. CUMULATIVE and MONOTONIC per match: the same rank is never "
                + "granted twice (survives reconnect). Reset on mission change; prestige never re-grants. "
                + "Uses the same funds path as admin addfunds.");
            RankFundsMode = Config.Bind("Scoring", "RankFundsMode", "catchup_raised",
                "WHEN rank funds pay out (needs RankFundsPerRank above 0). "
                + "Never pays for match-start / join apply of PvpStartingRank or the mission start floor alone. "
                + "catchup_raised = mid-match catch-up ranks only (CatchupTick raises, and late-join CatchupFloor "
                + "above the join floor). A player already at that rank, or who earns it in play, gets nothing. "
                + "any_rankup = any player who reaches a new rank in play after first sighting (join floor is baseline, unpaid). "
                + "catchup_all = every connected player each time the catch-up floor steps up mid-match, one rank of funds each "
                + "(late-join catch-up delta also paid). Unknown values fall back to catchup_raised.");
            SnapshotSeconds = Config.Bind("Stats", "SnapshotSeconds", 10f, "Seconds between full per-player snapshots.");
            EnforceBalance  = Config.Bind("Balance", "Enforce", true,
                "PvP only: keeps the two teams' sizes close so one side doesn't badly outnumber the other. If a " +
                "team is more than 'Max Team Size Gap' players ahead, extra players are stopped from joining it " +
                "(and, with Auto-Move on, the rank-optimal player is moved to the smaller side).");
            BalanceMaxDiff  = Config.Bind("Balance", "MaxDifference", 2,
                "Max allowed team-size difference; balancing only triggers when a side is MORE than this many ahead (2 => a 2-player gap is allowed, only a 3+ gap acts). Higher = fewer/less-twitchy moves.");
            AutoMove        = Config.Bind("Balance", "AutoMove", true,
                "PvP only: when a side is more than MaxDifference ahead, MOVE the rank-optimal player to the smaller side (false = block-join only).");
            // (1.2.0: Balance.MoveOnlyUnspawned removed - it was never read, and it actively MISLED admins:
            //  BalanceOnce may still PICK an airborne player; Balance.MoveOnlyWhenGrounded decides whether the
            //  switch happens now or waits for them to land/die. Stale key in an old cfg is inert.
            //  1.3.23 replaces it with MoveOnlyWhenGrounded below, which IS read - the difference being that
            //  this one DEFERS the move instead of skipping it, so the balance still happens.)
            BalanceMoveOnlyGrounded = Config.Bind("Balance", "MoveOnlyWhenGrounded", true,
                "Never yank a player out of a flight to balance the teams. When ON (default), a picked player who "
                + "is AIRBORNE is told they will be moved, and the move happens the moment they land or die - so "
                + "nobody loses a sortie to the balancer. A picked player who is already on the ground or in the "
                + "spawn menu is moved immediately, as before. If the pending player is still flying after "
                + "PendingMoveTimeout the move is applied anyway, so one very long sortie cannot stall balancing "
                + "forever. When OFF, the old behaviour returns: airborne players are moved mid-flight.");
            BalancePendingTimeout = Config.Bind("Balance", "PendingMoveTimeout", 900,
                "Seconds a deferred balance move (MoveOnlyWhenGrounded) will wait for the player to land or die "
                + "before it is applied anyway. Default 900 = 15 minutes. 0 = wait forever.");
            BalanceNeverMoveTop = Config.Bind("Balance", "NeverMoveTopPlayer", true,
                "Never auto-balance a team's top scorer. #1 is by IN-GAME SCORE in the match being played "
                + "right now (not server rank, not the leaderboard), judged per team and rechecked "
                + "every pass, so it follows the lead as it changes hands. The balancer picks the next-best "
                + "candidate instead; if that top scorer is the ONLY person it could have moved, no move happens "
                + "at all and the teams stay uneven - the exemption is absolute, by design. Nobody is protected "
                + "before the first point of a match is scored.");
            RecheckSeconds  = Config.Bind("Balance", "RecheckSeconds", 6,
                "Seconds between auto-balance checks.");
            MoveDebounce    = Config.Bind("Balance", "MoveDebounce", 20,
                "Minimum seconds between auto-balance moves (anti-churn).");
            // (1.2.0: Balance.GraceSeconds removed - superseded by Balance.WarnSeconds. Its last reader was
            //  !autobalance, which quoted it to players and therefore LIED whenever the two disagreed
            //  (S1: GraceSeconds 180 vs WarnSeconds 450 => "~3 min" for a 7.5-min hold). ExplainAutobalance
            //  now reads WarnSeconds with MaybeBalance's own rounding, so the two can never disagree.)
            BalanceMinPlayers = Config.Bind("Balance", "MinPlayers", 6,
                "Auto-balance never MOVES anyone mid-match unless at least this many HUMANS are on the server "
                + "(no move, no warning below it). NOTE: this does NOT gate the join blocker - a player who picks "
                + "the fuller side while the gap already exceeds MaxDifference is bounced to spectate at ANY "
                + "headcount, including 2v0. Set Balance.Enforce=false if you want small lobbies truly untouched.");
            BalanceWarnSeconds = Config.Bind("Balance", "WarnSeconds", 300,
                "When teams become unbalanced (and >= MinPlayers are on), broadcast a warning and WAIT this many "
                + "seconds before moving anyone, giving the gap time to self-correct. Default 300 = a 5-minute warning. "
                + "The timer resets if teams even out (so each fresh imbalance gets its own 5-min warning).");
            BalanceMoveExemptGames = Config.Bind("Balance", "MoveExemptGames", 2,
                "Once auto-balance moves a player, don't move them again for this many GAMES (2 = at most once per 2 games). "
                + "Spreads the burden so the same person isn't repeatedly the one moved.");
            BalanceNewJoinerSeconds = Config.Bind("Balance", "NewJoinerSeconds", 900,
                "STRONGEST auto-balance protection: never move a player who connected less than this many seconds ago "
                + "(default 900 = 15 min). A new joiner is moved ONLY if every other non-exempt player on the bigger side "
                + "is also a new joiner. Resets if they leave and rejoin; after a server restart everyone counts as new "
                + "until the window elapses. 0 = off.");
            PvpStartingRank = Config.Bind("Mission", "PvpStartingRank", 3,
                "PvP matches only (both factions joinable - Escalation & Terminal Control): every player starts at "
                + "AT LEAST this in-game rank (applied on top of the mission's own playerStartingRank, incl. the built-in "
                + "PvP maps we can't edit). 0 = off. Co-op/PvE is unaffected (uses the mission file's playerStartingRank).");
            PvpRankCatchupMinutes = Config.Bind("Mission", "PvpRankCatchupMinutes", 0,
                "Rank catch-up: every this many MINUTES of match time, the starting-rank FLOOR rises by +1 - latecomers "
                + "spawn at the risen floor and connected players below it are raised too (a FLOOR: nobody is ever lowered). "
                + "0 = off. Base = the mission own starting rank; on PvP matches PvpStartingRank also floors the base.");
            PvpRankCatchupMaxRank = Config.Bind("Mission", "PvpRankCatchupMaxRank", 5,
                "Rank catch-up: the rising floor stops at this in-game rank. Ignored while catch-up is off.");
            ForfeitEnabled  = Config.Bind("Forfeit", "Enabled", true,
                "PvP only: a team can vote to SURRENDER the match via !forfeit / !f (loss for them, win for the other team). "
                + "Needs a majority of the team to agree.");
            ForfeitCooldownSeconds = Config.Bind("Forfeit", "CooldownSeconds", 90,
                "Seconds before a team can START another forfeit vote (anti-spam). The vote window is min(60, this).");
            // ---- Annihilate auto-win: ForceVictory when a side has zero aircraft AND cannot spawn
            //      (classic AND; 1.1.11 restores pre-1.1.10). Held for GraceSeconds.
            //      Empty air alone (planes==0 with hangars still up) does NOT end the match when RequireNoSpawn is on.
            //      PvE OK (PvPOnly default false). ----
            AnnihilateEnabled = Config.Bind("Annihilate", "Enabled", true,
                "Auto-declare victory when one side has NO planes AND cannot spawn (no hangars), sustained for GraceSeconds. "
                + "Spawn probe = FactionHQ.GetAirbases + Airbase.AnyHangarsAvailable. "
                + "ON by default; covers PvP and PvE unless PvPOnly is on.");
            AnnihilatePvPOnly = Config.Bind("Annihilate", "PvPOnly", false,
                "When TRUE, only run on PvP (two joinable human sides). Default FALSE so PvE co-op also ends when the "
                + "AI (or human) side is wiped (no planes and no hangars).");
            AnnihilateRequireNoSpawn = Config.Bind("Annihilate", "RequireNoSpawn", true,
                "When TRUE (default): wipe = (planes==0) AND (!canSpawn) — classic AND. "
                + "When FALSE: only planes==0 counts (hangars ignored). Neither hangars-alone nor planes-alone OR.");
            AnnihilateCountAI = Config.Bind("Annihilate", "CountAI", true,
                "Count AI aircraft toward 'planes left'. ON = a side still flying AI is not annihilated by planes==0.");
            AnnihilateGraceSeconds = Config.Bind("Annihilate", "GraceSeconds", 20,
                "Seconds the no-planes AND no-hangars condition must hold continuously before ForceVictory.");
            AnnihilateMinMatchSeconds = Config.Bind("Annihilate", "MinMatchSeconds", 120,
                "Do not annihilate until this many seconds after mission load (avoids false wins while airbases/AI spin up).");
            AnnihilateMinPlayers = Config.Bind("Annihilate", "MinPlayers", 1,
                "Minimum connected human players required before annihilate can fire (empty lobby guard).");
            AnnihilateBothDead = Config.Bind("Annihilate", "BothDead", "noop",
                "When EVERY side is annihilated at once: noop = do nothing; draw = ForceDraw if two sides.");
            SwapAltitude    = Config.Bind("Swap", "Altitude", 2500f,
                "!swapteam / !forceteamswap: world-Y altitude (metres) at which the brief CI-22 Cricket is spawned "
                + "before ejecting. ~2500 m clears all terrain at the chosen out-of-the-way coords; raise to 3000 if any embed/crash is seen.");
            // ---- faction-safe drop points: where a swapped player is spawned, per map + destination team,
            //      so a drop lands over their OWN side instead of mid-map. Format: x,z world metres. ----
            SkyDropHeartlandPala = Config.Bind("Admin", "SkyDropHeartlandPala", "-5000,-60000",
                "Drop point x,z for a player swapped to PALA (Primeva) on Heartland - far SOUTH, behind the PALA "
                + "landmass at the map edge. Used by the swapteam/forceteamswap Cricket so the "
                + "spawn is well behind their own side of the map.");
            SkyDropHeartlandBdf = Config.Bind("Admin", "SkyDropHeartlandBdf", "-5000,60000",
                "Drop point x,z for a player swapped to BDF (Boscali) on Heartland - far NORTH, behind the BDF "
                + "landmass at the map edge.");
            SkyDropIgnusPala = Config.Bind("Admin", "SkyDropIgnusPala", "-75000,0",
                "Drop point x,z for a player swapped to PALA (Primeva) on Ignus - far west, over the Primeva side.");
            SkyDropIgnusBdf = Config.Bind("Admin", "SkyDropIgnusBdf", "75000,0",
                "Drop point x,z for a player swapped to BDF (Boscali) on Ignus - far east, over the Boscali side.");
            AdminSteamIds   = Config.Bind("Admin", "SteamIds", "",
                "Comma-separated SteamIDs allowed to use the IN-GAME team commands (!move/!spec/!join/!balance). " +
                "The public !autobalance explainer works for everyone; command-centre moves don't need this.");
            CustomChat      = Config.Bind("Chat", "CustomChat", true,
                "Custom chat. ON = the plugin composes chat itself: player chat is REROUTED as a "
                + "'[ACM] Brick: msg' server message with names in ABSOLUTE faction colours "
                + "(PALA #ffe294 / BDF #d4baff, spectator #CFCFCF, both chat modes; TTS is "
                + "deliberately NOT run on rerouted chat), join lines are replaced with "
                + "faction-coloured ones, and swap/admin messages carry the rank tag (rank tags "
                + "appear only while the rank ladder is running). OFF = the plugin leaves chat "
                + "completely native - no rewriting at all (vanilla chat + vanilla join lines). "
                + "Known custom-chat costs: the line renders as a server message, client-side mute "
                + "lists no longer filter it, and TTS does not read it.");
            ProfanityFilter = Config.Bind("Chat", "ProfanityFilter", true,
                "If a chat message contains a racist slur (leet/spacing/repeats normalised away), " +
                "replace the WHOLE message with a canned line. Ordinary swearing is NOT filtered.");
            ReconBreaker = Config.Bind("Recon", "Breaker", true,
                "Recon/spotting pays VANILLA score + funds (restored 2026-07-28 - it was muted "
                + "outright, an overcorrection after a 2026-06-24 point blowout whose recon "
                + "attribution was assumed and never proven; the mechanic runs unmodified on every "
                + "vanilla server). This switch keeps a per-player meter on it: if one player's recon "
                + "earnings go abnormal, THAT player stops earning recon score AND funds for the rest "
                + "of the match. Nobody is kicked or warned; everyone else is unaffected. OFF = pure "
                + "vanilla with no metering at all.");
            ReconRateWindow = Config.Bind("Recon", "RateWindowSeconds", 60f,
                "Rolling window the recon rate is measured over.");
            ReconRatePerWindow = Config.Bind("Recon", "RatePerWindow", 300f,
                "Recon score in one window that trips the breaker. Recon pays out in ~1.0 lumps (the "
                + "game accumulates to a threshold of 1 before it rewards), so this is about 300 "
                + "payouts a minute - five every second, sustained for a full minute. Honest play, "
                + "including a dedicated radar/AWACS pilot painting whole formations, is nowhere near "
                + "it; a runaway is far above it. Raise it if a real player is ever caught.");
            ReconMatchCap = Config.Bind("Recon", "MatchCap", 25000f,
                "Backstop: total recon score one player may earn in a match before the breaker trips. "
                + "This is deliberately far away, because matches here run long (median 73 min, up to "
                + "3 hours) and a cap that a long honest match can reach would punish the best radar "
                + "pilot on the server. Its only job is catching a slow burn that sits just under "
                + "RatePerWindow - at that rate this binds in about an hour and a half. Simulated against the "
                + "heaviest honest profile anyone could sustain (one payout every second for a three-hour "
                + "match, ~10700 points) it is still under half this cap.");
            ReconSuppressAll = Config.Bind("Recon", "SuppressAll", false,
                "EMERGENCY: mute recon/jamming score for EVERYONE, the pre-2026-07-28 behaviour. "
                + "One toggle, no deploy - use it if recon ever misbehaves and tell the maintainer.");
            ForfeitMinTeam = Config.Bind("Forfeit", "MinTeamSize", 3,
                "Minimum players on YOUR side before a forfeit vote can be called or pass. The vote needs a "
                + "majority of your current team (size/2 + 1), so on a team of ONE that majority is one vote - "
                + "a single player alone on a side could end the whole match instantly for everyone else. "
                + "Set 1 to allow that (the old behaviour); 3 means a forfeit is a real team decision.");
            HideRepairMessages = Config.Bind("KillFeed", "HideRepairMessages", true,
                "Hide the game's native 'repaired / rearmed / refueled' notification that fires when a player "
                + "services their aircraft at a base. ON (default) = suppressed entirely (nothing else emits this "
                + "line); OFF shows it.");
            TimeoutForceDefeat = Config.Bind("PvE", "TimeoutForceDefeat", true,
                "PvE co-op: when the mission timer expires and humans haven't won, declare the human team " +
                "DEFEATED (the AI faction 'wins') instead of silently rotating. No effect in PvP. " +
                "Default OFF until observed on a live timeout - flip to true in the config once verified.");
            PvpTimeoutResult = Config.Bind("PvP", "TimeoutResult", true,
                "PvP: when the mission timer expires with no winner, decide the match by TOTAL in-game score - the " +
                "higher-scoring team wins (an exact tie is a draw) instead of rotating with no result. " +
                "Off = the match just rotates. No effect in PvE/co-op.");
            TimeoutLeadSeconds = Config.Bind("Match", "TimeoutLeadSeconds", 120,
                "Fire the timeout resolution (PvE defeat or PvP score result) this many seconds BEFORE the mission's " +
                "MaxTime, so the match ends with time to spare and the map vote can run before the game auto-rotates. " +
                "120 = 2 min early. 0 = exactly at MaxTime (the map may rotate before the vote).");
            CleanupPilots   = Config.Bind("Cleanup", "DismountedPilots", true,
                "Periodically despawn dismounted (ejected) pilots that have lingered on the map, to cut clutter and load.");
            PilotLifetime   = Config.Bind("Cleanup", "PilotLifetimeSeconds", 300,
                "Seconds a dismounted pilot may linger before it is cleaned up (captures/rescues usually happen well within this).");
            TeamkillEnforce = Config.Bind("Teamkill", "Enforce", true,
                "Auto-punish friendly fire (destroying a friendly player's aircraft/vehicle/building). Per match: " +
                "1st = eject + private warning, 2nd = kick (+ in-game rank reset on rejoin), 3rd = ban. Bans persist (plugin_bans.txt).");
            TeamkillMinDamage = Config.Bind("Teamkill", "MinDamage", 100f,
                "Minimum credited damage for a friendly kill to COUNT as a punishable teamkill; 0 = off. Default 100 = "
                + "one destroyed part: a deliberate gun kill credits ~100-140 while a mere graze credits under 100 - so 100 "
                + "rejects the grazed-a-teammate-who-later-died-to-terrain wrongful attribution without missing real kills. "
                + "A friendly kill below the floor is shown in Moderation as a flagged not-counted report, never a punishment.");
            TeamkillCollateralEnforce = Config.Bind("Teamkill", "CollateralEnforce", true,
                "COLLATERAL CHECK. Judge each friendly kill by what the same player blast/window ALSO killed: only-friendlies "
                + "= deliberate -> the punish ladder; a few of EACH (enemies >= friendlies) = collateral -> NOT punished, "
                + "Moderation entry listing every unit that died; overwhelming = collateral, no Moderation entry. FALSE = "
                + "LOG-ONLY: verdicts computed and logged but enforcement runs the old ladder regardless.");
            TeamkillCollateralWindow = Config.Bind("Teamkill", "CollateralWindow", 2.5f,
                "Seconds BEFORE a friendly kill in which the same player other kills count toward the collateral verdict "
                + "(one conventional bomb kills land within a couple of seconds).");
            TeamkillCollateralWindowNuclear = Config.Bind("Teamkill", "CollateralWindowNuclear", 20f,
                "Seconds counted EACH WAY around a friendly kill for NUKE-scale blasts (the shockwave expands at 340 m/s and "
                + "kills in ANY order over ~7-35s; nuclear is detected from the munition blastYield at launch). Also delays a "
                + "nuke event verdict/warn by this long.");
            TeamkillSilentMinEnemies = Config.Bind("Teamkill", "SilentMinEnemies", 10,
                "When a blast killed at least this many enemies AND at least SilentRatio x the friendly count, the collateral "
                + "verdict is SILENT - no Moderation entry, just a log line. Still counts toward CollateralMaxPerMatch so "
                + "silence cannot be farmed. 0 = tier off (every collateral verdict is logged in Moderation).");
            TeamkillSilentRatio = Config.Bind("Teamkill", "SilentRatio", 5f,
                "Companion to SilentMinEnemies: enemies must also be >= this many times the friendly count for the silent verdict.");
            TeamkillCollateralMaxPerMatch = Config.Bind("Teamkill", "CollateralMaxPerMatch", 3,
                "Anti-abuse cap: how many EXONERATING collateral/big-unit verdicts one player can receive per match before "
                + "further friendly kills are judged on the normal ladder regardless. 0 = uncapped.");
            TeamkillBigUnitExempt = Config.Bind("Teamkill", "BigUnitExempt", true,
                "If the same blast/window also killed a BIG enemy objective (carrier/destroyer/other ship classes), treat the "
                + "friendly kill as collateral of that strike - flag it in Moderation, never punish.");
            // Damage calibration diagnostic. Moved out of the Stats section (0.9.47) into Teamkill - it feeds the
            // teamkill min-damage floor calibration, so it belongs with the other moderation/teamkill diagnostics.
            DamageCalibration = Config.Bind("Teamkill", "DamageCalibration", true,
                "Log a [dmgcal] line (victim unit type, total credited damage at death, top attacker share, attacker unit) "
                + "for every player-caused unit death. The game exposes no max-HP, so total-credited-damage-at-death is the "
                + "best proxy for a unit effective HP pool - collected over time it builds an empirical per-unit kill-threshold "
                + "table used to calibrate the teamkill MinDamage floor. Log-only; no gameplay effect.");
            AiLimit         = Config.Bind("AILimit", "Enforce", true,
                "Performance precaution: cap AI aircraft and clear stuck ones. ONLY ever removes AI aircraft, never players.");
            AiPerTeamCap    = Config.Bind("AILimit", "PerTeamAICap", 32,
                "Max AI aircraft flying per faction. The excess (grounded/lowest first) is destroyed.");
            AiTotalCap      = Config.Bind("AILimit", "TotalAircraftCap", 64,
                "Max TOTAL aircraft (AI + players, all sides). When exceeded, AI is removed from the side with the " +
                "MOST aircraft until at/under the cap -- a player is never force-ejected, only AI.");
            AiStuckSeconds  = Config.Bind("AILimit", "StuckSeconds", 45,
                "A GROUNDED AI aircraft that has not moved for this many seconds is cleared (frees a clogged runway). 0 = off.");
            AiStuckRadius   = Config.Bind("AILimit", "StuckRadiusMetres", 25,
                "Movement radius (metres) under which a grounded AI counts as 'not moving' for the stuck check.");
            FloodLogDrops   = Config.Bind("Flood", "LogDrops", true,
                "Log (throttled, at most once per 5s per connection) the name/SteamID of a sender whose inbound RPCs are "
                + "being rate-dropped by the Layer D inbound guard (Flood.InboundRpcGuard). 1.2.4: this no longer has "
                + "anything to do with move orders - the plugin does not rate-limit those at all any more.");
            FloodDropDeadNet = Config.Bind("Flood", "DropDeadNetIdRpcs", true,
                "Defence-in-depth: silently drop ServerRpcs aimed at a netId with no live object (already-destroyed/unknown). "
                + "The game drops these anyway, but first LOGS each one + pushes an error to the sender + builds a network "
                + "reader -- under a flood (a client re-firing at a just-destroyed unit) that storm exhausts the ByteBuffer "
                + "pool and overflows send buffers. Dropping silently removes the amplifier. Patches a private Mirage method; "
                + "fail-open (auto-disables if it can't bind, leaving the CmdSetDestination throttle as the primary guard).");
            FloodDeadNetIdKickStrikes = Config.Bind("Flood", "DeadNetIdKickStrikes", 3,
                "STALE-netId exploit kick. Mirage keeps its own 5-second list of just-destroyed netIds, so 'my unit died while "
                + "my order was in flight' (INNOCENT - always dropped silently, never counted) is distinguishable from "
                + "'commanding a netId the server has no record of at all' (the classic order-a-dead-unit exploit). After this "
                + "many stale-netId RPCs within 10s the sender is kicked. 0 = off (drop only). Admins exempt; Grief.ReportOnly "
                + "downgrades to a report; suppressed for 30s after every mission change and while 3+ players trip together "
                + "(both are normal stale-netId sources, not grief). Needs DropDeadNetIdRpcs on; the evidence (netId, strike "
                + "count, window) is logged on every kick. NOTE: on a server with DisableErrorKick=true this is the ONLY "
                + "penalty for the exploit -- the game's own SetError path is a no-op there.");
            FloodInboundGuard = Config.Bind("Flood", "InboundRpcGuard", true,
                "ROOT-CAUSE flood guard (Layer D): a GENERAL per-connection rate limit on ALL inbound ServerRpcs, not just "
                + "fleet move-orders. A single client streaming reliable RPCs is re-broadcast by the server to EVERY "
                + "player, so one source multiplies by the player count and overflows every client's reliable send buffer -- the "
                + "lobby-wide mass-disconnect flood that the old fleet-order limiter could not stop. Caps each connection's RPC intake with a "
                + "token bucket at the HandleRpc choke point; excess is dropped server-side. ALSO emits a [flood-measure] log of "
                + "the real peak inbound RPC/s per connection so the cap can be TUNED FROM DATA rather than a guess. Shares guard "
                + "B's HandleRpc patch; fail-open (any error/unbound = disabled, Layers B/C/E still apply). LIVE-tunable.");
            FloodInboundPerSec = Config.Bind("Flood", "InboundRpcPerSec", 400,
                "Sustained inbound ServerRpcs accepted per second per connection (token refill); excess is dropped. DELIBERATELY "
                + "HIGH (400) because this counts EVERY Cmd and a 60hz sim can legitimately stream many state RPCs/s per player -- "
                + "the observed flood was far higher (hundreds+/s of re-broadcast RPCs). Watch the [flood-measure] log for your "
                + "server's real peak, then set this to ~3-5x that peak before tightening. Dropping non-move-order RPCs is NOT "
                + "free (a dropped fire/spawn is lost), so keep this comfortably above legit peak.");
            FloodInboundBurst = Config.Bind("Flood", "InboundRpcBurst", 800,
                "Token-bucket capacity: the max burst of inbound RPCs before excess is dropped (bucket starts FULL, so a normal "
                + "burst -- a salvo, a spawn-in, a scene/mission load -- is never dropped). 800 = ~2s of headroom at 400/s.");
            FloodInboundKickSeconds = Config.Bind("Flood", "InboundRpcKickSeconds", 0f,
                "If a connection stays OVER the inbound rate limit continuously for this many seconds, auto-kick it (the offender, "
                + "not the lobby; admins exempt). DEFAULT 0 = DROP-ONLY, never kick. Auto-kick is OPT-IN: only enable it (typical "
                + "1.5) AFTER you have watched [flood-measure] and confirmed a legit client never sustains > InboundRpcPerSec, "
                + "otherwise a busy 60hz player could be false-kicked. Floored to 0.5s minimum when > 0.");
            OverflowAbsorb = Config.Bind("Flood", "AbsorbSendBufferOverflow", true,
                "GUARD E (the real mass-DC fix): when a client's reliable send buffer overflows, Mirage's built-in reaction is to "
                + "DISCONNECT that player -- and a flood overflows EVERYONE's buffer at once, so the game itself kicks the whole "
                + "lobby. This vetoes ONLY that buffer-full disconnect (Mirage DisconnectReason 5, used nowhere else, verified by "
                + "decompiling the SocketLayer) so the overflow is ABSORBED instead of disconnecting the lobby. COMMAND-AGNOSTIC "
                + "(works no matter what/how-little is spammed). The overflowing packet is dropped for that one connection, so a "
                + "SUSTAINED flood causes DESYNC (not a brief blip) until the source stops or is kicked -- but nobody is mass-kicked. "
                + "Genuinely-dead clients still drop via Mirage's own Timeout (reason 1), which is NOT vetoed, so there are no zombie "
                + "connections. Patches Mirage.NetworkPlayer.Disconnect(DisconnectReason); fail-open (any error/unbound = normal game "
                + "behavior). Known gaps: a match-start string-store send overflow uses a different disconnect overload (not covered); "
                + "SyncVar/spawn-storm floods are absorbed but their source is not identified for the kick.");
            OverflowKickThreshold = Config.Bind("Flood", "OverflowKickThreshold", 0,
                "GUARD E part 2 -- stop the flood at its source. DEFAULT 0 = ABSORB-ONLY (the absorb alone already prevents the "
                + "mass-kick; the source is not auto-kicked). Set to ~6 to ENABLE the auto-kick after you've watched the [flood] blame "
                + "logs and confirmed it only ever fingers real flooders. When enabled: each absorbed overflow is blamed on the client "
                + "whose ServerRpc is being broadcast at that instant (the amplifier); if ONE source overflows this many DISTINCT "
                + "victims within ~3s it is kicked (admins exempt). Counting DISTINCT victims (not raw overflows) is what makes it "
                + "safe -- a legit sender overflows ~no one, a flooder overflows everyone -- plus a congestion breaker suppresses the "
                + "kick when 3+ sources flood at once (server-wide lag, not one griefer). Only reached for RPC-triggered broadcasts.");
            CommandPolicy = Config.Bind("Command", "Policy", "HeliDroppedOnly",
                "Which units players may order via CmdSetDestination (unit move-commands). One of: "
                + "All (any commandable unit) | RateLimitOnly (alias of All) | "
                + "HeliDroppedOnly (ONLY player-deployed ground vehicles -- the Hexhound SAM/GMG, AA, APC, LAC "
                + "SAM/AT, AT trucks dropped or sling-loaded from a UH-190/Tarantula; blocks mission/AI ground "
                + "units, ships, missiles) | AllowlistTypes (all ground vehicles, or only those whose jsonKey is "
                + "in Command.AllowedJsonKeys) | Disabled (no unit can be commanded by anyone). The per-player "
                + "rate limit (Flood.*) ALWAYS applies on top. LIVE-tunable; an unknown/unresolved value fails "
                + "OPEN (treated as All) so a typo never breaks commanding.");
            CommandAllowedJsonKeys = Config.Bind("Command", "AllowedJsonKeys", "",
                "Only used when Policy=AllowlistTypes. Comma-separated UnitDefinition.jsonKey values to allow "
                + "(case-insensitive). EMPTY = allow ALL ground vehicles. Discover the exact jsonKeys with "
                + "Command.DiagLog=true, then paste them here, e.g. \"hexhound_sam,lac_at,apc\".");
            CommandDiagLog = Config.Bind("Command", "DiagLog", false,
                "Log the resolved unit type (Class/jsonKey), player-deployed owner state, and the ALLOW/DROP "
                + "decision for each command order (drops throttled ~once/5s per player). Turn ON briefly to "
                + "discover unit jsonKeys / confirm what's being blocked, then OFF (verbose).");
            MirageRaiseSendBuffer = Config.Bind("Mirage", "RaiseReliableSendBuffer", true,
                "Anti mass-DC (Layer C): raise Mirage's per-connection reliable-send-buffer cap "
                + "(MaxReliablePacketsInSendBufferPerConnection, game default 3000) so a transient fleet-order / dead-netId "
                + "RPC burst on a busy server is ABSORBED and drained instead of overflowing into a BufferFullException that "
                + "cascades a lobby-wide disconnect. Mutates the one Config at host start (BEFORE the Peer is built), so it "
                + "takes effect on the NEXT match host. ONLY raises a buffer ceiling -- never kicks, never touches gameplay. "
                + "Fail-open (auto-disables if the host/config site can't be resolved; Layers A/B still apply).");
            MirageSendBufferLimit = Config.Bind("Mirage", "ReliableSendBufferLimit", 12000,
                "Layer C target for MaxReliablePacketsInSendBufferPerConnection (default 12000 = 4x the game's 3000). Higher "
                + "absorbs bigger bursts but costs more memory per connection and a dead-slow client buffers longer before "
                + "it's finally dropped. Clamped to never go BELOW the game default 3000 (and never LOWERS an already-higher "
                + "value). Try 24000 (8x) if a burst still overflows. Live-tunable via the webcc settings menu, but only "
                + "applies at the NEXT match host.");
            LimboAutoRelease = Config.Bind("Limbo", "AutoRelease", false,
                "SHIPS OFF (2026-07-28). Detection and logging run either way - turn this ON only once the "
                + "[limbo] lines in the server log show it is identifying real wedges and not slow loaders. "
                + "It is off because it is the one remaining action in this build that can affect a player who "
                + "has done nothing wrong, and the person most exposed to that is whoever is testing alone on an "
                + "empty server. 2026-07-28 wedge fix. Since the 07-27 game update some clients authenticate, get their faction restored, "
                + "but NEVER finish loading the map (stale build/content on the client) - they sit connected with no spawn "
                + "request until the client dies into 'Local client stopped', a state only a full game-client restart clears. "
                + "When ON, a player who has been in that state for Limbo.ReleaseSeconds is released with a PLAIN transport "
                + "disconnect (the same Mirage INetworkPlayer.Disconnect() the game's own error handler uses) so their client "
                + "gets a clean 'Disconnected by server' and can reconnect immediately. VERIFIED SAFE: this path never touches "
                + "the session kick list (only KickPlayer/BanPlayer do), so it can never convert a wedge into a rejoin lockout. "
                + "ONLY fires for clients whose map load never completed (server-side SceneIsReady=false, i.e. no "
                + "SceneReadyMessage ever arrived) - a player idling in the spawn menu HAS loaded the scene and is NEVER "
                + "released. Per-player cooldown 10 min: a repeat wedge inside the cooldown is logged, not disconnected.");
            LimboReleaseSeconds = Config.Bind("Limbo", "ReleaseSeconds", 180,
                "Continuous seconds a player must be wedged (connected + faction restored + NO aircraft + map load never "
                + "completed) before Limbo.AutoRelease disconnects the session. Floored to 60 (the watchdog's detection "
                + "threshold). Raise it if players on very slow machines legitimately need longer to load a map; releases "
                + "are also suppressed for a grace window right after a map change so mid-rotation reloads are never hit. "
                + "Three further safeguards make a mass disconnect impossible: the wedge clock RESETS on every "
                + "level load (it no longer runs through a map rotation), THREE OR MORE players unready at once is "
                + "treated as a server event and suppresses releases entirely, and at most ONE player is released "
                + "per 5s scan.");
            ErrorKickLiftTimeout = Config.Bind("ErrorKick", "LiftTimeout", true,
                "GUARD F - the 2026-07-28 lockout fix. The game's error-kick (since the 07-27 update it fires on "
                + "InvalidTransformSnapshot noise, e.g. a client still streaming snapshots for its just-destroyed aircraft "
                + "25s after a death) also creates a TimeoutManager entry that SILENTLY refuses every rejoin for ~300s, "
                + "+10s more per attempt - the client shows the same 'Local client stopped' as the wedge, and repeated "
                + "error-kicks walk the player toward the game's PERSISTENT 'Error Auto Ban'. When ON, the moment an "
                + "error-kick creates that timeout the plugin clears it and rolls the ban-ladder counters back, so the "
                + "disconnect still happens (it cleanly resets the client) but the player can reconnect IMMEDIATELY and "
                + "can never be auto-banned by error noise. Instant-ban error flags (cheat-grade) are deliberately NOT "
                + "touched. Works regardless of the server's DisableErrorKick setting, on both servers.");
            DiagNetProbe = Config.Bind("Diag", "NetProbe", false,
                "ONE-OFF DIAGNOSTIC (default OFF). When on AND at least one player is connected, dump the first online "
                + "player's network-connection object to BepInEx/LogOutput.log (concrete type + every numeric/string field & "
                + "property, recursing one level into any AckSystem member) plus NetworkTime.Rtt, ONCE per process (a few "
                + "snapshots, then it stops). Pure read-only reflection, never touches the netcode, emits NOTHING to players. "
                + "Purpose: settle empirically whether per-player server-side RTT is even reachable on this Mirage build before "
                + "any ping feature is built (research says NetworkTime.Rtt is client-fed ~0 on a headless server). Turn ON, "
                + "capture one LogOutput.log dump, turn OFF.");
            GriefReportOnly = Config.Bind("Grief", "ReportOnly", false,
                "If true, the DETECT + REPORT to the Reports tab still happen but nobody is KICKED. LIVE and load-bearing: "
                + "this is the halfway house for the dead-unit exploit guard (Flood.DeadNetIdKickStrikes). Use it for a "
                + "night to validate the strike threshold against real play before enabling the kick. Default false = kick.");
            GriefExemptAdmins = Config.Bind("Grief", "ExemptAdmins", true,
                "Never auto-kick a player whose SteamID is in [Admin] SteamIds (an admin may be deliberately testing a "
                + "guard). LIVE and load-bearing: honoured by the dead-unit exploit kick, the Layer D inbound-RPC kick "
                + "and the Layer E overflow-source kick. Set false to include admins (e.g. to self-test a detector).");

            // ===== POSITION FEED ENRICHMENT =====
            AnomalyEnrichPos = Config.Bind("Anomaly", "EnrichPos", true,
                "When true (default): PosTick adds altitude y, airframe code ac, landed g to each t:pos "
                + "player. Set false to "
                + "emit legacy {id,x,z,k,h?} only.");

            LoadBans();
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;   // guard E: netcode overflow blame/kick runs ONLY on this thread

            _harmony = new Harmony(Guid);
            // PER-CLASS patch application (1.1.28, F1): PatchAll ran every attribute class inside ONE
            // try/catch, so a single dead target (a game update deleting one method) aborted the
            // application of every class AFTER it in metadata order (that is how the update killed
            // the CmdSetDestination patch). CreateClassProcessor(t).Patch() is the exact per-type path PatchAll
            // uses internally, so semantics are identical minus the shared fate: one dead target now
            // degrades ONLY its own feature. The '[patch] ... SKIPPED' string is a monitor-tick alarm
            // token. The four manual Mirage patches below keep their own individual try/catch blocks.
            int patchOk = 0; var patchFailed = new List<string>();
            foreach (var t in typeof(NukeStatsPlugin).Assembly.GetTypes())
            {
                if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try { _harmony.CreateClassProcessor(t).Patch(); patchOk++; }
                catch (Exception e)
                {
                    patchFailed.Add(t.Name);
                    Log.LogError($"[patch] {t.Name} SKIPPED (target missing/changed - THIS feature degrades, others unaffected): {e.Message}");
                }
            }
            Log.LogInfo($"[patch] applied {patchOk} class(es), skipped {patchFailed.Count}{(patchFailed.Count > 0 ? ": " + string.Join(", ", patchFailed) : "")}");
            try
            {
                var mine = _harmony.GetPatchedMethods().ToList();
                Log.LogInfo($"[diag] patched {mine.Count} method(s): " +
                    string.Join(", ", mine.Select(m => (m.DeclaringType != null ? m.DeclaringType.Name : "?") + "." + m.Name)));
            }
            catch (Exception e) { Log.LogError("patch diag: " + e); }

            // Flood guard Layer B: silently drop ServerRpcs aimed at a dead/unknown netId (kills the
            // "Spawned object not found" log + sender SetError + ByteBuffer-pool storm that overflows
            // reliable send buffers and mass-disconnects the lobby). Manual patch because
            // Mirage.RemoteCalls.RpcHandler is internal and HandleRpc is private ([AggressiveInlining]).
            // Fail-open: if it can't bind, guard D still caps inbound RPC volume and guard E still absorbs
            // the overflow -- but the dead-unit EXPLOIT is then unguarded, so the SKIPPED line below is an alarm.
            try
            {
                var rpcHandlerT = AccessTools.TypeByName("Mirage.RemoteCalls.RpcHandler");
                var handleRpc = rpcHandlerT != null ? AccessTools.Method(rpcHandlerT, "HandleRpc") : null;
                if (handleRpc != null)
                {
                    _harmony.Patch(handleRpc,
                        prefix:  new HarmonyMethod(typeof(DeadNetIdDropPatch).GetMethod("Prefix",  BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(DeadNetIdDropPatch).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic)));
                    Log.LogInfo("[diag] HandleRpc patched (flood guards B: dead-netId drop, D: inbound rate, E: source-tag)");
                }
                else Log.LogWarning("[flood] RpcHandler.HandleRpc not found; dead-netId drop DISABLED (guard B is the only dead-unit exploit protection -- investigate)");
            }
            catch (Exception e) { Log.LogError("[flood] HandleRpc patch failed (guard B DOWN: dead-unit exploit unguarded): " + e); }

            // Flood guard Layer E (the real mass-DC fix): veto Mirage's buffer-full disconnect (DisconnectReason 5) so a
            // send-buffer overflow is ABSORBED instead of disconnecting the player. That is the exact point the game kicks
            // the whole lobby, so vetoing it stops the mass-DC exploit regardless of which command triggers it, and can
            // never false-kick. Manual patch of Mirage.NetworkPlayer.Disconnect(DisconnectReason) via reflection.
            // Fail-open: if it can't bind, guards A/B/C/D still apply.
            try
            {
                var npT  = AccessTools.TypeByName("Mirage.NetworkPlayer");
                var drT  = AccessTools.TypeByName("Mirage.SocketLayer.DisconnectReason");
                var disc = (npT != null && drT != null) ? AccessTools.Method(npT, "Disconnect", new[] { drT }) : null;
                if (disc != null)
                {
                    _harmony.Patch(disc, prefix: new HarmonyMethod(
                        typeof(OverflowDisconnectVetoPatch).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)));
                    Log.LogInfo("[diag] NetworkPlayer.Disconnect(reason) patched (flood guard E: absorb send-buffer overflow -> no lobby-wide mass-DC)");
                }
                else Log.LogWarning("[flood] guard E: Mirage.NetworkPlayer.Disconnect(DisconnectReason) not found; overflow-absorb disabled (guards B/C/D still apply)");
            }
            catch (Exception e) { Log.LogError("[flood] guard E patch failed (guards B/C/D still apply): " + e); }

            // Flood guard Layer C: raise Mirage's per-connection reliable-send-buffer cap so a transient
            // fleet-order / dead-netId RPC burst is ABSORBED and drained instead of overflowing into a
            // BufferFullException -> lobby-wide mass-DC. Mutates the single Mirage.SocketLayer.Config at its
            // one creation site (NetworkManagerNuclearOption.ConfigureNetwork), after it's assigned to
            // Server.PeerConfig and BEFORE NetworkServer.StartServer builds the Peer. Reflective: no hard
            // SocketLayer reference. Fail-open: unresolved type/method -> warn + skip (Layers A/B still apply).
            try
            {
                if (MirageRaiseSendBuffer != null && MirageRaiseSendBuffer.Value)
                {
                    var nmnoT = AccessTools.TypeByName("NuclearOption.Networking.NetworkManagerNuclearOption");
                    var configure = nmnoT != null ? AccessTools.Method(nmnoT, "ConfigureNetwork") : null;
                    if (configure != null)
                    {
                        _harmony.Patch(configure, postfix: new HarmonyMethod(
                            typeof(MirageBufferRaisePatch).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic)));
                        Log.LogInfo("[diag] ConfigureNetwork patched (flood guard C: raise reliable send buffer)");
                    }
                    else Log.LogWarning("[flood] NetworkManagerNuclearOption.ConfigureNetwork not found; send-buffer raise disabled (Layers B/D/E still active)");
                }
            }
            catch (Exception e) { Log.LogError("[flood] ConfigureNetwork patch failed (Layers B/D/E still active): " + e); }

            // CONNECTION-HEALTH telemetry: record the DisconnectReason on each forced drop. The PUBLIC
            // NetworkServer.Disconnected event hands us the player but NOT the reason; the reason only exists
            // on the PRIVATE Peer_OnDisconnected(IConnection, DisconnectReason) callback. Manual patch (private
            // target). Read-only postfix: it just tallies per-SteamID forced-DC count + last reason for the
            // {"t":"net"} telemetry line. Fail-open: if the method can't be resolved, no-op (never blocks load).
            try
            {
                var nsT = AccessTools.TypeByName("Mirage.NetworkServer");
                var onDisc = nsT != null ? AccessTools.Method(nsT, "Peer_OnDisconnected") : null;
                if (onDisc != null)
                {
                    _harmony.Patch(onDisc, postfix: new HarmonyMethod(
                        typeof(DcReasonPatch).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic)));
                    Log.LogInfo("[diag] Peer_OnDisconnected patched (net-health: capture DisconnectReason)");
                }
                else Log.LogWarning("[net] NetworkServer.Peer_OnDisconnected not found; per-DC reason capture disabled (net telemetry still emits, lastDc stays empty)");
            }
            catch (Exception e) { Log.LogError("[net] Peer_OnDisconnected patch failed (net telemetry still emits): " + e); }

            // GUARD F (1.2.1, root cause of the 2026-07-28 owner lockout): the game's error-kick
            // (TimeoutManager.OnKickFromError) creates a ~300s entry that makes SteamNetAcceptCallback
            // SILENTLY refuse every rejoin (+10s per attempt via HasTimeout's spam penalty) - the client
            // renders each refusal as "Local client stopped", and repeated error-kicks escalate to the
            // game's persistent "Error Auto Ban". Two postfixes, both read the config live and fail open:
            //   * OnKickFromError -> lift the just-created timeout + roll back the ban-ladder counters
            //     (ErrorKick.LiftTimeout, default ON), and ALWAYS emit [errkick] evidence.
            //   * HasTimeout -> when a join is refused, log + emit a {"t":"joinblock"} frame (read-only)
            //     so a locked-out player is never invisible again.
            // Manual reflective binding: a future game update renaming TimeoutManager degrades ONLY this
            // guard (warn + skip), never the plugin.
            try
            {
                var tmT = AccessTools.TypeByName("NuclearOption.Networking.Authentication.TimeoutManager");
                var okfe = tmT != null ? AccessTools.Method(tmT, "OnKickFromError") : null;
                var hasT = tmT != null ? AccessTools.Method(tmT, "HasTimeout") : null;
                if (okfe != null)
                {
                    _harmony.Patch(okfe, postfix: new HarmonyMethod(
                        typeof(ErrorKickLiftPatch).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic)));
                    Log.LogInfo("[diag] TimeoutManager.OnKickFromError patched (guard F: lift error-kick rejoin lockout + evidence)");
                }
                else Log.LogWarning("[errkick] guard F: TimeoutManager.OnKickFromError not found; error-kick lockouts run NATIVE (no lift, no evidence)");
                if (hasT != null)
                {
                    _harmony.Patch(hasT, postfix: new HarmonyMethod(
                        typeof(JoinTimeoutBlockPatch).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic)));
                    Log.LogInfo("[diag] TimeoutManager.HasTimeout patched (guard F: join-refusal evidence, read-only)");
                }
                else Log.LogWarning("[errkick] guard F: TimeoutManager.HasTimeout not found; join refusals stay silent in the plugin feed");
            }
            catch (Exception e) { Log.LogError("[errkick] guard F patch failed (error-kick system runs native): " + e); }

            Log.LogInfo($"NukeStats {Version} loaded (+ team balance: autobalance fires ONLY on a LEAVE, then WARNS and waits before moving; protection tiers = new joiners (<{(BalanceNewJoinerSeconds!=null?BalanceNewJoinerSeconds.Value:900)}s, strongest) > then the best rank-evening pick; join-the-fuller-side = INSTANT spectate, no warning; + PvP !forfeit team-surrender vote (cd {ForfeitCooldownSeconds.Value}s); + PvP start-rank floor={PvpStartingRank.Value}; + admin !setrank/!setfunds/!addfunds; + live-map entity feed: AI aircraft + ships, heli/plane; + AI aircraft limiter: per-team {AiPerTeamCap.Value}/total {AiTotalCap.Value} caps + {AiStuckSeconds.Value}s stuck-runway clear @5s scan; AI-only, never players; LIFE EVENTS: a life ends ONLY on death/air-eject, survives disconnect + match-end (no match-end eject), balance/admin moves are life-NEUTRAL; strategic-strike announce removed; PvP balance: joinable-only team detect [spectate-move]; radar/spotting + jamming score VANILLA [per-player breaker armed]; FLOOD GUARD: A=REMOVED in 1.2.4 (the game already caps move commands at ~5/s per player; ours counted clicks, not wire RPCs, and false-kicked honest players) - move orders are NEVER dropped/warned/kicked by this plugin; B=silent-drop ServerRpc to dead netId [{(FloodDropDeadNet!=null&&FloodDropDeadNet.Value?"on":"off")}] + stale-netId exploit strikes -> stops match-start mass-DC; autobalance: never under MinPlayers={(BalanceMinPlayers!=null?BalanceMinPlayers.Value:6)} + {(BalanceWarnSeconds!=null?BalanceWarnSeconds.Value:300)}s WARNED hold, then MOVES the picked player via the swap mechanic (Cricket spawned high over open ocean (Swap.Altitude)); admin !swapteam/!forceteamswap [team swap + Cricket spawn HIGH over open ocean + eject -> UI reset, life/points-neutral]; + LIVE CONFIG (webcc settings menu via setcfg/dumpcfg -> live ConfigEntry edit + Config.Save)). RankFile={RankFilePath}");
            WebNameProbe();   // 1.1.40: prove the name lookup's TLS path on this container
            DumpCfg();   // emit an initial [NOSTATS] cfg snapshot so the webcc settings menu has live values on load
            try { var tgo = new GameObject("NukeStatsTicker"); DontDestroyOnLoad(tgo); tgo.AddComponent<Ticker>(); Log.LogInfo("[diag] NukeStatsTicker up (fallback periodic driver; survives mission/scene changes)"); }
            catch (Exception e) { Log?.LogError("ticker create: " + e); }
            StartConfigPoller();   // 1.2.0: the only driver that works on an empty server
        }

        // Deliberately NO OnDestroy/UnpatchSelf: on this dedicated server the manager
        // GameObject is destroyed shortly after load, and unpatching there was REMOVING
        // every hook (the debug trace showed the methods re-patched with 0 prefixes right
        // after we applied them). Harmony patches are static and live for the process, so
        // we never unpatch — the hooks then survive even if this object is destroyed.

        // Periodic full-player snapshot. On this dedicated server the manager
        // GameObject is destroyed shortly after Awake, so our own Update() never ticks
        // (verified: 0 "snap" lines reach console.log). We therefore drive the periodic
        // work from both a Harmony hook on FactionHQ.Update and a persistent fallback
        // ticker. The ticker is important during mission/scene transitions and built-in
        // PvP map states where HQ.Update can stop being a reliable heartbeat.
        static float _nextSnapShared;
        static int _snapDiag;
        static int _lastPeriodicFrame = -1;

        internal static void PeriodicTick()
        {
            try
            {
                int frame = UnityEngine.Time.frameCount;
                if (frame == _lastPeriodicFrame) return;
                _lastPeriodicFrame = frame;
                PerfTick();                           // server frametime sampler (smoothed ms; emitted on the net line)
                RttProbeTick();                       // per-player RTT: Steam m_nPing preferred; Notify ACK fallback
                MaybeEmitFactionColours();            // one-shot vanilla faction.color → hex for bot/WebCC
                PvETimeoutTick();                     // PvE: force human defeat when the mission timer expires
                AnnihilateTick();                     // no planes AND no hangars -> ForceVictory (grace)
                NameTick();                           // 1.1.28: pump async Steam name resolution + OnNameResolved re-snap
                PumpWebNames();                       // 1.1.38: apply server-side Steam web lookups (placeholder killer)
                PumpJoinAnnounces();                  // 1.1.28: ranked join lines waiting on name resolution (<=8s)
                MaybeSnapshot();
                MaybeCleanupPilots();
                MaybeBalance();
                PumpPendingBalance();                 // apply deferred balance moves once the pick lands/dies
                PumpBounces();                        // bounce wrong-team joiners to spectate (cheap when idle)
                LifeTick();                           // life/aircraft-state scan: map death crosses + bail/balance ledger upkeep
                PosTick();                            // live map: ~0.5s plane position + heading broadcast
                MapEntTick();                         // live map: ~1s AI aircraft + ships (with heading)
                TkTick();                             // teamkill enforcement (warn/eject/kick/ban)
                GriefTick();                          // 1.2.4: TELEMETRY ONLY — rolls order attempts into the net frame's "streak"; no longer kicks anything
                AiLimitTick();                        // AI aircraft limiter (cap + stuck-runway clear)
                CatchupTick();                        // rank catch-up: raise already-connected players below the risen floor
                RankFundsTick();                      // accumulative rank funds: grant on any in-game rank increase
                LimboWatchTick();                     // 1.1.30: loud diag when a rejoined player sits on a faction with no aircraft (reconnect desync)
                PollCommands();
            }
            catch (Exception e) { Log?.LogError("PeriodicTick: " + e); }
        }

        internal static void MaybeSnapshot()
        {
            try
            {
                float now = Time.time;
                if (now < _nextSnapShared) return;
                float iv = (SnapshotSeconds != null) ? Mathf.Max(2f, SnapshotSeconds.Value) : 10f;
                _nextSnapShared = now + iv;
                if (_snapDiag < 5)   // first few snapshots: confirm it runs + player count
                {
                    try { Log?.LogInfo($"[diag] snapshot #{_snapDiag}: {Humans().Count} player(s)"); } catch { }
                    _snapDiag++;
                }
                EmitAll("snap");
                NetHealthTick();                     // connection-health telemetry ({"t":"net"}); always-works, no RTT needed
                NetProbe();                          // one-off diagnostic dump (no-op unless Diag.NetProbe is on); settles RTT reachability
                PruneLeavers();                      // forget custom-chat bookkeeping for players who left
            }
            catch (Exception e) { Log?.LogError("MaybeSnapshot: " + e); }
        }

        // Drop name/JoinMessage state for SteamIDs no longer present, so a genuine
        // rejoin gets a fresh "joined" message and the dictionaries don't grow unbounded.
        // Also prunes the OnNameResolved one-shot registry (_nameSub) so a rejoining player
        // gets a fresh listener + resolved-name re-snap.
        static void PruneLeavers()
        {
            try
            {
                var present = new HashSet<string>();
                foreach (var p in Humans()) present.Add(Sid(p));
                PruneRttState(present);
                if (RawNames.Count > 0)
                    foreach (var sid in new List<string>(RawNames.Keys))
                        if (!present.Contains(sid)) RawNames.Remove(sid);
                if (_nameSub.Count > 0)
                    foreach (var sid in new List<string>(_nameSub))
                        if (!present.Contains(sid)) _nameSub.Remove(sid);
                // 1.2.4: the Layer-A prunes that stood here (click groups, token buckets, order-spam clocks)
                // went with layer A itself. The Layer-B stale-netId strike prune below is UNRELATED and stays.
                foreach (var sid in new List<string>(_staleNetStrikes.Keys))
                    if (!present.Contains(sid)) _staleNetStrikes.Remove(sid);
            }
            catch (Exception e) { Log?.LogError("PruneLeavers: " + e); }
        }

        // ---------------- 1.1.28 name resolution pump (F3) ----------------
        // Names no longer cross the wire: the server resolves each player's persona LOCALLY and
        // ASYNCHRONOUSLY (GetPlayerName -> UnitRegistry.cachedPlayerNames -> SteamFriends, with a
        // throttled RequestUserInformation while unresolved). For each human not yet in RawNames we
        // call RawNameOf (which caches on resolution and keeps the game's Steam request warm) and
        // register a ONE-SHOT OnNameResolved listener that caches the resolved name and re-emits
        // EmitOne(p,"snap") so the bot/webcc replace "ID: 7656..." in place with NO new event type.
        // AddLateEvent invokes a late-added listener immediately if already resolved - the _nameSub
        // set (pruned by PruneLeavers) absorbs that plus any double-invoke, so no duplicate snaps.
        static readonly HashSet<string> _nameSub = new HashSet<string>(StringComparer.Ordinal);
        static float _nextNameTick;
        internal static void NameTick()
        {
            try
            {
                float now = Time.time;
                if (now < _nextNameTick) return;
                _nextNameTick = now + 2f;
                foreach (var p in Humans())
                {
                    string sid = Sid(p);
                    if (string.IsNullOrEmpty(sid) || sid == "0") continue;
                    if (RawNames.ContainsKey(sid)) continue;          // already resolved + cached
                    RawNameOf(p);                                     // caches if resolved; else keeps the game's throttled Steam request going
                    if (RawNames.ContainsKey(sid) || _nameSub.Contains(sid)) continue;
                    _nameSub.Add(sid);                                // one listener per sid per session (rejoin re-registers via PruneLeavers)
                    var pl = p;                                       // capture THIS player for the listener
                    try
                    {
                        pl.OnNameResolved.AddListener(pn =>
                        {
                            try
                            {
                                string s2 = Sid(pl);
                                if (string.IsNullOrEmpty(s2) || s2 == "0") return;
                                string n = pn != null ? pn.SanitizedName : null;
                                if (!IsResolved(n)) return;
                                bool dup = RawNames.TryGetValue(s2, out var old) && old == n;
                                RawNames[s2] = n;
                                if (!dup)
                                {
                                    Log?.LogInfo($"[name] resolved {s2} -> {n}");
                                    EmitOne(pl, "snap");              // late-correct the bot's "ID: 7656..." in place
                                }
                            }
                            catch (Exception e) { Log?.LogError("OnNameResolved: " + e); }
                        });
                    }
                    catch (Exception e) { Log?.LogError("NameTick listen: " + e); }
                }
            }
            catch (Exception e) { Log?.LogError("NameTick: " + e); }
        }

        // ---------------- flood guard Layer A: REMOVED in 1.2.4 ----------------
        // What stood here: a per-player token bucket over UnitCommand.CmdSetDestination, the click-coalescing
        // machinery that tried to make one right-click cost one token however many units it moved, and the
        // consecutive-seconds order-spam clock that warned and then kicked. AllowFleetOrder / OpenClick /
        // PruneClickGroups / NoteOrderSpam and their state are all gone. NOTHING in this plugin drops, warns
        // about, or kicks for a unit move-order any more.
        //
        // WHY IT WENT (owner decision, 2026-07-28):
        //  1. IT WAS REDUNDANT. The game registers UnitCommand.CmdSetDestination with
        //     RpcRateLimitConfig.Enabled(interval 1s, refill 5, max 20, penalty 1), and Mirage keys that bucket
        //     inside a PER-PLAYER dictionary — so vanilla already caps every player at ~5 accepted move-RPCs/s
        //     (burst 20) across ALL their units, in NetworkPlayer.CheckRateLimit, inside HandleRpc, BEFORE this
        //     plugin's prefix ever runs. Re-verified against the DLL after the 2026-07-27 game update.
        //  2. IT MEASURED THE WRONG THING. The harm in the 2026-06-26/27 mass-disconnects was RPC COUNT ON THE
        //     WIRE (every accepted order is re-broadcast to every player), not clicks. Coalescing let up to 64
        //     unit-RPCs ride a single token, so the number layer A capped was not the number that hurts.
        //  3. IT HAD A PROVEN FALSE-POSITIVE HISTORY. It kicked a real player for "unit-flood (owned 8, 2/s)" —
        //     eight units at two commands a second is somebody ordering a group, which is just playing the game.
        //
        // WHAT STILL GUARDS THE SERVER (unchanged, all four still armed):
        //   B = dead-netId drop + stale-netId strikes (DeadNetIdDropPatch / NoteStaleNetIdRpc). This is the
        //       order-a-dead-unit EXPLOIT protection and the one the owner explicitly asked to keep. Vanilla
        //       does NOT cover it: a dead-netId RPC exits HandleRpc at the identity lookup, before the
        //       rate-limit check. On a DisableErrorKick=true server it is the only penalty that exists.
        //   C = Mirage reliable-send-buffer headroom.
        //   D = general inbound-RPC limit at HandleRpc (~400/s burst 800, ALL rpc types) — the broad backstop.
        //   E = the DisconnectReason 5 veto, i.e. the actual fix for the lobby-wide mass-disconnect.
        // FleetOrderFloodPatch itself SURVIVES: it still carries the Command.Policy target rule
        // (AllowCommandTarget) and the NoteOrderAttempt telemetry call. Only the rate limiting left it.

        // ---------------- flood guard Layer D: general per-connection INBOUND RPC rate limit (root-cause fix) ----------------
        // A single client streaming reliable ServerRpcs is re-broadcast by the server to EVERY connected client, so one
        // source multiplies by player-count and overflows every client's reliable send buffer -> the lobby-wide mass-DC
        // flood (Steam k_EResultLimitExceeded -> Mirage "Sent queue is full"). The retired layer A throttled ONLY CmdSetDestination;
        // this caps ALL inbound RPCs per SENDER at the HandleRpc choke point (shared with guard B). Excess is dropped
        // server-side (same safe path as guard B); a connection that SUSTAINS past InboundRpcKickSeconds MAY be auto-kicked.
        // SAFETY (why the defaults are conservative): this is the choke point for EVERY Cmd, and a 60hz sim can legitimately
        // stream many state RPCs/s per player -- and dropping a non-move-order RPC is NOT free (a dropped fire/spawn is lost).
        // So the cap defaults HIGH (400/s) and auto-kick DEFAULTS OFF (drop-only): the [flood-measure] log reports the real
        // per-connection peak, and the operator tightens the cap / opts into the kick only AFTER seeing that data. Thread
        // safety: HandleRpc dispatches during NetworkServer.Update on the MAIN thread (the CmdSetDestination prefix reads
        // Time.time in the same path and would already be log-spamming if it were off-thread), so Humans()/Sid/_tkKicks are safe;
        // and every path is wrapped so nothing can throw into the netcode (fails open = guard disables, never a crash/kick).
        internal sealed class RefCmp : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
        static readonly Dictionary<object, (float tokens, float last, float starvedSince)> _rpcBucket =
            new Dictionary<object, (float, float, float)>(new RefCmp());
        static readonly Dictionary<object, float> _rpcKickActed = new Dictionary<object, float>(new RefCmp());
        static readonly Dictionary<object, float> _rpcDropLog   = new Dictionary<object, float>(new RefCmp());
        static readonly Dictionary<object, int>   _rpcWindow    = new Dictionary<object, int>(new RefCmp());   // measurement: RPCs this 1s window
        static float _rpcBucketPrune, _rpcWindowStart, _rpcMeasureLog;
        static int _rpcWindowPeak;
        static bool _inboundDiagFired;

        // true = DROP this inbound RPC (over the per-connection rate limit), false = allow it. `sender` is HandleRpc's
        // first arg (__0, the sending connection/player) -- a stable per-connection object we key the token bucket on.
        internal static bool DropInboundRpc(object sender)
        {
            try
            {
                if (FloodInboundGuard == null || !FloodInboundGuard.Value || sender == null) return false;
                if (!_inboundDiagFired) { _inboundDiagFired = true; Log?.LogInfo("[diag] inbound RPC guard ACTIVE (flood guard D); VERIFY sender is an INetworkPlayer -> " + sender.GetType().FullName); }
                float now = Time.time;
                float cap  = Mathf.Max(1f, FloodInboundBurst  != null ? FloodInboundBurst.Value  : 800);
                float rate = Mathf.Max(1f, FloodInboundPerSec != null ? FloodInboundPerSec.Value : 400);

                // MEASUREMENT (always-on while enabled; drives NO drop/kick): count each connection's RPCs in 1s windows and
                // log the peak every 30s, so the REAL per-connection inbound rate can be observed and the cap/kick tuned from
                // data instead of a guess. Look at this number before tightening InboundRpcPerSec or enabling the kick.
                if (now - _rpcWindowStart >= 1f) { _rpcWindowStart = now; _rpcWindow.Clear(); }
                int wc = (_rpcWindow.TryGetValue(sender, out var wcp) ? wcp : 0) + 1;
                _rpcWindow[sender] = wc;
                if (wc > _rpcWindowPeak) _rpcWindowPeak = wc;
                if (now - _rpcMeasureLog > 30f)
                {
                    _rpcMeasureLog = now;
                    bool kickOn = (FloodInboundKickSeconds != null ? FloodInboundKickSeconds.Value : 0f) > 0f;
                    Log?.LogInfo($"[flood-measure] peak inbound RPC/s per connection (last 30s) = {_rpcWindowPeak}  [cap {(int)rate}/s burst {(int)cap}, auto-kick {(kickOn ? "ON" : "OFF")}]");
                    _rpcWindowPeak = 0;
                }

                // occasional prune so the ref-keyed dicts never outlive the connection set (bounded by concurrent players)
                if (now - _rpcBucketPrune > 30f)
                {
                    _rpcBucketPrune = now;
                    if (_rpcBucket.Count > 64)
                    {
                        var dead = new List<object>();
                        foreach (var kv in _rpcBucket) if (now - kv.Value.last > 30f) dead.Add(kv.Key);
                        foreach (var k in dead) { _rpcBucket.Remove(k); _rpcKickActed.Remove(k); _rpcDropLog.Remove(k); }
                    }
                }

                if (!_rpcBucket.TryGetValue(sender, out var b)) b = (cap, now, 0f);   // new connection: bucket starts FULL
                float tokens = Mathf.Min(cap, b.tokens + (now - b.last) * rate);
                if (tokens >= 1f) { _rpcBucket[sender] = (tokens - 1f, now, 0f); return false; }   // under limit -> allow + clear starvation

                float starvedSince = b.starvedSince > 0f ? b.starvedSince : now;   // over limit: record when starvation began
                _rpcBucket[sender] = (tokens, now, starvedSince);
                if (FloodLogDrops != null && FloodLogDrops.Value)   // throttled visibility that a connection is being rate-dropped
                {
                    if (!_rpcDropLog.TryGetValue(sender, out var dt) || now - dt > 5f)
                    { _rpcDropLog[sender] = now; Log?.LogWarning($"[flood] rate-dropping inbound RPCs from a connection -> sustained over {(int)rate}/s (burst {(int)cap})"); }
                }
                float kickSecs = FloodInboundKickSeconds != null ? FloodInboundKickSeconds.Value : 0f;   // DEFAULT 0 = drop-only (no kick until measured)
                if (kickSecs > 0f) kickSecs = Mathf.Max(0.5f, kickSecs);   // floor: a tiny typo must not insta-kick
                if (kickSecs > 0f && now - starvedSince >= kickSecs) MaybeKickFlooder(sender, now, (int)rate);
                return true;   // DROP the excess RPC
            }
            catch (Exception e) { Log?.LogError("DropInboundRpc: " + e); return false; }   // fail-open: never drop on error
        }

        // Queue a one-shot kick of a connection sustained-flooding inbound RPCs. Main thread; reuses the tk-kick drain
        // (TkTick). Resolves sender -> Player for the SteamID/report; admins exempt; throttled once per ~15s per sender.
        static void MaybeKickFlooder(object sender, float now, int rate)
        {
            try
            {
                if (_rpcKickActed.TryGetValue(sender, out var t) && now - t < 15f) return;   // throttle re-acting per connection
                _rpcKickActed[sender] = now;
                Player p = null;
                try
                {
                    foreach (var h in Humans())
                    {
                        if (ReferenceEquals(h.Owner, sender)) { p = h; break; }
                        object conn = h.Owner != null ? ReflectGet(h.Owner, "Connection") : null;
                        if (conn != null && ReferenceEquals(conn, sender)) { p = h; break; }
                    }
                }
                catch { }
                if (p == null) { Log?.LogWarning($"[flood] inbound RPC flood over {rate}/s from an unresolved connection -> already rate-dropping (no player to kick)"); return; }
                string sid = Sid(p); string nm = RawNameOf(p);
                if (GriefExemptAdmins != null && GriefExemptAdmins.Value && IsAdmin(p)) { Log?.LogWarning($"[flood] {nm} ({sid}) over {rate}/s inbound but ADMIN-exempt -> not kicked (still rate-dropped)"); return; }
                if (string.IsNullOrEmpty(sid) || sid == "0") return;   // unresolved -> the drop already protects the lobby
                Log?.LogWarning($"[flood] INBOUND RPC flood from {nm} ({sid}) -> sustained over {rate}/s; auto-kick (guard D)");
                // report to the webcc Reports tab ONLY when a kick actually fires (ts=0 -> the bot stamps the real time)
                Out("{\"t\":\"report\",\"id\":\"" + sid + "\",\"n\":\"" + Esc(nm)
                    + "\",\"reason\":\"RPC flood (sustained inbound rate) - server protection\",\"count\":0,\"rate\":" + rate
                    + ",\"action\":\"kick\",\"rejoin\":true,\"ts\":0}");
                try { Instance?.TellPlayer(p, "<color=#FF0000>Removed: RPC flooding (server protection).</color>"); } catch { }
                _tkKicks.Add(new KeyValuePair<string, float>(sid, now + 0.5f));   // drained by TkTick on the main thread
            }
            catch (Exception e) { Log?.LogError("MaybeKickFlooder: " + e); }
        }

        // ---------------- flood guard Layer E: absorb the send-buffer overflow + kick its source ----------------
        // The actual mass-DC mechanism: a flood overflows every client's reliable send buffer, and Mirage's reaction is
        // to DISCONNECT each overflowing player (reason 5) -> the game kicks the WHOLE lobby (not our guards). Guard E
        // (OverflowDisconnectVetoPatch) ALWAYS vetoes that reason-5 disconnect so the overflow is ABSORBED. A genuinely
        // dead client is still dropped by Mirage's own Timeout (reason 1, un-vetoed) -- so no grace valve is needed and
        // there are no zombies. To STOP the flood we attribute each absorbed overflow to the client whose ServerRpc is
        // being broadcast at that instant (_curSource) and kick a source that overflows too many DISTINCT victims: only
        // a genuine amplifier overflows many distinct players, a legit sender overflows ~none -> no false-kick, plus a
        // congestion breaker. Blame/kick is MAIN-THREAD-ONLY (gated on _mainThreadId + locked) so the netcode cannot
        // race the dictionaries; the absorb path itself touches no shared dictionary, so it is thread-safe regardless.
        // Documented limits: SyncVar/spawn-storm floods run OUTSIDE an RPC so _curSource is null -> absorbed but source
        // not kicked; and a match-start string-store overflow uses the no-arg Disconnect() overload, not covered here.
        [ThreadStatic] static object _curSource;                     // per-thread: the ServerRpc sender being dispatched/broadcast right now
        static readonly Dictionary<object, (HashSet<object> victims, float last)> _ovBlame = new Dictionary<object, (HashSet<object>, float)>(new RefCmp());
        static readonly Dictionary<object, float> _ovKickActed = new Dictionary<object, float>(new RefCmp());
        static readonly object _ovLock = new object();
        static float _ovStormAt;
        internal static int _mainThreadId;                           // captured in Awake (definitely main thread)
        internal static void SetRpcSource(object s) { _curSource = s; }
        internal static void ClearRpcSource() { _curSource = null; }

        // Called from OverflowDisconnectVetoPatch each time a buffer-full disconnect is absorbed; `victim` = the
        // NetworkPlayer whose buffer overflowed. Blames the current RPC source and kicks it once it has overflowed
        // OverflowKickThreshold DISTINCT victims within ~3s (with a congestion breaker). Main-thread-only + locked.
        internal static void NoteOverflowAbsorbed(object victim)
        {
            try
            {
                if (System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThreadId) return;   // blame/kick only on the main thread (netcode-safe)
                object src = _curSource;
                if (src == null || victim == null) return;           // overflow OUTSIDE an RPC (AI-tick / SyncVar / spawn broadcast) -> absorb only, blame no one
                int thr = OverflowKickThreshold != null ? OverflowKickThreshold.Value : 6;
                if (thr <= 0) return;                                // absorb-only mode
                float now; try { now = Time.time; } catch { return; }
                object toKick = null;
                lock (_ovLock)
                {
                    if (!_ovBlame.TryGetValue(src, out var b) || now - b.last > 3f) b = (new HashSet<object>(new RefCmp()), now);   // >3s gap = new burst window
                    b.victims.Add(victim); b.last = now;
                    _ovBlame[src] = b;
                    if (_ovBlame.Count > 64 || _ovKickActed.Count > 64)   // time-prune BOTH maps independently (a kicked source leaves _ovBlame, so its _ovKickActed entry must be aged out on its own or it leaks)
                    {
                        var deadB = new List<object>();
                        foreach (var kv in _ovBlame) if (now - kv.Value.last > 5f) deadB.Add(kv.Key);
                        foreach (var k in deadB) _ovBlame.Remove(k);
                        var deadK = new List<object>();
                        foreach (var kv in _ovKickActed) if (now - kv.Value > 20f) deadK.Add(kv.Key);
                        foreach (var k in deadK) _ovKickActed.Remove(k);
                    }
                    if (b.victims.Count >= thr)
                    {
                        int flooding = 0;                            // congestion breaker: many sources each hitting many victims = server-wide lag, not one griefer
                        foreach (var kv in _ovBlame) if (now - kv.Value.last <= 3f && kv.Value.victims.Count >= thr) flooding++;
                        if (flooding >= 3) { if (now - _ovStormAt > 30f) { _ovStormAt = now; Log?.LogWarning($"[flood] overflow STORM: {flooding} sources flooding at once -> treated as congestion; auto-kick SUPPRESSED (still absorbing)"); } }
                        else { _ovBlame.Remove(src); toKick = src; }
                    }
                }
                if (toKick != null) KickOverflowSource(toKick, now);
            }
            catch (Exception e) { Log?.LogError("NoteOverflowAbsorbed: " + e); }
        }

        // Kick the connection overflowing everyone's send buffer (guard E part 2). Resolves source -> Player; admins
        // exempt; throttled per source; queued via the tk-kick drain (TkTick). Main thread (from NoteOverflowAbsorbed).
        static void KickOverflowSource(object src, float now)
        {
            try
            {
                if (_ovKickActed.TryGetValue(src, out var t) && now - t < 15f) return;   // throttle re-acting per source
                _ovKickActed[src] = now;
                Player p = null;
                foreach (var h in Humans())
                {
                    if (ReferenceEquals(h.Owner, src)) { p = h; break; }
                    object conn = h.Owner != null ? ReflectGet(h.Owner, "Connection") : null;
                    if (conn != null && ReferenceEquals(conn, src)) { p = h; break; }
                }
                if (p == null) { Log?.LogWarning("[flood] send-buffer overflow flood from an unresolved connection -> absorbed (no player to kick)"); return; }
                string sid = Sid(p); string nm = RawNameOf(p);
                if (GriefExemptAdmins != null && GriefExemptAdmins.Value && IsAdmin(p)) { Log?.LogWarning($"[flood] {nm} ({sid}) is overflowing send buffers but ADMIN-exempt -> absorbed, not kicked"); return; }
                if (string.IsNullOrEmpty(sid) || sid == "0") return;
                Log?.LogWarning($"[flood] SEND-BUFFER OVERFLOW flood from {nm} ({sid}) -> auto-kick (guard E: overflow source)");
                Out("{\"t\":\"report\",\"id\":\"" + sid + "\",\"n\":\"" + Esc(nm)
                    + "\",\"reason\":\"send-buffer overflow flood (mass-DC exploit) - server protection\",\"count\":0,\"rate\":0,\"action\":\"kick\",\"rejoin\":true,\"ts\":0}");
                try { Instance?.TellPlayer(p, "<color=#FF0000>Removed: flooding the network (server protection).</color>"); } catch { }
                _tkKicks.Add(new KeyValuePair<string, float>(sid, now + 0.2f));   // drained by TkTick on the main thread
            }
            catch (Exception e) { Log?.LogError("KickOverflowSource: " + e); }
        }

        // COMMAND POLICY: which units may be ordered via CmdSetDestination, INDEPENDENTLY of any rate limit (there is none as of 1.2.4).
        // true = ALLOW (no longer subject to any rate limit; see rate limit), false = DROP this order outright. `cmd` is the
        // UnitCommand component, which lives on the SAME GameObject as the commanded unit (only GroundVehicle/
        // Ship/Missile are ICommandable). GroundVehicle.Networkowner = the deploying Player on a heli drop/sling,
        // null for mission/AI spawns -> the clean "player-deployed" discriminator. Default "All" = no filtering
        // (current behaviour). LIVE-tunable; fail-OPEN on any ambiguity (the rate limit is the real flood guard).
        static readonly Dictionary<string, float> _cmdPolicyDropLog = new Dictionary<string, float>();
        static HashSet<string> _allowedKeysCache; static string _allowedKeysRaw;

        internal static bool AllowCommandTarget(UnitCommand cmd, Player player)
        {
            try
            {
                string mode = CommandPolicy != null ? (CommandPolicy.Value ?? "All").Trim() : "All";
                if (mode.Length == 0
                    || string.Equals(mode, "All", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mode, "RateLimitOnly", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase))
                    return DropCmd(player, cmd, "policy=Disabled");

                Unit unit = cmd != null ? cmd.GetComponent<Unit>() : null;
                if (unit == null)   // resolve failure -> ALLOW (never break legit commanding); the rate limit still guards
                {
                    if (CommandDiagLog != null && CommandDiagLog.Value)
                        Log?.LogInfo("[cmdpolicy] unresolved target (no Unit on UnitCommand) -> ALLOW (fail-open)");
                    return true;
                }
                GroundVehicle gv = unit as GroundVehicle;

                if (string.Equals(mode, "HeliDroppedOnly", StringComparison.OrdinalIgnoreCase))
                {
                    bool ok = false; try { ok = gv != null && gv.Networkowner != null; } catch { ok = false; }
                    if (CommandDiagLog != null && CommandDiagLog.Value)
                        Log?.LogInfo($"[cmdpolicy] HeliDroppedOnly target={Describe(unit)} gv={(gv != null)} owned={(gv != null && SafeOwner(gv) != null)} -> {(ok ? "ALLOW" : "DROP")}");
                    return ok ? true : DropCmd(player, cmd, "not a player-deployed ground unit");
                }
                if (string.Equals(mode, "AllowlistTypes", StringComparison.OrdinalIgnoreCase))
                {
                    if (gv == null) return DropCmd(player, cmd, "not a GroundVehicle");
                    string keys = CommandAllowedJsonKeys != null ? (CommandAllowedJsonKeys.Value ?? "") : "";
                    if (string.IsNullOrWhiteSpace(keys)) return true;   // empty list => all ground vehicles allowed
                    if (!ReferenceEquals(keys, _allowedKeysRaw))        // rebuild cache only when the config string changes
                    {
                        _allowedKeysRaw = keys;
                        _allowedKeysCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var k in keys.Split(',')) { var t = k.Trim(); if (t.Length > 0) _allowedKeysCache.Add(t); }
                    }
                    string jk = gv.definition != null ? gv.definition.jsonKey : null;
                    bool ok = jk != null && _allowedKeysCache.Contains(jk);
                    if (CommandDiagLog != null && CommandDiagLog.Value)
                        Log?.LogInfo($"[cmdpolicy] AllowlistTypes jsonKey={jk} -> {(ok ? "ALLOW" : "DROP")}");
                    return ok ? true : DropCmd(player, cmd, $"jsonKey '{jk}' not in allowlist");
                }
                if (CommandDiagLog != null && CommandDiagLog.Value)
                    Log?.LogWarning($"[cmdpolicy] unknown Command.Policy '{mode}' -> ALLOW (fail-open)");
                return true;
            }
            catch (Exception e) { Log?.LogError("AllowCommandTarget: " + e); return true; }   // fail-open on any error
        }

        static bool DropCmd(Player player, UnitCommand cmd, string why)
        {
            try
            {
                string id = player != null ? Sid(player) : null;
                float now = Time.time;
                if (!string.IsNullOrEmpty(id) && (!_cmdPolicyDropLog.TryGetValue(id, out var t) || now - t > 5f))
                {
                    _cmdPolicyDropLog[id] = now;
                    string what = "?"; try { var u = cmd != null ? cmd.GetComponent<Unit>() : null; what = Describe(u); } catch { }
                    Log?.LogInfo($"[cmdpolicy] dropped order from {(player != null ? RawNameOf(player) : "?")} on {what} ({why})");
                }
            }
            catch { }
            return false;
        }

        static Player SafeOwner(GroundVehicle gv) { try { return gv.Networkowner; } catch { return null; } }

        static string Describe(Unit u)
        {
            if (u == null) return "null";
            try
            {
                string jk = u.definition != null ? u.definition.jsonKey : null;
                string nm = u.definition != null ? u.definition.unitName : u.unitName;
                return $"{u.GetType().Name}/{jk ?? nm ?? "?"}";
            }
            catch { return u.GetType().Name; }
        }

        // ======================= CONNECTION-HEALTH telemetry + RTT probe =======================
        // Two read-only, fail-open, never-throw additions for the connection-stress webcc panel.
        //  (A) NetHealthTick(): emit a {"t":"net"} line every snapshot when humans>0 -- per-player order/drop
        //      rate, anti-grief streak, forced-DC count + last reason -- using ONLY existing counters plus the
        //      two tiny tallies below. NEEDS NO RTT, so it ships regardless of whether ping is reachable.
        //  (B) NetProbe(): a ONE-OFF diagnostic (Diag.NetProbe, default false) that dumps the first online
        //      player's connection object's fields to LogOutput.log to settle whether per-player RTT is reachable.
        // Everything is try/catch-swallowed and read-only; nothing here can disturb the netcode.

        // per-player tallies for the {"t":"net"} line, reset on each emit (lightweight, no allocation churn)
        static readonly Dictionary<string, int> _netOrders = new Dictionary<string, int>();   // CmdSetDestination attempts since last emit
        static readonly Dictionary<string, int> _netDrops  = new Dictionary<string, int>();    // ALWAYS 0 since 1.2.4:
        // layer A (the fleet-order rate limiter) was its only writer. The "drop" field is still emitted so the
        // net-health frame keeps its shape for anything already parsing it; nothing reads it today. Layer D does
        // still drop RPCs, but it keys on the connection rather than a SteamID, so it cannot feed this map.
        // forced-DC bookkeeping, populated by DcReasonPatch (keyed on SteamID)
        internal static readonly Dictionary<string, int>    _dcCount  = new Dictionary<string, int>();
        internal static readonly Dictionary<string, string> _dcReason = new Dictionary<string, string>();
        static float _netEmitElapsedAnchor = -1f;

        // Reflectively read NetworkTime.Rtt (proves it reads ~0 on a headless server). Empty string if unresolved.
        static string ProbeRttString()
        {
            try
            {
                var ntT = AccessTools.TypeByName("Mirage.NetworkTime");
                if (ntT == null) return "";
                var rttP = AccessTools.Property(ntT, "Rtt");
                if (rttP != null && rttP.GetMethod != null && rttP.GetMethod.IsStatic)
                    return System.Convert.ToString(rttP.GetValue(null), CultureInfo.InvariantCulture);
                return "";
            }
            catch { return ""; }
        }

        // Reflectively read the per-connection reliable-send-buffer cap (Layer C target) for the bufCap field.
        static int ProbeSendBufferCap()
        {
            try
            {
                var nmno = NetworkManagerNuclearOption.i;
                if (nmno == null) return 0;
                var server = ReflectGet(nmno, "Server");
                var peerCfg = server != null ? ReflectGet(server, "PeerConfig") : null;
                var v = peerCfg != null ? ReflectGet(peerCfg, "MaxReliablePacketsInSendBufferPerConnection") : null;
                return v != null ? System.Convert.ToInt32(v) : 0;
            }
            catch { return 0; }
        }

        // FIELD-first + per-(Type,name) CACHED reflective getter (read-only, swallow).
        // 1.1.30: the game update turned the probed members (NetworkManagerNuclearOption.Server,
        // Config.MaxReliablePacketsInSendBufferPerConnection) into plain fields, and HarmonyX
        // warn-logs EVERY AccessTools.Property miss - the old property-first, uncached probe
        // emitted 2 warning lines every ~10s (most of LogOutput.log by volume). Field is now
        // tried FIRST, and the resolved MemberInfo (or the miss itself) is cached so each
        // member is probed exactly once per process. Behaviour on success is unchanged.
        static readonly Dictionary<string, MemberInfo> _reflectCache = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
        internal static object ReflectGet(object o, string name)
        {
            try
            {
                if (o == null) return null;
                var t = o.GetType();
                string key = t.FullName + "::" + name;
                MemberInfo mi;
                if (!_reflectCache.TryGetValue(key, out mi))
                {
                    mi = (MemberInfo)AccessTools.Field(t, name) ?? AccessTools.Property(t, name);
                    _reflectCache[key] = mi;   // caches the miss (null) too -> one probe, one possible warning, ever
                }
                if (mi is FieldInfo f) return f.GetValue(o);
                if (mi is PropertyInfo p && p.GetMethod != null) return p.GetValue(o);
                return null;
            }
            catch { return null; }
        }

        // RUNTIME HASH RESOLUTION (1.1.28, F2): the game's generated UserCode_* RPC bodies carry a
        // signature hash in their NAME (e.g. UserCode_CmdSetDestination_1791143641) that churns on
        // ANY signature touch, so a hardcoded hash is a time bomb every update. Every hashed target
        // is now resolved at patch time by PREFIX SCAN instead - exactly one match = the target;
        // 0 or >1 = return null, which makes that class's Patch() throw and land in the F1 isolation
        // catch as a logged skip (that one feature degrades, everything else applies).
        internal static MethodBase ResolveUserCode(Type type, string prefix)
        {
            MethodBase hit = null; int n = 0;
            foreach (var m in AccessTools.GetDeclaredMethods(type))
                if (m.Name.StartsWith(prefix, StringComparison.Ordinal)) { hit = m; n++; }
            if (n == 1) { Log?.LogInfo($"[patch] {type.Name}.{prefix}* -> {hit.Name}"); return hit; }
            Log?.LogError($"[patch] {type.Name}.{prefix}* matched {n} method(s) - patch skipped");
            return null;   // class processor throws -> the F1 isolation catch logs the skip
        }

        // ---- SERVER FRAMETIME sampler (contract [FRAMETIME]). Sample real per-frame delta on the per-frame
        // PeriodicTick pump; publish a ~1s smoothed EMA in ms on the {"t":"net"} telemetry line. A tick GAP
        // (>5000ms mission transition) is NOT a frame and restarts the accumulator so it can't fake a spike.
        internal static float SrvFrameMs;   // smoothed frametime (ms); 0 = no data yet
        static float _pfLast, _pfEma;
        internal static void PerfTick()
        {
            try
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (_pfLast > 0f)
                {
                    float dt = (now - _pfLast) * 1000f;
                    if (dt > 5000f) { _pfLast = now; _pfEma = 0f; SrvFrameMs = 0f; return; }   // tick gap -> restart
                    _pfEma = _pfEma <= 0f ? dt : _pfEma + 0.1f * (dt - _pfEma);                 // EMA (~0.1 alpha)
                    SrvFrameMs = _pfEma;
                }
                _pfLast = now;
            }
            catch { }
        }

        // Connection-health line + optional per-player RTT. Omits rtt_ms until a sample lands.
        internal static void NetHealthTick()
        {
            try
            {
                var humans = Humans();
                if (humans.Count == 0) return;
                float now = Time.time;
                float elapsed = _netEmitElapsedAnchor < 0f ? 1f : Mathf.Max(0.5f, now - _netEmitElapsedAnchor);
                _netEmitElapsedAnchor = now;

                var sb = new StringBuilder(256);
                sb.Append("{\"t\":\"net\",\"p\":[");
                bool first = true;
                foreach (var p in humans)
                {
                    string id = Sid(p);
                    if (string.IsNullOrEmpty(id) || id == "0") continue;
                    int orders = _netOrders.TryGetValue(id, out var o2) ? o2 : 0;
                    int drops  = _netDrops.TryGetValue(id, out var d2) ? d2 : 0;
                    int ordPerSec = (int)(orders / elapsed);
                    int streak = _griefStreak.TryGetValue(id, out var st) ? st : 0;
                    int sbDc   = _dcCount.TryGetValue(id, out var dc) ? dc : 0;
                    string lastDc = _dcReason.TryGetValue(id, out var dr) ? dr : "";
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":\"").Append(id).Append("\",\"ord\":").Append(ordPerSec)
                      .Append(",\"drop\":").Append(drops)
                      .Append(",\"streak\":").Append(streak)
                      .Append(",\"sbDc\":").Append(sbDc)
                      .Append(",\"lastDc\":\"").Append(Esc(lastDc)).Append("\"");
                    // Prefer EMA for UI stability; fall back to latest raw sample.
                    float rttShow = 0f;
                    if (_rttEma.TryGetValue(id, out var ema) && ema > 0f) rttShow = ema;
                    else if (_rttLatest.TryGetValue(id, out var raw) && raw > 0f) rttShow = raw;
                    if (rttShow > 0f)
                        sb.Append(",\"rtt_ms\":").Append(Mathf.RoundToInt(rttShow).ToString(CultureInfo.InvariantCulture));
                    sb.Append('}');
                }
                sb.Append("],\"deadNet\":").Append(_deadNetDrops)
                  .Append(",\"bufCap\":").Append(ProbeSendBufferCap())
                  .Append(",\"frametime_ms\":").Append(SrvFrameMs.ToString("0.0", CultureInfo.InvariantCulture)).Append('}');
                Out(sb.ToString());
                _netOrders.Clear(); _netDrops.Clear();   // reset per-emit tallies (forced-DC tallies persist for the panel)
            }
            catch (Exception e) { Log?.LogError("NetHealthTick: " + e); }
        }

        // ======================= Per-player RTT (Steam HostPing-class; Notify fallback) =======================
        // NOT NetworkTime.Rtt (client-fed, ~0 on headless).
        // Preferred: Steamworks GetConnectionRealTimeStatus.m_nPing via Mirage SteamConnection.ConnId —
        // same transport ping family as lobby HostPing / server-list (tens of ms).
        // Fallback: raw IConnection.SendNotify (1-byte, NOT NetworkPingMessage) → OnDelivered, minus one
        // server-frame bias. Notify alone inflates badly: ACK is piggybacked on the client's next
        // outbound (or empty-ack timeout) + Unity tick scheduling — Tomo on 1.1.7 saw ~94ms vs ~16–25ms list.
        // Fail-soft: Steam miss → Notify; Notify hard-fail → disable Notify only (Steam may still work).
        const float RttProbeIntervalSec = 2.0f;
        const float RttEmaAlpha = 0.45f;
        const float RttNotifyMaxFrameBiasMs = 33f;
        static readonly byte[] RttNotifyPayload = { 0 };
        static readonly Dictionary<string, float> _rttLatest = new Dictionary<string, float>();
        static readonly Dictionary<string, float> _rttEma = new Dictionary<string, float>();
        static readonly Dictionary<string, float> _rttLastProbe = new Dictionary<string, float>();
        static readonly HashSet<string> _rttInFlight = new HashSet<string>();
        static bool _rttNotifyDisabled;
        static bool _rttNotifyLogged;
        static bool _rttSteamReadyLogged;
        static bool _rttSteamResolveDone;
        static bool _rttSteamUnavailable;
        static Type _rttSteamConnT;
        static Type _rttSteamStatusT;
        static FieldInfo _rttSteamConnIdField;
        static FieldInfo _rttSteamPingField;
        static MethodInfo _rttSteamGsGetStatus;
        static MethodInfo _rttSteamGetStatus;

        // Lightweight callback; one allocation per in-flight Notify probe (interval is seconds, not per-frame).
        sealed class RttNotifyCallback : INotifyCallBack
        {
            public string Sid;
            public float SentAt;
            public void OnDelivered()
            {
                try { NoteRttNotifyDelivered(Sid, SentAt); }
                catch { }
            }
            public void OnLost()
            {
                try { NoteRttLost(Sid); }
                catch { }
            }
        }

        static void NoteRttSample(string sid, float ms)
        {
            if (string.IsNullOrEmpty(sid) || sid == "0") return;
            if (ms < 1f || ms > 30000f) return;
            _rttLatest[sid] = ms;
            if (!_rttEma.TryGetValue(sid, out var ema) || ema <= 0f)
                _rttEma[sid] = ms;
            else
                _rttEma[sid] = ema + RttEmaAlpha * (ms - ema);
        }

        internal static void NoteRttNotifyDelivered(string sid, float sentAt)
        {
            try
            {
                if (!string.IsNullOrEmpty(sid)) _rttInFlight.Remove(sid);
                if (string.IsNullOrEmpty(sid) || sid == "0") return;
                float raw = (Time.realtimeSinceStartup - sentAt) * 1000f;
                if (raw < 1f || raw > 30000f) return;
                // ACK is observed on a later server tick — subtract one frametime (capped), not half-RTT.
                float bias = Mathf.Clamp(SrvFrameMs, 0f, RttNotifyMaxFrameBiasMs);
                NoteRttSample(sid, Mathf.Max(1f, raw - bias));
            }
            catch { }
        }

        internal static void NoteRttLost(string sid)
        {
            try { if (!string.IsNullOrEmpty(sid)) _rttInFlight.Remove(sid); }
            catch { }
        }

        static void EnsureSteamRttResolved()
        {
            if (_rttSteamResolveDone) return;
            _rttSteamResolveDone = true;
            try
            {
                _rttSteamConnT = AccessTools.TypeByName("Mirage.SteamworksSocket.SteamConnection");
                _rttSteamStatusT = AccessTools.TypeByName("Steamworks.SteamNetConnectionRealTimeStatus_t");
                var gsT = AccessTools.TypeByName("Steamworks.SteamGameServerNetworkingSockets");
                var clT = AccessTools.TypeByName("Steamworks.SteamNetworkingSockets");
                if (_rttSteamConnT == null || _rttSteamStatusT == null || (gsT == null && clT == null))
                {
                    _rttSteamUnavailable = true;
                    Log?.LogWarning("[rtt] Steam ping types missing — using Notify fallback only");
                    return;
                }
                _rttSteamConnIdField = AccessTools.Field(_rttSteamConnT, "ConnId");
                _rttSteamPingField = AccessTools.Field(_rttSteamStatusT, "m_nPing");
                if (gsT != null) _rttSteamGsGetStatus = FindSteamGetConnectionRealTimeStatus(gsT);
                if (clT != null) _rttSteamGetStatus = FindSteamGetConnectionRealTimeStatus(clT);
                if (_rttSteamConnIdField == null || _rttSteamPingField == null
                    || (_rttSteamGsGetStatus == null && _rttSteamGetStatus == null))
                {
                    _rttSteamUnavailable = true;
                    Log?.LogWarning("[rtt] Steam GetConnectionRealTimeStatus unresolved — Notify fallback only");
                    return;
                }
                if (!_rttSteamReadyLogged)
                {
                    _rttSteamReadyLogged = true;
                    Log?.LogInfo("[rtt] Steam GetConnectionRealTimeStatus path ready (HostPing-class m_nPing)");
                }
            }
            catch (Exception e)
            {
                _rttSteamUnavailable = true;
                Log?.LogWarning("[rtt] Steam ping resolve failed — Notify fallback only ("
                    + e.GetBaseException().Message + ")");
            }
        }

        static MethodInfo FindSteamGetConnectionRealTimeStatus(Type t)
        {
            try
            {
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "GetConnectionRealTimeStatus") continue;
                    var ps = m.GetParameters();
                    if (ps.Length >= 2) return m;
                }
            }
            catch { }
            return AccessTools.Method(t, "GetConnectionRealTimeStatus");
        }

        static bool SteamResultOk(object result)
        {
            if (result == null) return false;
            try
            {
                if (result is int i) return i == 1;                 // EResult.k_EResultOK
                if (result is Enum) return Convert.ToInt32(result) == 1;
                string s = result.ToString();
                return s == "k_EResultOK" || s == "OK";
            }
            catch { return false; }
        }

        // Read Steam transport ping for this player's Mirage SteamConnection handle. False = use Notify.
        static bool TryReadSteamRttMs(Player p, out float ms)
        {
            ms = 0f;
            EnsureSteamRttResolved();
            if (_rttSteamUnavailable) return false;
            try
            {
                if (!(p?.Owner is NetworkPlayer np)) return false;
                object handle = ReflectGet(np, "ConnectionHandle");
                if (handle == null || _rttSteamConnT == null || !_rttSteamConnT.IsInstanceOfType(handle))
                    return false;
                object connId = _rttSteamConnIdField.GetValue(handle);
                if (connId == null) return false;

                // Dedicated servers use SteamGameServerNetworkingSockets; fall back to client API.
                if (InvokeSteamRealTimeStatus(_rttSteamGsGetStatus, connId, out int ping)
                    || InvokeSteamRealTimeStatus(_rttSteamGetStatus, connId, out ping))
                {
                    if (ping < 1 || ping > 30000) return false;
                    ms = ping;
                    return true;
                }
            }
            catch { }
            return false;
        }

        static bool InvokeSteamRealTimeStatus(MethodInfo method, object connId, out int ping)
        {
            ping = 0;
            if (method == null || _rttSteamStatusT == null || _rttSteamPingField == null) return false;
            try
            {
                var ps = method.GetParameters();
                if (ps.Length < 2) return false;
                object status = Activator.CreateInstance(_rttSteamStatusT);
                var args = new object[ps.Length];
                args[0] = connId;
                args[1] = status;
                if (ps.Length > 2) args[2] = 0;          // nLanes
                if (ps.Length > 3) args[3] = null;       // pLanes
                object result = method.Invoke(null, args);
                if (!SteamResultOk(result)) return false;
                status = args[1] ?? status;
                ping = Convert.ToInt32(_rttSteamPingField.GetValue(status));
                return ping > 0;
            }
            catch { return false; }
        }

        internal static void RttProbeTick()
        {
            try
            {
                var humans = Humans();
                if (humans.Count == 0) return;
                float now = Time.realtimeSinceStartup;
                foreach (var p in humans)
                {
                    string id = Sid(p);
                    if (string.IsNullOrEmpty(id) || id == "0") continue;
                    float last = _rttLastProbe.TryGetValue(id, out var lp) ? lp : -999f;
                    if (now - last < RttProbeIntervalSec) continue;

                    // 1) Steam transport ping (matches server-list / HostPing ballpark)
                    if (TryReadSteamRttMs(p, out float steamMs))
                    {
                        NoteRttSample(id, steamMs);
                        _rttLastProbe[id] = now;
                        continue;
                    }

                    // 2) Notify fallback (raw socket notify — no NetworkPingMessage / no app handler)
                    if (_rttNotifyDisabled) continue;
                    if (_rttInFlight.Contains(id)) continue;
                    if (!TrySendRttNotifyProbe(p, id)) return;   // hard failure disables Notify globally
                    _rttLastProbe[id] = now;
                }
            }
            catch (Exception e) { Log?.LogError("RttProbeTick: " + e); }
        }

        static bool TrySendRttNotifyProbe(Player p, string sid)
        {
            try
            {
                if (p?.Owner == null) return true;   // soft skip — player not networked yet
                if (!(p.Owner is NetworkPlayer np))
                {
                    DisableRttNotify("Owner is not NetworkPlayer (" + (p.Owner.GetType()?.FullName ?? "?") + ")");
                    return false;
                }
                object connObj = ReflectGet(np, "Connection");
                if (!(connObj is IConnection conn))
                    return true;   // soft skip — connection not ready
                _rttInFlight.Add(sid);
                // Clock starts immediately before wire notify (excludes earlier probe-tick queue wait).
                float wireAt = Time.realtimeSinceStartup;
                conn.SendNotify(RttNotifyPayload, new RttNotifyCallback { Sid = sid, SentAt = wireAt });
                return true;
            }
            catch (Exception e)
            {
                try { _rttInFlight.Remove(sid); } catch { }
                DisableRttNotify(e.GetBaseException().Message);
                return false;
            }
        }

        static void DisableRttNotify(string why)
        {
            if (_rttNotifyDisabled) return;
            _rttNotifyDisabled = true;
            if (!_rttNotifyLogged)
            {
                _rttNotifyLogged = true;
                Log?.LogWarning("[rtt] Notify probe unavailable — Steam ping still tried; Notify disabled (" + why + ")");
            }
        }

        static void PruneRttState(HashSet<string> present)
        {
            try
            {
                void prune(Dictionary<string, float> d)
                {
                    foreach (var k in new List<string>(d.Keys))
                        if (!present.Contains(k)) d.Remove(k);
                }
                prune(_rttLatest); prune(_rttEma); prune(_rttLastProbe);
                foreach (var k in new List<string>(_rttInFlight))
                    if (!present.Contains(k)) _rttInFlight.Remove(k);
            }
            catch { }
        }

        // ONE-OFF RTT-reachability probe. Pure read-only reflection; emits to LogOutput.log only, never to players.
        static int _netProbeRuns; static bool _netProbeDone;
        internal static void NetProbe()
        {
            try
            {
                if (DiagNetProbe == null || !DiagNetProbe.Value || _netProbeDone) return;
                var humans = Humans();
                if (humans.Count == 0) return;
                if (_netProbeRuns++ >= 3) { _netProbeDone = true; return; }   // a few snapshots then stop (throttle)

                Player p = humans[0];
                object owner = ReflectGet(p, "Owner");                        // INetworkPlayer
                Log?.LogInfo($"[netprobe] run #{_netProbeRuns}: NetworkTime.Rtt={ProbeRttString()} (expect ~0 on a headless server) bufCap={ProbeSendBufferCap()}");
                if (owner == null) { Log?.LogInfo("[netprobe] Owner is null (no INetworkPlayer); cannot reach a connection object"); return; }
                Log?.LogInfo($"[netprobe] Owner concrete type: {owner.GetType().FullName}");
                object conn = ReflectGet(owner, "Connection");               // IConnection (Mirror/Mirage fork-specific)
                if (conn == null) { DumpMembers("Owner", owner, 0); return; } // no Connection member -> dump the player object itself
                DumpMembers("Connection", conn, 0);
                _netProbeDone = (_netProbeRuns >= 3);
            }
            catch (Exception e) { Log?.LogError("NetProbe: " + e); }
        }

        // Log every numeric/string field & property of `o`; recurse ONE level into any AckSystem-ish member.
        static void DumpMembers(string label, object o, int depth)
        {
            try
            {
                if (o == null) { Log?.LogInfo($"[netprobe] {label}: <null>"); return; }
                var t = o.GetType();
                Log?.LogInfo($"[netprobe] {label} type={t.FullName} (depth {depth})");
                const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var f in t.GetFields(BF))
                {
                    try { object v = f.GetValue(o); ProbeMember(label, f.Name, f.FieldType, v, depth); }
                    catch { }
                }
                foreach (var pr in t.GetProperties(BF))
                {
                    try
                    {
                        if (pr.GetMethod == null || pr.GetIndexParameters().Length > 0) continue;
                        object v = pr.GetValue(o);
                        ProbeMember(label, pr.Name, pr.PropertyType, v, depth);
                    }
                    catch { }
                }
            }
            catch (Exception e) { Log?.LogError("DumpMembers: " + e); }
        }

        static void ProbeMember(string label, string name, Type type, object v, int depth)
        {
            try
            {
                string tn = type != null ? type.Name : "?";
                bool ackish = (tn.IndexOf("AckSystem", StringComparison.OrdinalIgnoreCase) >= 0)
                              || name.StartsWith("ack", StringComparison.OrdinalIgnoreCase)
                              || name.IndexOf("AckSystem", StringComparison.OrdinalIgnoreCase) >= 0;
                if (v == null) { Log?.LogInfo($"[netprobe]   {label}.{name} ({tn}) = null"); return; }
                if (type != null && (type.IsPrimitive || type.IsEnum || v is string || v is decimal))
                    Log?.LogInfo($"[netprobe]   {label}.{name} ({tn}) = {System.Convert.ToString(v, CultureInfo.InvariantCulture)}");
                else
                    Log?.LogInfo($"[netprobe]   {label}.{name} ({tn}) = <object>");
                if (ackish && depth < 1)
                    DumpMembers(label + "." + name, v, depth + 1);
            }
            catch { }
        }

        // Map a Mirage IConnection (the arg to Peer_OnDisconnected) to the SteamID of a CURRENT player, by
        // reflectively comparing each online player's Owner.Connection. Read-only; "" if no live match.
        internal static string SidForConnection(object conn)
        {
            try
            {
                if (conn == null) return "";
                foreach (var p in Humans())
                {
                    try
                    {
                        object owner = ReflectGet(p, "Owner");
                        object pConn = owner != null ? ReflectGet(owner, "Connection") : null;
                        if (pConn != null && ReferenceEquals(pConn, conn)) return Sid(p);
                    }
                    catch { }
                }
            }
            catch { }
            return "";   // player already removed from the lookup by the time we run -> unmapped (telemetry just omits it)
        }

        // Record a forced disconnect (count + last reason) for the {"t":"net"} telemetry line. Called from DcReasonPatch.
        internal static void NoteForcedDc(string sid, string reason)
        {
            try
            {
                if (string.IsNullOrEmpty(sid) || sid == "0") return;
                _dcCount[sid] = (_dcCount.TryGetValue(sid, out var c) ? c : 0) + 1;
                _dcReason[sid] = reason ?? "";
            }
            catch { }
        }

        // Layer B bookkeeping: count silently-dropped dead-netId ServerRpcs (the log/alloc amplifier),
        // surfaced occasionally so admins can see the guard working without re-introducing the spam.
        static int _deadNetDrops; static float _deadNetLog = -999f;
        internal static void NoteDeadNetIdDrop()
        {
            try
            {
                _deadNetDrops++;
                float now = Time.time;
                if (now - _deadNetLog > 30f)
                {
                    _deadNetLog = now;
                    Log?.LogInfo($"[flood] dead-netId ServerRpc drops so far: {_deadNetDrops} (silently absorbed; no log/alloc storm)");
                }
            }
            catch { }
        }

        // ---- STALE-netId exploit strikes (guard B part 2, 1.2.0) ----
        // Mirage keeps its own 5s RecentlyDestroyed list, so guard B can tell two very different things apart:
        //   * netId IS on that list  -> "my unit died while my order was in flight". INNOCENT: dropped silently
        //     as before, never counted here. This is the ONLY case ordinary play produces.
        //   * netId is NOT on it     -> the sender is commanding/firing at an id the server has no record of at
        //     all: the classic order-a-dead-unit exploit (and the amplifier that exhausts the ByteBuffer pool).
        // Only the second case earns strikes. It still has legitimate-ish sources (a mission rotation invalidates
        // every netId at once, and a lagged client can flush a queue after the 5s grace), so: a threshold rather
        // than one strike, a 30s grace after every mission change, a distinct-player storm breaker, admin exemption,
        // and Grief.ReportOnly support. The evidence is always logged with the action.
        static readonly Dictionary<string, (float since, float last, int hits, uint lastNetId)> _staleNetStrikes =
            new Dictionary<string, (float, float, int, uint)>(StringComparer.Ordinal);
        static readonly Dictionary<string, float> _staleNetActed = new Dictionary<string, float>(StringComparer.Ordinal);
        static float _staleNetGraceUntil = -999f;   // suppressed until this time (set on mission change / plugin start)
        static float _staleNetStormAt = -999f;
        const float STALE_NET_WINDOW = 10f;         // strikes must land within this rolling window

        // Called on every mission change (AdvanceGame): every netId in the world has just been invalidated, so a
        // client's in-flight orders will legitimately reference ids the server no longer knows.
        internal static void OpenStaleNetGrace(float seconds)
        {
            try { _staleNetGraceUntil = Time.time + Mathf.Max(0f, seconds); } catch { }
        }

        // Note ONE stale-netId RPC from `sender` (HandleRpc's INetworkPlayer). Kicks after
        // Flood.DeadNetIdKickStrikes strikes inside STALE_NET_WINDOW. Fully wrapped; never throws into the netcode.
        internal static void NoteStaleNetIdRpc(object sender, uint netId)
        {
            try
            {
                int strikes = FloodDeadNetIdKickStrikes != null ? FloodDeadNetIdKickStrikes.Value : 0;
                if (strikes <= 0) return;                                  // feature off -> silent drop only
                float now; try { now = Time.time; } catch { return; }
                if (now < _staleNetGraceUntil) return;                     // mission change / startup grace
                var np = sender as INetworkPlayer;
                if (np == null || !np.TryGetPlayer<Player>(out Player p) || p == null) return;   // can't attribute -> never punish
                string sid = Sid(p);
                if (string.IsNullOrEmpty(sid) || sid == "0") return;

                if (!_staleNetStrikes.TryGetValue(sid, out var st) || now - st.since > STALE_NET_WINDOW)
                    st = (now, now, 0, netId);
                st = (st.since, now, st.hits + 1, netId);
                _staleNetStrikes[sid] = st;
                if (_staleNetStrikes.Count > 128)                          // bounded: prune windows that have expired
                {
                    var dead = new List<string>();
                    foreach (var kv in _staleNetStrikes) if (now - kv.Value.last > STALE_NET_WINDOW * 2f) dead.Add(kv.Key);
                    foreach (var k in dead) _staleNetStrikes.Remove(k);
                }
                if (st.hits < strikes) return;
                if (_staleNetActed.TryGetValue(sid, out var at) && now - at < 15f) return;   // throttle re-acting
                if (GriefExemptAdmins == null || GriefExemptAdmins.Value) { if (IsAdmin(p)) return; }

                // storm breaker: a rotation artifact or server-wide desync hits many players at once, not one griefer
                int stormers = 0;
                foreach (var kv in _staleNetStrikes)
                    if (now - kv.Value.last <= STALE_NET_WINDOW && kv.Value.hits >= strikes) stormers++;
                bool storm = stormers >= 3;

                _staleNetActed[sid] = now;
                _staleNetStrikes.Remove(sid);
                string nm = RawNameOf(p);
                bool reportOnly = GriefReportOnly != null && GriefReportOnly.Value;
                string action = (reportOnly || storm) ? "report" : "kick";
                // EVIDENCE for the action taken (always logged, kick or report)
                if (storm)
                {
                    if (now - _staleNetStormAt > 30f)
                    {
                        _staleNetStormAt = now;
                        Log?.LogWarning($"[flood] stale-netId STORM: {stormers} players sending unknown netIds at once "
                            + "-> treated as a rotation/desync artifact; auto-kicks SUPPRESSED");
                    }
                }
                else
                    Log?.LogWarning($"[flood] {action} {nm} ({sid}) — {st.hits} ServerRpcs to netIds the server has no record of "
                        + $"within {(now - st.since):0.0}s (last netId {netId}); NOT on Mirage's recently-destroyed list, "
                        + $"so this is not an order that raced a unit's death. Threshold {strikes}.");

                Out("{\"t\":\"report\",\"id\":\"" + sid + "\",\"n\":\"" + Esc(nm)
                    + "\",\"reason\":\"" + (storm ? "dead-unit commands (SUPPRESSED: many players at once, likely a rotation artifact)"
                                                  : "dead-unit commands (" + st.hits + " RPCs to unknown netIds) - server protection")
                    + "\",\"count\":" + st.hits + ",\"rate\":0,\"action\":\"" + action
                    + "\",\"rejoin\":" + (action == "kick" ? "true" : "false") + ",\"ts\":0}");
                if (action != "kick") return;
                try { Instance?.TellPlayer(p, "<color=#FF0000>Removed: commanding destroyed units (server protection).</color>"); } catch { }
                _tkKicks.Add(new KeyValuePair<string, float>(sid, now + 0.5f));   // drained by TkTick on the main thread
            }
            catch (Exception e) { Log?.LogError("NoteStaleNetIdRpc: " + e); }
        }

        void Update() { PeriodicTick(); }   // a no-op if this object never ticks

        // -------- player enumeration (humans only; SteamID filters out AI/unjoined) --------
        static string Sid(Player p) { try { return p.SteamID.ToString(); } catch { return ""; } }
        // internal shims for the patch classes (Sid/Humans are private by design)

        // PERF: FindObjectsOfType<Player> is expensive and Humans() is called many times
        // per HQ tick (LifeTick/TkTick/PollCommands/FindPlayerBySid/snapshot/balance...).
        // Cache it for the current frame (Time.time is constant within a frame) so the
        // scene scan runs once per frame instead of a dozen+ times.
        static List<Player> _humansCache;
        static float _humansCacheTime = -1f;
        static List<Player> Humans()
        {
            float now = Time.time;
            if (_humansCache != null && _humansCacheTime == now) return _humansCache;
            var ok = new List<Player>();
            // Use the GAME's own player registry (UnitRegistry.playerLookup) - the same source
            // ChatManager uses for chat delivery, so these Player objects have a valid .Owner
            // (FindObjectsOfType returned copies whose .Owner was null in the poll context, so
            // whispers/TellPlayer silently no-op'd). Fall back to a scene scan if it's empty.
            try
            {
                foreach (var p in UnitRegistry.playerLookup.Values)
                {
                    if (p == null) continue;
                    string id = Sid(p);
                    if (!string.IsNullOrEmpty(id) && id != "0") ok.Add(p);
                }
            }
            catch (Exception e) { Log?.LogError("Humans/playerLookup: " + e); }
            if (ok.Count == 0)
                foreach (var p in UnityEngine.Object.FindObjectsOfType<Player>())
                {
                    if (p == null) continue;
                    string id = Sid(p);
                    if (!string.IsNullOrEmpty(id) && id != "0") ok.Add(p);
                }
            _humansCache = ok; _humansCacheTime = now;
            return ok;
        }

        static string Fac(Player p)
        {
            try { return p.HQ != null && p.HQ.faction != null ? p.HQ.faction.factionName : ""; }
            catch { return ""; }
        }

        // Short aircraft designator from AircraftDefinition: prefer definition.code
        // (KR-67 / FS-12 / CI-22) over the long nickname (Ifrit / Revoker / Cricket).
        static string PlaneDesignator(AircraftDefinition d)
        {
            if (d == null) return "";
            try
            {
                string co = (d.code ?? "").Trim();
                if (!string.IsNullOrEmpty(co)) return co;
            }
            catch { }
            try
            {
                string un = (d.unitName ?? "").Trim();
                if (!string.IsNullOrEmpty(un)) return un;
            }
            catch { }
            return "";
        }

        // The plane the player is currently in (their live Aircraft), or the airframe
        // they have selected if not spawned. Empty string => in menu / between spawns.
        // Returns the short designator (code) for POS space savings.
        static string Plane(Player p)
        {
            try
            {
                var ac = p.Aircraft;
                if (ac != null)
                {
                    string c = PlaneDesignator(ac.definition);
                    if (!string.IsNullOrEmpty(c)) return c;
                }
            }
            catch { }
            try
            {
                var af = p.AirframeInUse;            // OwnedAirframe? - selected airframe
                if (af.HasValue && af.Value.Definition != null)
                {
                    string c = PlaneDesignator(af.Value.Definition);
                    if (!string.IsNullOrEmpty(c)) return c;
                }
            }
            catch { }
            return "";
        }

        // -------- emit [NOSTATS] lines --------
        // Use UnityEngine.Debug.Log so the line lands in Unity's -logFile
        // (/logs/console.log) which the external bot tails. (Console.WriteLine only
        // reaches process stdout, NOT the -logFile, so the bot wouldn't see it.)
        static void Out(string json) => Debug.Log("[NOSTATS] " + json);

        internal static void EmitAll(string type)
        {
            try
            {
                foreach (var p in Humans()) EmitOne(p, type);
                if (type == "end") Out("{\"t\":\"end\"}");
            }
            catch (Exception e) { Log?.LogError("EmitAll: " + e); }
        }

        internal static void EmitOne(Player p, string type)
        {
            Trace("EmitOne");
            if (p == null) return;
            try
            {
                string id = Sid(p);
                if (string.IsNullOrEmpty(id) || id == "0") return;
                var sb = new StringBuilder(160);
                sb.Append("{\"t\":\"").Append(type).Append("\",\"id\":\"").Append(id).Append("\"");
                sb.Append(",\"n\":\"").Append(Esc(RawNameOf(p))).Append("\"");
                sb.Append(",\"f\":\"").Append(Esc(Fac(p))).Append("\"");
                sb.Append(",\"s\":").Append(Num(p.PlayerScore));
                sb.Append(",\"rk\":").Append(Num(p.PlayerRank));
                sb.Append(",\"tk\":").Append(Num(p.Teamkills));
                sb.Append(",\"ac\":\"").Append(Esc(Plane(p))).Append("\"");
                sb.Append('}');
                Out(sb.ToString());
            }
            catch (Exception e) { Log?.LogError("EmitOne: " + e); }
        }

        // -------- live map: fast (~0.5s) position tick. One compact line of every FLYING player's
        // world x/z + heading. Cheap enough to feel near-real-time; the webcc glides short deltas only.
        // Players marked map-dead (just died/ejected) skip POS while on a disabled wreck so the
        // bot DOWNED flag is not cleared by corpse tracking. Cleared by: (1) Aircraft-null gap
        // then a new airframe, or (2) a live (!disabled) airframe after a short lockout — some
        // respawns never observe a null gap in LifeTick and used to stick map-dead forever
        // (WebCC false ✝ + frozen blip). EnrichPos g=landed is unrelated to map-dead.
        // When Anomaly.EnrichPos (default true), also emit y / ac / g (ac feeds the bot's
        // airframe lookups). Legacy consumers ignore unknown keys.
        // 1.0.30+: top-level emit-time unix `ts`. --------
        static float _nextPos;
        static readonly HashSet<string> _mapDead = new HashSet<string>(StringComparer.Ordinal);
        static readonly HashSet<string> _mapDeadSawGap = new HashSet<string>(StringComparer.Ordinal); // null-aircraft seen after death
        static readonly Dictionary<string, float> _mapDeadSince = new Dictionary<string, float>(StringComparer.Ordinal);
        const float MapDeadLiveAcUnlock = 2.5f; // seconds: live airframe clears map-dead without null gap
        // Heading degrees (0 = +Z / north on the WebCC map, clockwise toward +X). -1 = unknown.
        static int HeadingDeg(UnityEngine.Component c)
        {
            try
            {
                Vector3 f = c.transform.forward;
                float deg = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
                int h = ((int)Mathf.Round(deg)) % 360;
                if (h < 0) h += 360;
                return h;
            }
            catch { return -1; }
        }
        static void ClearMapDead(string sid)
        {
            _mapDead.Remove(sid);
            _mapDeadSawGap.Remove(sid);
            _mapDeadSince.Remove(sid);
        }
        internal static void PosTick()
        {
            float now = Time.time;
            if (now < _nextPos) return;
            // 0.5 Hz (every 2s). Was 2 Hz (+0.5s); map trail still smooths via bot POS_TRAIL lerp.
            _nextPos = now + 2f;
            try
            {
                // Emit-time unix seconds (invariant). Bot prefers this over ingest wall-clock so a
                // stalled SFTP/RCMD burst does not compress several 0.5s samples into dt≈0.05.
                string emitTs = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0)
                    .ToString("0.###", CultureInfo.InvariantCulture);
                bool enrich = AnomalyEnrichPos == null || AnomalyEnrichPos.Value;
                var sb = new StringBuilder(256);
                sb.Append("{\"t\":\"pos\",\"ts\":").Append(emitTs).Append(",\"p\":[");
                bool first = true;
                foreach (var p in Humans())
                {
                    Aircraft ac = null; try { ac = p.Aircraft; } catch { }
                    if (ac == null) continue;                              // only Occupied players
                    bool disabled = false;
                    try { disabled = ac.disabled; } catch { disabled = true; }
                    if (disabled) continue;                                // skip mid-despawn wrecks
                    string id = Sid(p);
                    if (string.IsNullOrEmpty(id) || id == "0") continue;
                    if (_mapDead.Contains(id))
                    {
                        // Wreck already filtered (disabled). Live airframe after lockout = new sortie
                        // even when LifeTick never saw Aircraft==null (gap miss → stuck false-dead).
                        if (!_mapDeadSince.TryGetValue(id, out float since) || (now - since) < MapDeadLiveAcUnlock)
                            continue;
                        ClearMapDead(id);
                    }
                    var gp = ac.GlobalPosition();
                    int hdg = HeadingDeg(ac);
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":\"").Append(id).Append("\",\"x\":").Append((int)gp.x).Append(",\"z\":").Append((int)gp.z)
                      .Append(",\"k\":\"").Append(AcKind(ac)).Append("\"");
                    if (hdg >= 0) sb.Append(",\"h\":").Append(hdg);
                    if (enrich)
                    {
                        // y = altitude m; ac = short designator; g = landed (NOT dead — WebCC must ignore)
                        sb.Append(",\"y\":").Append((int)gp.y);
                        string code = "";
                        try { code = PlaneDesignator(ac.definition); } catch { }
                        if (string.IsNullOrEmpty(code))
                        {
                            try { code = Plane(p); } catch { }
                        }
                        if (!string.IsNullOrEmpty(code))
                            sb.Append(",\"ac\":\"").Append(Esc(code)).Append("\"");
                        sb.Append(",\"g\":").Append(IsGrounded(ac) ? 1 : 0);
                    }
                    sb.Append('}');
                }
                sb.Append("]}");
                Out(sb.ToString());
            }
            catch (Exception e) { Log?.LogError("PosTick: " + e); }
        }

        // ======================= AI AIRCRAFT LIMITER =======================
        // Performance precaution against AI over-spawning / clogging runways. Checked ~every 3s.
        // It ONLY ever removes AI aircraft (ac.Player == null) -- a player is never touched.
        //   A) per-side AI cap: each faction may have at most AiPerTeamCap (32) AI flying.
        //   B) total cap: total aircraft (AI + players, all sides) must not exceed AiTotalCap (64);
        //      when over, AI is removed from the side with the MOST aircraft (never a player).
        //   C) stuck: a GROUNDED AI that hasn't moved > AiStuckRadius for AiStuckSeconds (45s) is
        //      cleared, to free a clogged runway. Independent of the caps.
        // Removal = Aircraft.DisableUnit() (the game's own destroy path -> explode + despawn, synced
        // to clients), falling back to ejection if that ever throws. A per-tick budget smooths the ramp.
        // CRITICAL (1.1.19): never DisableUnit an already-disabled wreck. Aircraft.ServerDisableUnit
        // always calls ReportKilled (unless landed at airbase) even when already disabled, and
        // damageCredit is NOT cleared — so re-DisableUnit re-pays PlayerScore + re-fires KF.
        // Wrecks also linger ~30s in FindObjectsOfType; counting them toward caps made the limiter
        // prefer lowest-alt wrecks every 5s tick → same kill credited ~5× (reed 2026-07-26).
        const int AiMaxRemovalsPerTick = 12;
        static float _nextAiTick;
        sealed class AiTrack { public Vector3 anchor; public float since; }
        static readonly Dictionary<int, AiTrack> _aiStuck = new Dictionary<int, AiTrack>();

        static Vector3 AcPos(Aircraft ac) { try { var g = ac.GlobalPosition(); return new Vector3(g.x, g.y, g.z); } catch { return Vector3.zero; } }
        static float AcAlt(Aircraft ac) { try { return ac.GlobalPosition().y; } catch { return 99999f; } }
        static bool IsGrounded(Aircraft ac) { try { return ac.IsLanded(); } catch { return false; } }
        static bool IsDisabled(Aircraft ac) { try { return ac != null && ac.disabled; } catch { return true; } }

        // Wipe Unit.damageCredit before AiLimit DisableUnit. ServerDisableUnit always calls
        // ReportKilled; with uncleared credit a *first* cull of a live damaged AI still pays
        // PlayerScore once (1.1.19 only stopped re-pay on already-disabled wrecks).
        static void ClearDamageCredit(Unit u)
        {
            try
            {
                if (u == null) return;
                if (_dmgCreditFI == null)
                    _dmgCreditFI = typeof(Unit).GetField("damageCredit", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                object dcRaw = _dmgCreditFI?.GetValue(u);
                if (dcRaw is System.Collections.IDictionary dc) dc.Clear();
            }
            catch { }
        }

        internal static void AiLimitTick()
        {
            // The AI-limiter switch must NOT also switch off the WebCC Team data panel. This tick is the
            // only producer of the "air" telemetry frame (per-side aircraft, score, hangars, helipads),
            // so returning early here used to blank that whole panel the moment an operator turned
            // enforcement off - a legitimate thing to do, since the caps sit well above what the mission
            // files set. When disabled we still gather and still emit; we simply never cull.
            bool enforce = AiLimit != null && AiLimit.Value;
            float now = Time.time;
            if (now < _nextAiTick) return;
            _nextAiTick = now + 5f;   // 5s (was 3s): with mission AI caps now below the 32 limiter cap the
                                      // limiter rarely acts, so this full-scene FindObjectsOfType<Aircraft>
                                      // scan can run less often - fewer frame hitches + less GC. The 45s
                                      // stuck-runway timer means 5s reaction granularity is still fine.
            try
            {
                var sides   = new Dictionary<FactionHQ, List<Aircraft>>();   // every LIVE aircraft, per side
                var aiSides = new Dictionary<FactionHQ, List<Aircraft>>();   // live AI only, per side
                var live = new HashSet<int>();
                foreach (var ac in UnityEngine.Object.FindObjectsOfType<Aircraft>())
                {
                    if (ac == null || IsDisabled(ac)) continue;              // skip wrecks (still in scene ~30s)
                    FactionHQ hq = null; try { hq = ac.NetworkHQ; } catch { }
                    if (hq == null) continue;
                    Player pl = null; try { pl = ac.Player; } catch { }
                    if (!sides.TryGetValue(hq, out var L)) { sides[hq] = L = new List<Aircraft>(); aiSides[hq] = new List<Aircraft>(); }
                    L.Add(ac);
                    if (pl == null)                                          // AI aircraft (no human pilot)
                    {
                        aiSides[hq].Add(ac);
                        int id = ac.GetInstanceID(); live.Add(id);
                        Vector3 pos = AcPos(ac);
                        float r = AiStuckRadius.Value;
                        if (!_aiStuck.TryGetValue(id, out var t)) _aiStuck[id] = new AiTrack { anchor = pos, since = now };
                        else if ((pos - t.anchor).sqrMagnitude > r * r) { t.anchor = pos; t.since = now; }
                    }
                }
                if (_aiStuck.Count > 0)                                      // forget aircraft that no longer exist
                {
                    var goneIds = new List<int>();
                    foreach (var k in _aiStuck.Keys) if (!live.Contains(k)) goneIds.Add(k);
                    foreach (var k in goneIds) _aiStuck.Remove(k);
                }

                var removed = new HashSet<Aircraft>();
                int budget = AiMaxRemovalsPerTick;
                void Remove(Aircraft ac, string why)
                {
                    if (ac == null || budget <= 0 || removed.Contains(ac)) return;
                    if (ac.Player != null) return;                          // SAFETY: never remove a player's aircraft
                    if (IsDisabled(ac)) return;                             // SAFETY: never re-DisableUnit a wreck (re-pays score)
                    removed.Add(ac); budget--;
                    ClearDamageCredit(ac);                                  // 1.1.20: first cull must not pay PlayerScore
                    try { ac.DisableUnit(); }
                    catch (Exception e) { try { ac.StartEjectionSequence(); } catch { } Log?.LogWarning("[ailimit] DisableUnit fell back to eject: " + e.Message); }
                    Log?.LogInfo("[ailimit] cleared AI aircraft (" + why + ")");
                }
                IEnumerable<Aircraft> Removable(List<Aircraft> ai, int n) =>
                    ai.Where(a => a != null && a.Player == null && !IsDisabled(a) && !removed.Contains(a)).OrderBy(AcAlt).Take(n);

                if (!enforce)
                {
                    // Enforcement off: telemetry only. `removed` stays empty, so the panel reports every
                    // live aircraft and nothing is ever culled.
                    EmitAir(sides, aiSides, removed);
                    return;
                }

                // RULE C: stuck grounded AI (independent of the caps)
                int stuckSec = AiStuckSeconds.Value;
                if (stuckSec > 0)
                    foreach (var ai in aiSides.Values)
                        foreach (var ac in ai)
                        {
                            if (ac == null || removed.Contains(ac)) continue;
                            int id = ac.GetInstanceID();
                            if (_aiStuck.TryGetValue(id, out var t) && now - t.since >= stuckSec && IsGrounded(ac))
                            { Remove(ac, "stuck " + stuckSec + "s on the ground"); _aiStuck.Remove(id); }
                        }

                // RULE A: per-side AI cap
                int perCap = AiPerTeamCap.Value;
                if (perCap > 0)
                    foreach (var kv in aiSides)
                    {
                        int n = kv.Value.Count(a => a != null && !removed.Contains(a)) - perCap;
                        if (n > 0) foreach (var ac in Removable(kv.Value, n)) Remove(ac, "team AI cap " + perCap);
                    }

                // RULE B: total aircraft cap -> trim AI from the busiest side (never a player)
                int totalCap = AiTotalCap.Value;
                if (totalCap > 0)
                {
                    int Eff(FactionHQ h) => sides[h].Count(a => a != null && !removed.Contains(a));
                    int total = 0; foreach (var h in sides.Keys) total += Eff(h);
                    while (total > totalCap && budget > 0)
                    {
                        FactionHQ busiest = null; int best = -1;
                        foreach (var h in sides.Keys)
                        {
                            if (!aiSides[h].Any(a => a != null && a.Player == null && !removed.Contains(a))) continue;
                            int e = Eff(h);
                            if (e > best) { best = e; busiest = h; }
                        }
                        if (busiest == null) break;                         // no removable AI anywhere
                        var victim = Removable(aiSides[busiest], 1).FirstOrDefault();
                        if (victim == null) break;
                        Remove(victim, "total cap " + totalCap);
                        total--;
                    }
                }

                EmitAir(sides, aiSides, removed);
                // Bot mute window: USE_PLUGIN_SCORE must not bank PlayerScore climbs from culls.
                if (removed.Count > 0)
                    Out("{\"t\":\"ailimit\",\"n\":" + removed.Count + "}");
                // Entity feed moved to MapEntTick (~1s) so the live map stays responsive even when
                // AiLimit is off / the 5s limiter scan is idle.
            }
            catch (Exception e) { Log?.LogError("AiLimitTick: " + e); }
        }

        // -------- live map entity feed (~1s): AI aircraft + ships with world pos + heading.
        // Independent of AiLimit so the map still gets ships/AI when the limiter is disabled. --------
        static float _nextMapEnt;
        internal static void MapEntTick()
        {
            float now = Time.time;
            if (now < _nextMapEnt) return;
            _nextMapEnt = now + 1f;
            try
            {
                var sides = new Dictionary<FactionHQ, List<Aircraft>>();
                foreach (var ac in UnityEngine.Object.FindObjectsOfType<Aircraft>())
                {
                    if (ac == null) continue;
                    FactionHQ hq = null; try { hq = ac.NetworkHQ; } catch { }
                    if (hq == null) continue;
                    if (!sides.TryGetValue(hq, out var L)) sides[hq] = L = new List<Aircraft>();
                    L.Add(ac);
                }
                EmitEntities(sides, null);
            }
            catch (Exception e) { Log?.LogError("MapEntTick: " + e); }
        }

        // -------- live map entity feed: per-entity world positions for the command-centre map.
        //   "a" = AI aircraft only (ac.Player==null) -> {i,x,z,f,k,g,h}
        //         i=GetInstanceID (client interpolation key), f=faction, k=plane/heli, g=grounded, h=heading deg
        //   "s" = all ships -> {i,x,z,f,c,h} where c=class, h=heading deg
        // Everything is guarded per-unit: a throw skips that one unit, never the whole feed. --------
        static void EmitEntities(Dictionary<FactionHQ, List<Aircraft>> sides, HashSet<Aircraft> removed)
        {
            try
            {
                var sb = new StringBuilder(512);
                sb.Append("{\"t\":\"ent\",\"a\":[");
                bool first = true;
                foreach (var kv in sides)
                {
                    string fn = ""; try { fn = kv.Key.faction != null ? kv.Key.faction.factionName : ""; } catch { }
                    foreach (var ac in kv.Value)
                    {
                        try
                        {
                            if (ac == null || (removed != null && removed.Contains(ac))) continue;
                            if (ac.Player != null) continue;                  // AI aircraft only
                            try { if (ac.disabled) continue; } catch { }      // skip mid-despawn ghosts
                            var gp = ac.GlobalPosition();
                            int hdg = HeadingDeg(ac);
                            if (!first) sb.Append(',');
                            first = false;
                            sb.Append("{\"i\":").Append(ac.GetInstanceID())
                              .Append(",\"x\":").Append((int)gp.x).Append(",\"z\":").Append((int)gp.z)
                              .Append(",\"f\":\"").Append(Esc(fn)).Append("\"")
                              .Append(",\"k\":\"").Append(AcKind(ac)).Append("\"")
                              .Append(",\"g\":").Append(IsGrounded(ac) ? 1 : 0);
                            if (hdg >= 0) sb.Append(",\"h\":").Append(hdg);
                            sb.Append('}');
                        }
                        catch { }                                             // fail-safe: skip this aircraft
                    }
                }
                sb.Append("],\"s\":[");
                first = true;
                foreach (var sh in UnityEngine.Object.FindObjectsOfType<Ship>())
                {
                    try
                    {
                        if (sh == null) continue;
                        try { if (sh.disabled) continue; } catch { }          // skip mid-despawn ghosts
                        FactionHQ hq = null; try { hq = sh.NetworkHQ; } catch { }
                        if (hq == null) continue;                             // skip ships with no side
                        string fn = ""; try { fn = hq.faction != null ? hq.faction.factionName : ""; } catch { }
                        var gp = sh.GlobalPosition();
                        int hdg = HeadingDeg(sh);
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append("{\"i\":").Append(sh.GetInstanceID())
                          .Append(",\"x\":").Append((int)gp.x).Append(",\"z\":").Append((int)gp.z)
                          .Append(",\"f\":\"").Append(Esc(fn)).Append("\"")
                          .Append(",\"c\":\"").Append(ShipClass(sh)).Append("\"");
                        if (hdg >= 0) sb.Append(",\"h\":").Append(hdg);
                        sb.Append('}');
                    }
                    catch { }                                                 // fail-safe: skip this ship
                }
                sb.Append("]}");
                Out(sb.ToString());
            }
            catch (Exception e) { Log?.LogError("EmitEntities: " + e); }
        }

        // plane vs heli, cached per AircraftDefinition. A heli has a CompoundHeloController in
        // its hierarchy; failing that we fall back to a known heli jsonKey set.
        static readonly Dictionary<AircraftDefinition, string> _acKindCache = new Dictionary<AircraftDefinition, string>();
        static readonly HashSet<string> _heliKeys = new HashSet<string> { "AttackHelo1", "QuadVTOL1" };
        static string AcKind(Aircraft ac)
        {
            try
            {
                AircraftDefinition def = null; try { def = ac.definition; } catch { }
                if (def != null && _acKindCache.TryGetValue(def, out var cached)) return cached;
                string kind = "p";
                try { if (ac.GetComponentInChildren<CompoundHeloController>() != null) kind = "h"; } catch { }
                if (kind == "p" && def != null) { try { if (_heliKeys.Contains(def.jsonKey)) kind = "h"; } catch { } }
                if (def != null) _acKindCache[def] = kind;
                return kind;
            }
            catch { return "p"; }
        }

        // ship class string for the map, cached per ShipDefinition.
        static readonly Dictionary<ShipDefinition, string> _shipClassCache = new Dictionary<ShipDefinition, string>();
        static string ShipClass(Ship sh)
        {
            try
            {
                var def = sh.definition as ShipDefinition;
                if (def == null) return "corvette";
                if (_shipClassCache.TryGetValue(def, out var cached)) return cached;
                string cls;
                switch (def.shipType)
                {
                    case ShipType.CV:  case ShipType.LHA: cls = "carrier";   break;
                    case ShipType.DDG:                    cls = "destroyer"; break;
                    case ShipType.FFG:                    cls = "argus";     break;
                    case ShipType.FFL:                    cls = "corvette";  break;
                    // PB = patrol boat, NEW in the 2026-07-27 game update (ShipType gained an 8th
                    // member). Without this case it fell to the default below and every patrol boat
                    // on Escalation / Terminal Control drew as a corvette - present on the map all
                    // along, wearing the wrong silhouette. The co-op missions have none.
                    case ShipType.PB:                     cls = "patrol";    break;
                    case ShipType.LFD: case ShipType.LC:  cls = "cursor";    break;
                    default:                              cls = "corvette";  break;
                }
                _shipClassCache[def] = cls;
                return cls;
            }
            catch { return "corvette"; }
        }

        // live AI/player aircraft counts for the web command centre (per side + totals + caps).
        // Always emit EVERY HQ even at ai=0,pl=0 so WebCC keeps hangar/sp chips for wiped sides.
        static void EmitAir(Dictionary<FactionHQ, List<Aircraft>> sides,
                            Dictionary<FactionHQ, List<Aircraft>> aiSides, HashSet<Aircraft> removed)
        {
            try
            {
                try
                {
                    foreach (var hq in UnityEngine.Object.FindObjectsOfType<FactionHQ>())
                    {
                        if (hq == null || hq.faction == null) continue;
                        if (!sides.ContainsKey(hq))
                        {
                            sides[hq] = new List<Aircraft>();
                            if (!aiSides.ContainsKey(hq)) aiSides[hq] = new List<Aircraft>();
                        }
                        else if (!aiSides.ContainsKey(hq)) aiSides[hq] = new List<Aircraft>();
                    }
                }
                catch { }

                var sb = new StringBuilder(192);
                sb.Append("{\"t\":\"air\",\"s\":[");
                bool first = true; int totAi = 0, totPl = 0;
                foreach (var kv in sides)
                {
                    int ai = 0;
                    try { if (aiSides.TryGetValue(kv.Key, out var aiL) && aiL != null) ai = aiL.Count(a => a != null && !removed.Contains(a)); } catch { }
                    int pl = kv.Value.Count(a => a != null && a.Player != null && !removed.Contains(a));
                    totAi += ai; totPl += pl;
                    string fn = ""; try { fn = kv.Key.faction != null ? kv.Key.faction.factionName : ""; } catch { }
                    if (!first) sb.Append(',');
                    first = false;
                    bool canSpawn = FactionCanSpawn(kv.Key);
                    // Team data for the WebCC panel: this side's in-game score, and the pads it still
                    // holds split by type (a helipad cannot launch a jet, so one number for both would
                    // read as spawn capacity that isn't there).
                    int hgs = 0, hps = 0;
                    try { CountPads(kv.Key, out hgs, out hps); } catch { }
                    double sc = 0; try { sc = kv.Key.factionScore; } catch { }
                    sb.Append("{\"n\":\"").Append(Esc(fn)).Append("\",\"ai\":").Append(ai)
                      .Append(",\"pl\":").Append(pl).Append(",\"sp\":").Append(canSpawn ? 1 : 0)
                      .Append(",\"hg\":").Append(hgs).Append(",\"hp\":").Append(hps)
                      .Append(",\"sc\":").Append(sc.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
                      .Append('}');
                }
                sb.Append("],\"ai\":").Append(totAi).Append(",\"pl\":").Append(totPl)
                  .Append(",\"teamcap\":").Append(AiPerTeamCap.Value).Append(",\"totcap\":").Append(AiTotalCap.Value).Append('}');
                Out(sb.ToString());
            }
            catch (Exception e) { Log?.LogError("EmitAir: " + e); }
        }
        // ===================== end AI AIRCRAFT LIMITER =====================

        // -------- PvP team-balance: the other faction + a per-player message --------
        internal static FactionHQ OtherHQ(FactionHQ target)
        {
            try
            {
                foreach (var hq in UnityEngine.Object.FindObjectsOfType<FactionHQ>())
                    if (hq != null && hq != target) return hq;
            }
            catch { }
            return null;
        }

        internal void TellPlayer(Player p, string msg)
        {
            try
            {
                var cm = ResolveChatManager();   // static cache, Unity-null re-resolve (1.1.30)
                if (cm != null && p != null && p.Owner != null) cm.RpcTargetServerMessage(p.Owner, msg, false);
                else Log?.LogWarning($"[tell] SKIP send: cm={(cm != null)} p={(p != null)} owner={(p != null && p.Owner != null)} len={(msg != null ? msg.Length : 0)}");
            }
            catch (Exception e) { Log?.LogError("TellPlayer: " + e); }
        }

        // Private command list (the !help reply). Sent natively from the plugin via TellPlayer -- the SAME
        // path as !spec's confirmation, which renders reliably -- instead of the bot's relayed 'tell' verb
        // (which logged "delivering" but never rendered). Built here so no text is relayed. ONE message with
        // \n line breaks; the diagnostic log records the size + ChatManager state so a non-render is visible.
        internal void SendHelp(Player p)
        {
            try
            {
                // THREE lines, grouped, owner's order: Server, Teams, Stats (1.3.15). !help itself is
                // dropped - they just used it.
                string[] lines = {
                    "<color=#cfd8e3>Server</color>   <color=#FFC857>!votemap</color> \u00b7 <color=#cfd8e3>!notk</color> \u00b7 <color=#cfd8e3>!discord</color>",
                    "<color=#36FFD0>Teams</color>    <color=#36FFD0>!spec</color> \u00b7 <color=#36FFD0>!swapteam</color> \u00b7 <color=#FFC857>!forfeit</color>",
                    "<color=#55FF55>Stats</color>    <color=#55FF55>!rank</color> \u00b7 <color=#55FF55>!ranks</color> \u00b7 <color=#55FF55>!points</color> \u00b7 <color=#55FF55>!leaderboard</color> \u00b7 <color=#55FF55>!prestige</color>",
                };
                string msg = string.Join("\n", lines);
                Log?.LogInfo($"[help] -> {Sid(p)} : {lines.Length} lines, {msg.Length} chars, Cm={(Cm != null)}");
                TellPlayer(p, msg);
            }
            catch (Exception e) { Log?.LogError("SendHelp: " + e); }
        }

        // -------- player-vs-player kill (the bot's "kill" frame) --------
        // FactionHQ.ReportKillAction(killer, target, factor) fires for EVERY player who gets
        // kill credit on a death — including assists. Emitting a "kill" frame for each caused
        // duplicates (same victim, two killers, same second). We only emit when this
        // killer is the TOP damager on the victim unit, plus a short per-victim window dedup.
        static readonly Dictionary<string, float> _pvpKillAnnounced = new Dictionary<string, float>(StringComparer.Ordinal);
        const float PVP_KILL_DEDUP = 3f;

        // True when `killer` owns the unit with the highest damageCredit on `dead`.
        // Fail-open (return true) if the credit map is unreadable — victim-window dedup still
        // prevents a double kill frame; we prefer emitting once over dropping a real kill.
        // ================= damage-credit attribution (ONE implementation, two callers) =================
        // Minimum credit for a name to be trusted. A seeker LOCK credits the missile's owner 0.001 on
        // the target the moment the lock takes - ARHSeeker.cs:339, IRSeeker.cs:165 - whether or not the
        // missile ever connects. The game's OWN attribution filter is a 1%-SHARE test (Unit.cs:2099:
        // skip a contributor whose credit/total < 0.01), which a lone lock passes trivially: it is
        // 0.001 of 0.001, i.e. 100% of all damage the victim took. A share test cannot catch this; only
        // an ABSOLUTE floor can. 1.0 is chosen on its own terms - three orders of magnitude above a
        // lock, two below the weakest real weapon (guns credit ~100, warheads 1000+) - so it can only
        // ever remove a wrong name, never suppress a real kill.
        internal const float CreditNoiseFloor = 1f;

        // Argmax over the victim's private damageCredit, WITH the noise floor applied. Returns false
        // when the kill is unattributed. Both the moderation path (CheckTeamkill) and the scoring path
        // (IsTopDamager -> OnKill -> the kill frame) go through here: 1.3.30 shipped
        // the floor in CheckTeamkill alone, so a lock-only death was announced as a crash in-game while
        // the panel still published and PAID it as a kill. Two copies of an argmax is what allowed that,
        // so there is now exactly one.
        //   topKey == null && dmgTotal > 0  =>  credit existed but the best of it was noise (FLOORED)
        //   topKey == null && dmgTotal == 0 =>  no credit at all (a genuine crash / terrain / fire)
        static bool TopDamager(Unit dead, out object topKey, out float top, out float dmgTotal)
        {
            topKey = null; top = 0f; dmgTotal = 0f;
            if (_dmgCreditFI == null)
                _dmgCreditFI = typeof(Unit).GetField("damageCredit", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            object dcRaw = _dmgCreditFI?.GetValue(dead);
            if (dcRaw is Dictionary<PersistentID, float> dcT)
            {
                PersistentID topId = default; bool haveTop = false;
                foreach (var e in dcT)
                { dmgTotal += e.Value; if (e.Value > top) { top = e.Value; topId = e.Key; haveTop = true; } }
                if (haveTop) topKey = topId;
            }
            else if (dcRaw is System.Collections.IDictionary dc)
                foreach (System.Collections.DictionaryEntry e in dc)
                { float v; try { v = Convert.ToSingle(e.Value); } catch { continue; } dmgTotal += v; if (v > top) { top = v; topKey = e.Key; } }

            if (topKey != null && top < CreditNoiseFloor) { topKey = null; top = 0f; return false; }
            return topKey != null;
        }

        static bool IsTopDamager(Unit dead, Player killer)
        {
            try
            {
                if (dead == null || killer == null) return true;
                object topKey; float top, dmgTotal;
                TopDamager(dead, out topKey, out top, out dmgTotal);
                // FLOORED: real credit existed, but the best of it was seeker-lock noise. Return FALSE
                // explicitly - this function fail-opens to true on a null key, and fail-open here would
                // let the kill frame survive the floor the moderation path already applied.
                if (topKey == null && dmgTotal > 0f) return false;
                if (topKey == null) return true;   // no credit at all: unchanged fail-open (crash/terrain)
                if (!UnitRegistry.TryGetPersistentUnit((PersistentID)topKey, out var pu)) return true;
                Player topPl = null; try { topPl = pu.player; } catch { }
                if (topPl == null) return true;
                string topSid = Sid(topPl), kid = Sid(killer);
                return !string.IsNullOrEmpty(topSid) && topSid == kid;
            }
            catch { return true; }
        }

        internal static void OnKill(Player killer, object targetObj)
        {
            try
            {
                if (killer == null) return;
                string kid = Sid(killer);
                if (string.IsNullOrEmpty(kid) || kid == "0") return;
                if (targetObj is PilotDismounted) return;                   // hide ejected/rescued pilots entirely

                var ac = targetObj as Aircraft;          // players fly aircraft; AI aircraft have no Player
                if (ac == null) return;
                Player victim = ac.Player;
                if (victim == null) return;
                string vid = Sid(victim);
                if (string.IsNullOrEmpty(vid) || vid == "0" || vid == kid) return;     // human victim, not self
                if (killer.HQ != null && victim.HQ != null && killer.HQ == victim.HQ) return;  // enemy team only

                // 1.3.34: the pilot LEFT this airframe on the ground - destroying the abandoned hull is
                // not a kill of the pilot. Without this gate the enemy was still credited the kill and
                // the feed said "splashed <pilot>" for someone who walked away (the exact adjudicated
                // Alkyon case, one pipeline over). Same stamp CheckTeamkill consults.
                if (BailedFrom(vid, ac.GetInstanceID()))
                {
                    Log?.LogInfo($"[kill] {RawNameOf(killer)} destroyed {RawNameOf(victim)}'s abandoned hull - no kill credit");
                    return;
                }

                // Assist filter: ReportKillAction also fires for secondary credit — only the top
                // damager gets the splash.
                if (!IsTopDamager(ac, killer)) return;

                float now = Time.time;
                if (_pvpKillAnnounced.TryGetValue(vid, out var prev) && now - prev < PVP_KILL_DEDUP)
                    return;                                                 // same victim death already announced
                _pvpKillAnnounced[vid] = now;
                if (_pvpKillAnnounced.Count > 64)                           // bound the map (rare long sessions)
                {
                    var stale = new List<string>();
                    foreach (var kv in _pvpKillAnnounced)
                        if (now - kv.Value > 30f) stale.Add(kv.Key);
                    for (int i = 0; i < stale.Count; i++) _pvpKillAnnounced.Remove(stale[i]);
                }

                // Aircraft designators for the bot splash ([Rank] Name KR-67 in faction colour).
                string kPlane = "";
                try { kPlane = SafeText(Plane(killer)); } catch { }
                string vPlane = "";
                try { vPlane = SafeText(PlaneDesignator(ac.definition)); } catch { }
                if (string.IsNullOrEmpty(vPlane))
                { try { vPlane = SafeText(Plane(victim)); } catch { } }

                Out("{\"t\":\"kill\",\"kid\":\"" + kid + "\",\"kn\":\"" + Esc(RawNameOf(killer)) +
                    "\",\"kc\":\"" + FactionColour(killer) + "\",\"vid\":\"" + vid + "\",\"vn\":\"" + Esc(RawNameOf(victim)) +
                    "\",\"vc\":\"" + FactionColour(victim) +
                    "\",\"killer_plane\":\"" + Esc(kPlane) + "\",\"victim_plane\":\"" + Esc(vPlane) +
                    "\",\"ka\":\"" + Esc(kPlane) + "\",\"va\":\"" + Esc(vPlane) + "\"}");

                // The native killfeed announces the kill in-game; the bot logs activity only — never
                // rc.say splash. (The death stamp is written by CheckTeamkill, which runs first.)
            }
            catch (Exception e) { Log?.LogError("OnKill: " + e); }
        }

        // A death was BOOKED for this player (the stamp below). Any "eject" the 1Hz LifeTick scan
        // sees afterwards is the death sequence (leaving the wreck), not a voluntary bail, so it
        // must not book a second death. Cleared when they fly again (a LIVE airframe in LifeTick),
        // so a genuine eject on the NEXT life still counts. A wreck lingers ~30s, hence the window.
        static readonly Dictionary<string, float> _kfDownedAt = new Dictionary<string, float>(StringComparer.Ordinal);
        const float KF_DEATH_EJECT_SUPPRESS = 60f;   // generous: covers wreck lifetime + a slow bail

        static void NoteDeathAnnounced(string sid)
        {
            try
            {
                if (string.IsNullOrEmpty(sid) || sid == "0") return;
                float now = Time.time;
                _kfDownedAt[sid] = now;
                if (_kfDownedAt.Count > 64)
                {
                    foreach (var k in new List<string>(_kfDownedAt.Keys))
                        if (now - _kfDownedAt[k] > 120f) _kfDownedAt.Remove(k);
                }
            }
            catch { }
        }

        // ── BAIL LEDGER (1.3.21) ───────────────────────────────────────────────────────────────────
        // Which airframe each pilot has EJECTED from, recorded at the moment it happens.
        //
        // Everything here used to be inferred 30s later, and the inference was unsound. On a dedicated
        // server the ejection sequence never unlinks Aircraft.Player (the game only does that for a
        // LOCAL aircraft), so when the empty hull is finally destroyed it still reports the pilot as
        // its occupant. CheckTeamkill could not tell "crashed with someone aboard" from "empty hull of
        // a pilot who bailed 30s ago" - both arrive with victim != null and the life still open.
        //
        // The old workaround was to stamp a death only when a KILLER UNIT resolved, using "a killer
        // resolved" as a proxy for "this was a real death". That proxy is wrong in one direction: a
        // terrain crash, a stall, fuel starvation, or a killer whose PersistentID has left the registry
        // all resolve NO killer, so the death went unstamped and the wreck-despawn banked a SECOND life
        // for it - the same phantom-life class that was measured at 13-15% of all recorded lives.
        //
        // Recording the bail at its source removes the guess: if the pilot is on this ledger for THIS
        // airframe, the hull death is not their death; if they are not, it is - no killer required.
        static readonly Dictionary<string, int> _bailedFrom = new Dictionary<string, int>(StringComparer.Ordinal);
        // Write-time of each stamp (Time.time). The 1 Hz different-aircraft clear must NOT fire inside
        // the first ~15s: Player.SetAircraft auto-ejects the old hull and assigns the new one in the
        // SAME call, so a fast respawn put the pilot in a different airframe within one tick while the
        // abandoned hull's ejection tail (ReportKilled) was still ~1.6s away - the eager clear erased
        // the stamp first and the gates missed (audit r3). Lingering stamps are harmless: BailedFrom
        // needs sid AND airframe id to match, instance ids are session-unique, and each sid has one
        // slot that the next exit overwrites.
        static readonly Dictionary<string, float> _bailedAt = new Dictionary<string, float>(StringComparer.Ordinal);
        const float BAIL_STAMP_MIN_AGE = 15f;

        /// <summary>Record a bail from the aircraft itself. Both ejection entry points funnel here so
        /// the ledger cannot depend on which one the game happened to use.</summary>
        /// <summary>Total damage recorded against a unit. 0 means untouched.</summary>
        static float TotalDamageOn(Unit u)
        {
            try
            {
                if (u == null) return 0f;
                if (_dmgCreditFI == null)
                    _dmgCreditFI = typeof(Unit).GetField("damageCredit", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                object raw = _dmgCreditFI?.GetValue(u);
                float total = 0f;
                if (raw is Dictionary<PersistentID, float> dcT)
                {
                    foreach (var e in dcT) total += e.Value;
                }
                else if (raw is System.Collections.IDictionary dc)
                {
                    foreach (System.Collections.DictionaryEntry e in dc)
                    { try { total += Convert.ToSingle(e.Value); } catch { } }
                }
                return total;
            }
            catch { return 0f; }
        }

        // The game's OWN "this was a safe landed exit" signal: ReturnToInventory only runs on the
        // Abandoned path (landed, slow, near an own-faction airbase). Stamping here closes the
        // rollout race where the hook-instant IsLanded() read (speed < 2.5) disagrees with the tail
        // classifier's (speed < 2) a second later - the game declaring the airframe recovered IS a
        // landed exit by definition, whatever the thresholds said at the hook. (audit r2)
        internal static void NoteAbandonedReturn(Aircraft ac)
        {
            try
            {
                if (ac == null) return;
                Player p = null;
                try { p = ac.Player; } catch { }
                if (p == null) return;
                string sid = Sid(p);
                if (string.IsNullOrEmpty(sid) || sid == "0") return;
                int acId = 0;
                try { acId = ac.GetInstanceID(); } catch { }
                if (acId == 0) return;
                if (!_bailedFrom.TryGetValue(sid, out var was) || was != acId)
                {
                    _bailedFrom[sid] = acId;
                    Log?.LogInfo($"[bail] {RawNameOf(p)}'s airframe returned to inventory - landed exit confirmed");
                }
                _bailedAt[sid] = Time.time;   // refresh even when already stamped: the return runs ~1.6s
                                              // after the hook stamp and re-arms the anti-eager-clear window
            }
            catch { }
        }

        // -------- 1.4.6 water-ditch discriminator --------
        // OWNER CASE: EW-25 ocean ditch 2026-08-16 logged as a sortie. IsLanded() is a pure
        // radar-alt test (radarAlt < 5 && speed < 2.5) and the ocean is a REAL resting surface:
        // the water plane's collider sits on layer 6, inside the radar-alt linecast mask (2112),
        // so a ditched hull settles ON it, reads radarAlt~0 / speed~0 and passes the landed test
        // like a runway. This helper answers "is the supporting surface water?" with the game's
        // own two water idioms (BulletSim's water-impact test is exactly this pair):
        //   leg 1: hull at/under the waterline. Datum.LocalSeaY is sea level in the SAME
        //          Datum-shifted local space as transform.position (it moves with the floating
        //          origin); covers a semi-submerged hull and any scene without a water collider.
        //   leg 2 (PRIMARY): the stored radar-alt hit's collider carries
        //          GameAssets.i.WaterMaterial. A hull resting ON the water plane sits a few
        //          metres ABOVE LocalSeaY, so leg 1 alone can miss it. Unit.hit is the game's own
        //          stored linecast result, re-sampled every 0.1s by CheckRadarAlt - the very
        //          sample whose radarAlt made IsLanded() true - read via reflection because the
        //          plugin's reference set has no UnityEngine.PhysicsModule (RaycastHit / Collider
        //          / PhysicMaterial are compile-time unreachable; the comparison is done as
        //          UnityEngine.Object, which is CoreModule and keeps Unity's fake-null
        //          semantics). No new physics query is ever issued.
        // CARRIER DECKS FAIL BOTH LEGS: a floating deck sits above the waterline - the game's own
        // deck-recovery gate requires transform.position.y > Datum.LocalSeaY for every deck exit
        // that returns to inventory (live carrier recoveries on S2 prove it), and Ship declares
        // itself sinking once its origin drops below LocalSeaY - so a deck is well clear of the
        // 1m margin; and a deck hit fails leg 2 because the deck collider carries the ship's
        // non-water sharedMaterial (that collider's attachedRigidbody is precisely what makes
        // IsLanded()'s speed read deck-relative). Only evaluated AFTER IsLanded() returned true
        // (rare: landed exits), and every leg fails CLOSED to "not water", so any reflection
        // surprise reproduces 1.4.5 behaviour exactly.
        static FieldInfo _unitHitFI;      // Unit.hit (protected RaycastHit) - the stored radar-alt linecast result
        static MemberInfo _waterMatMI;    // GameAssets.WaterMaterial (PhysicsModule-typed -> reflection only)
        static bool IsOnWater(Aircraft ac)
        {
            try
            {
                if (ac == null) return false;
                var tr = ac.transform;
                if (tr == null) return false;
                if (tr.position.y < Datum.LocalSeaY + 1f) return true;           // leg 1: at/under the waterline
                if (_unitHitFI == null)
                    _unitHitFI = typeof(Unit).GetField("hit", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                object hit = _unitHitFI?.GetValue(ac);
                if (hit == null) return false;
                var colPI = hit.GetType().GetProperty("collider");
                var col = colPI?.GetValue(hit, null) as UnityEngine.Object;
                if (col == null) return false;                                    // linecast miss: no surface stored
                var matPI = col.GetType().GetProperty("sharedMaterial");
                var mat = matPI?.GetValue(col, null) as UnityEngine.Object;
                if (mat == null) return false;                                    // bare terrain/deck colliders
                var ga = GameAssets.i;
                if (ga == null) return false;
                if (_waterMatMI == null)
                    _waterMatMI = (MemberInfo)typeof(GameAssets).GetField("WaterMaterial")
                                  ?? typeof(GameAssets).GetProperty("WaterMaterial");
                UnityEngine.Object water = null;
                if (_waterMatMI is FieldInfo wf) water = wf.GetValue(ga) as UnityEngine.Object;
                else if (_waterMatMI is PropertyInfo wp) water = wp.GetValue(ga, null) as UnityEngine.Object;
                return water != null && mat == water;                             // leg 2: resting ON the water plane
            }
            catch { return false; }
        }

        internal static void NoteBailFrom(Aircraft ac)
        {
            try
            {
                if (ac == null) return;
                Player p = null;
                try { p = ac.Player; } catch { }
                if (p == null) return;

                // OWNER'S RULE (2026-08-13, superseding the 2026-08-01 damage rule): "a landed exit is
                // never a death." The old rule gated bail-vs-death on TotalDamageOn - but damageCredit
                // is a LIFETIME accumulator, never cleared for aircraft, tolerance-normalized and
                // overkill-inclusive, so flyable planes routinely carry five-digit totals (a Cricket
                // died carrying 77k, ~40x its hitpoint pool). Reading it as "current damage" booked the
                // owner's safely-landed Alkyon as "shot down by VT-7 Vagrant" (adjudicated, audit
                // 2026-08-13). The aircraft's LANDED state is the game's own exit discriminator
                // (Aircraft.cs tail classifier) and is current at this exact hook instant - use it.
                //   LANDED   -> stamp the bail ALWAYS, whatever the damage ledger says. The hull's later
                //               destruction is not this pilot's death (CheckTeamkill skips it via the
                //               stamp) and the life stays OPEN, same as a ground dismount at base.
                //   AIRBORNE -> stamp NOTHING. Every in-flight bail's hull reaches ReportKilled within
                //               seconds (crash or kill), which books the death there - matching the
                //               native kill feed, and keeping eject-before-the-missile-hits abuse
                //               closed. The old sub-1 "clean bail" exemption is deliberately gone: a
                //               mid-air bail IS in the kill feed, so it costs a life. (The auto-eject of
                //               an already-dead pilot also lands here: its death was ALREADY booked by
                //               ReportKilled before the ejection sequence ran, so stamping nothing is
                //               correct there too.)
                bool landed = false;
                try { landed = ac.IsLanded(); } catch { }
                // 1.4.6 WATER DITCH IS A DEATH: the ocean passes IsLanded() - the water plane is
                // a real resting surface inside the radar-alt mask (see IsOnWater above) - so a
                // mid-ocean ditch used to stamp the bail for a hull the game is about to disable
                // (owner case: EW-25 ocean ditch 2026-08-16). Demote it: a water "landing" takes
                // the existing airborne branch below - NOTHING is stamped - and the hull's
                // ReportKilled books the death through the untouched existing path.
                // Carrier-deck landings are unaffected (a deck fails
                // both IsOnWater legs), and ReturnToInventory (NoteAbandonedReturn) stays
                // game-authoritative for any near-base recovery the game itself performs.
                bool water = false;
                if (landed) { try { water = IsOnWater(ac); } catch { } }
                if (water) landed = false;
                if (!landed)
                {
                    bool adminStamped = false;
                    try { adminStamped = _bailedFrom.TryGetValue(Sid(p), out var w0) && w0 == ac.GetInstanceID(); } catch { }
                    Log?.LogInfo(adminStamped
                        ? $"[bail] admin/server eject of {RawNameOf(p)} - already stamped, the hull's ReportKilled is suppressed"
                        : water
                            ? $"[bail] water ditch by {RawNameOf(p)} - not a landed exit, the hull's ReportKilled books the death"
                            : $"[bail] airborne bail by {RawNameOf(p)} - the hull's ReportKilled books the death");
                    return;
                }
                float dmg = TotalDamageOn(ac);          // observability only - no longer a gate
                bool nearOwnBase = false;
                try { nearOwnBase = ac.NetworkHQ != null && ac.NetworkHQ.AnyNearAirbase(ac.transform.position, out _); } catch { }
                Log?.LogInfo($"[bail] landed exit by {RawNameOf(p)} (lifetime credit {dmg:0.###}, "
                             + $"nearOwnBase={nearOwnBase}) - never a death");
                string sid = Sid(p);
                if (string.IsNullOrEmpty(sid) || sid == "0") return;
                // Key on the AIRFRAME being left, taken from the aircraft we were called on - not from
                // p.Aircraft, which may already point elsewhere by the time this runs.
                int acId = 0;
                try { acId = ac.GetInstanceID(); } catch { }
                if (acId == 0) return;
                _bailedFrom[sid] = acId;
                _bailedAt[sid] = Time.time;
                Log?.LogInfo($"[bail] {RawNameOf(p)} ejected from airframe {acId}");
                PruneBailLedger();
            }
            catch { }
        }

        static void PruneBailLedger()
        {
            try
            {
                if (_bailedFrom.Count <= 64) return;
                var live = new HashSet<string>(StringComparer.Ordinal);
                foreach (var h in Humans()) { var s = Sid(h); if (!string.IsNullOrEmpty(s)) live.Add(s); }
                var drop = new List<string>();
                foreach (var kv in _bailedFrom) if (!live.Contains(kv.Key)) drop.Add(kv.Key);
                foreach (var s in drop) { _bailedFrom.Remove(s); _bailedAt.Remove(s); }
            }
            catch { }
        }

        // DEAD - retained for reference only, NOT called from anywhere (verified: zero callers; both
        // ejection postfixes call NoteBailFrom). This writes the bail ledger with NO classification at
        // all - neither the landed-exit test nor the older damage gate: wiring anything to it would
        // bypass NoteBailFrom's classification above
        // and mark a shot-down pilot as having bailed, leaving their death unstamped. If you need a
        // bail entry point, call NoteBailFrom - do not resurrect this. (audit 13)
        internal static void NoteBail(Player p)
        {
            try
            {
                string sid = Sid(p);
                if (string.IsNullOrEmpty(sid) || sid == "0") return;
                _bailedFrom[sid] = AcId(p);
                if (_bailedFrom.Count > 64)
                {
                    var live = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var h in Humans()) { var s = Sid(h); if (!string.IsNullOrEmpty(s)) live.Add(s); }
                    var drop = new List<string>();
                    foreach (var kv in _bailedFrom) if (!live.Contains(kv.Key)) drop.Add(kv.Key);
                    foreach (var s in drop) _bailedFrom.Remove(s);
                }
            }
            catch { }
        }

        /// <summary>True when this pilot already ejected from the given airframe, so its destruction is
        /// the empty hull despawning rather than a death.</summary>
        static bool BailedFrom(string sid, int acId)
        {
            try
            {
                return !string.IsNullOrEmpty(sid) && acId != 0
                       && _bailedFrom.TryGetValue(sid, out var was) && was == acId;
            }
            catch { return false; }
        }

        internal static void ClearBail(string sid)
        {
            try { if (!string.IsNullOrEmpty(sid)) { _bailedFrom.Remove(sid); _bailedAt.Remove(sid); } } catch { }
        }

        static bool EjectIsDeathSequence(string sid)
        {
            try
            {
                return !string.IsNullOrEmpty(sid) && _kfDownedAt.TryGetValue(sid, out var t)
                       && Time.time - t < KF_DEATH_EJECT_SUPPRESS;
            }
            catch { return false; }
        }

        // Flying again -> the previous death is done with; a later eject is genuine.
        internal static void ClearDeathAnnounced(string sid)
        {
            try { if (!string.IsNullOrEmpty(sid)) _kfDownedAt.Remove(sid); } catch { }
        }

        // Strategic-launcher name test (piledriver / ballistic / cruise). LIVE: feeds ClassifyTkMethod
        // via CachedIsStrategic so an AI-tasked launcher's friendly kill never escalates the owner.
        static bool IsStrategicLauncher(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("piledriver") || n.Contains("launcher") || n.Contains("ballistic")
                || n.Contains("strategic") || n.Contains("cruise");
        }

        // -------- 1.1.30 colour scheme (owner decision, final): ABSOLUTE hexes, no reflection --------
        // Chat + join names: bright faction colours PALA #ffe294 / BDF #d4baff, spectator/unknown
        // neutral #CFCFCF (ChatFactionHex below). GameFactionColour (raw faction.color, a plain
        // field read) is kept ONLY for WebCC/bot telemetry sampling (kc/vc, faction_colours frame,
        // map tint).
        internal const string BdfColourFallback = "#d4baff";   // BDF / Boscali chat+join name colour
        internal const string PalaColourFallback = "#ffe294";  // PALA / Primeva chat+join name colour
        // Legacy aliases (docs / any external refs).
        internal const string BdfColour = BdfColourFallback;
        internal const string PalaColour = PalaColourFallback;

        static readonly HashSet<string> _factionColourLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool _factionColoursEmitted;

        internal static string ColorToHex(Color c)
        {
            int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
            int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
            int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        static string FactionColourFallback(FactionHQ hq)
        {
            try
            {
                string f = (hq != null && hq.faction != null) ? hq.faction.factionName : "";
                f = (f ?? "").ToLowerInvariant();
                if (f.StartsWith("bosc") || f == "bdf") return BdfColourFallback;
                if (f.StartsWith("prim") || f == "pala") return PalaColourFallback;
            }
            catch { }
            return "#CFCFCF";
        }

        // Primary: RGB of game faction.color → #RRGGBB (raw / loud — NOT chat name tint).
        internal static string GameFactionColour(FactionHQ hq)
        {
            try
            {
                if (hq != null && hq.faction != null)
                {
                    Color c = hq.faction.color;
                    if (c.a > 0.01f && (c.r + c.g + c.b) > 0.02f)
                    {
                        string hex = ColorToHex(c);
                        try
                        {
                            string fn = hq.faction.factionName ?? "?";
                            if (_factionColourLogged.Add(fn))
                                Log?.LogInfo($"[colour] GameFactionColour {fn} = {hex} (raw faction.color)");
                        }
                        catch { }
                        return hex;
                    }
                }
            }
            catch { }
            return FactionColourFallback(hq);
        }

        internal static string GameFactionColour(Player p) => GameFactionColour(p != null ? p.HQ : null);

        // Raw faction.color for WebCC/bot NOSTATS (kc/vc, faction_colours). NOT for join paint.
        internal static string FactionColour(Player p) => GameFactionColour(p);
        internal static string FactionColour(FactionHQ hq) => GameFactionColour(hq);

        // Shared static ChatManager cache. 1.1.30: the old helper gated on `Instance != null`
        // (Unity fake-null -> always false once the plugin GameObject died), so the cache NEVER
        // cached and every caller paid a FindObjectOfType. Now: static field, Unity-null check on
        // the ChatManager itself, so a destroyed/stale manager re-resolves and a live one caches.
        internal static ChatManager ResolveChatManager()
        {
            try
            {
                var cm = Cm;
                if (cm == null)   // Unity op_Equality: also true for a DESTROYED cached instance -> re-resolve
                {
                    cm = UnityEngine.Object.FindObjectOfType<ChatManager>();
                    Cm = cm;
                }
                return cm;
            }
            catch { return null; }
        }

        // -------- 1.1.30: simple ABSOLUTE chat/join name colour (owner decision, final) --------
        // Replaces the whole native-colour reflection stack (GetTextColor invoke, allChatSaturation
        // HSV mirror, alliedChat/noFaction field reads - ALL removed). One fixed mapping, both chat
        // modes, every viewer: PALA #ffe294 / BDF #d4baff / spectator+unknown #CFCFCF.
        // Never throws, never needs a live ChatManager - a colour can never break chat delivery.
        internal static string ChatFactionHex(FactionHQ hq) => FactionColourFallback(hq);
        internal static string ChatFactionHex(Player p) => ChatFactionHex(p != null ? p.HQ : null);

        // Retained no-op: 1.0.15–1.0.17 wrote owner-picked hexes INTO faction.color (wrong).
        // Vanilla faction.color is now the source of truth — never overwrite it.
        internal static void ApplyJoinPaletteToFaction(FactionHQ hq) { }

        // Sample all HQs once a mission has factions; log + emit [NOSTATS] for bot/WebCC alignment.
        // WebCC still gets RAW faction.color; also log muted chat tint for join parity checks.
        internal static void MaybeEmitFactionColours()
        {
            if (_factionColoursEmitted) return;
            try
            {
                string pala = null, bdf = null, palaChat = null, bdfChat = null;
                foreach (var hq in UnityEngine.Object.FindObjectsOfType<FactionHQ>())
                {
                    if (hq == null || hq.faction == null) continue;
                    string f = (hq.faction.factionName ?? "").ToLowerInvariant();
                    string hex = GameFactionColour(hq);
                    if (string.IsNullOrEmpty(hex) || hex == "#CFCFCF") continue;
                    string chat = ChatFactionHex(hq);   // 1.1.30: absolute owner hexes (no native reflection)
                    if (f.StartsWith("prim") || f == "pala") { pala = hex; palaChat = chat; }
                    else if (f.StartsWith("bosc") || f == "bdf") { bdf = hex; bdfChat = chat; }
                }
                if (pala == null && bdf == null) return;
                _factionColoursEmitted = true;
                Log?.LogInfo($"[colour] raw faction.color PALA={pala ?? "?"} BDF={bdf ?? "?"}; absolute chat/join PALA={palaChat ?? "?"} BDF={bdfChat ?? "?"}");
                // pala/bdf = raw (WebCC map); pala_chat/bdf_chat = the absolute chat hexes for bot splash fallback.
                Out("{\"t\":\"faction_colours\",\"pala\":\"" + Esc(pala ?? "") + "\",\"bdf\":\"" + Esc(bdf ?? "")
                    + "\",\"pala_chat\":\"" + Esc(palaChat ?? "") + "\",\"bdf_chat\":\"" + Esc(bdfChat ?? "") + "\"}");
            }
            catch (Exception e) { Log?.LogError("MaybeEmitFactionColours: " + e); }
        }

        // -------- name layer (1.1.28, F3): the new async per-process pipeline --------
        // A usable resolved persona: not empty, not the game's no-steam fallback ("Player"),
        // not the unresolved sentinel ("ID: <steam64>").
        static bool IsResolved(string n)
            => !string.IsNullOrEmpty(n) && n != "Player" && !n.StartsWith("ID: ", StringComparison.Ordinal);

        // The player's REAL name on the new pipeline. Player.PlayerName no longer exists; each
        // process resolves names itself via GetPlayerName() (cache -> Steam, throttled request
        // while unresolved). Chain (1.1.29): RawNames cache -> Steam-resolved SanitizedName (the
        // exact string the game shows - it applies SanitizeRichText(32); cached on first
        // resolution) -> the bot's last-known name from the rank file (NameFallback; cached into
        // RawNames on use so every composer agrees at once) -> the game's own "ID: <steam64>"
        // sentinel. NEVER throws - ~70 call sites interpolate this inside action methods (see
        // the F4 hard rule).
        // WEB NAME LOOKUP (1.1.38, Tomo): when the game's own Steam resolution stalls, ask
        // Steam's public profile XML directly from the server - same data the clients read.
        // Background thread, fail-open; result applied on the main tick. The bot's rank-file
        // fallback stays as the belt to this brace.
        static readonly Dictionary<string, float> _webNameTried = new Dictionary<string, float>(StringComparer.Ordinal);
        static readonly List<KeyValuePair<string, string>> _webNameResults = new List<KeyValuePair<string, string>>();
        static readonly object _webNameLock = new object();
        static readonly System.Text.RegularExpressions.Regex _webNameRe =
            new System.Text.RegularExpressions.Regex("<steamID><!\\[CDATA\\[(.*?)\\]\\]></steamID>",
                System.Text.RegularExpressions.RegexOptions.Singleline);

        // WebClient's default 100s timeout can pin a thread-pool thread when Steam is slow.
        sealed class TimeoutWebClient : System.Net.WebClient
        {
            protected override System.Net.WebRequest GetWebRequest(Uri address)
            {
                var r = base.GetWebRequest(address);
                if (r != null) { r.Timeout = 10000; }
                return r;
            }
        }

        // Boot probe: fetch the Steam community root ONCE on a background thread purely to
        // prove TLS works under this container's Mono (the lookup's only silent failure mode).
        // No SteamID involved. Result lands in the BepInEx log as [webname] probe OK/FAIL.
        internal static void WebNameProbe()
        {
            try
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                        using (var wc = new TimeoutWebClient())
                        {
                            wc.Headers.Add("User-Agent", "Mozilla/5.0");
                            string body = wc.DownloadString("https://steamcommunity.com/");
                            Log?.LogInfo($"[webname] probe OK - TLS works here ({body.Length} bytes); name lookup is live");
                        }
                    }
                    catch (Exception e)
                    {
                        Log?.LogWarning("[webname] probe FAILED - server-side name lookup unavailable, "
                                        + "bot rank-file fallback still covers names: " + e.Message);
                    }
                });
            }
            catch { }
        }

        static void MaybeWebNameLookup(string sid)
        {
            try
            {
                if (string.IsNullOrEmpty(sid) || sid == "0" || sid.Length != 17) return;
                float now = Time.time;
                if (_webNameTried.TryGetValue(sid, out var last) && now - last < 120f) return;
                _webNameTried[sid] = now;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                        using (var wc = new TimeoutWebClient())
                        {
                            wc.Headers.Add("User-Agent", "Mozilla/5.0");
                            string xml = wc.DownloadString("https://steamcommunity.com/profiles/" + sid + "/?xml=1");
                            var m = _webNameRe.Match(xml);
                            if (!m.Success) return;
                            string n = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
                            n = n.Replace("<", "").Replace(">", "");          // never let a name smuggle rich text
                            if (n.Length == 0 || n.Length > 48) return;
                            lock (_webNameLock) _webNameResults.Add(new KeyValuePair<string, string>(sid, n));
                        }
                    }
                    catch { }                                                 // lookup is best-effort
                });
            }
            catch { }
        }

        static void PumpWebNames()   // on the main tick: apply fetched names + retag
        {
            try
            {
                if (_webNameResults.Count == 0) return;
                List<KeyValuePair<string, string>> got;
                lock (_webNameLock) { got = new List<KeyValuePair<string, string>>(_webNameResults); _webNameResults.Clear(); }
                foreach (var kv in got)
                {
                    // never clobber a resolved name; only fill gaps/placeholders
                    if (RawNames.TryGetValue(kv.Key, out var cur) && !string.IsNullOrEmpty(cur) && !cur.StartsWith("ID: ")) continue;
                    RawNames[kv.Key] = kv.Value;
                    Trace("WebNameApplied");
                    Log?.LogInfo($"[webname] {kv.Key.Substring(kv.Key.Length - 4)} resolved via Steam web lookup");
                }
            }
            catch (Exception e) { Log?.LogError("PumpWebNames: " + e); }
        }

        internal static string RawNameOf(Player p)
        {
            try
            {
                if (p == null) return "";
                string sid = Sid(p);
                if (!string.IsNullOrEmpty(sid) && RawNames.TryGetValue(sid, out var r) && !string.IsNullOrEmpty(r))
                    return r;
                var pn = p.GetPlayerName();                  // never null (falls back to "Player" / "ID: <sid>")
                string n = pn != null ? pn.SanitizedName : null;
                if (IsResolved(n))
                {
                    if (!string.IsNullOrEmpty(sid) && sid != "0") RawNames[sid] = n;
                    return n;
                }
                // 1.1.29: Steam unresolved -> the bot's last-known display name (rank-file field 6)
                // beats the sentinel. Cached in RawNames so the whole plugin flips to it at once
                // (this IS the player's last-known Steam persona, so a later live resolution would
                // agree in the overwhelming case; a rejoin re-resolves fresh via PruneLeavers).
                if (!string.IsNullOrEmpty(sid) && sid != "0")
                {
                    LoadRankMap();
                    if (NameFallback.TryGetValue(sid, out var fb) && !string.IsNullOrEmpty(fb))
                    {
                        RawNames[sid] = fb;
                        return fb;
                    }
                    MaybeWebNameLookup(sid);   // 1.1.38: about to show the placeholder -> ask Steam now
                }
                return "ID: " + sid;
            }
            catch { return "?"; }
        }

        // User-facing display name for plugin-composed chat/broadcast lines: "[TAG] Name" while
        // Chat.CustomChat is ON (rank now lives ONLY in plugin-composed strings), plain raw name
        // otherwise (pure-vanilla lever). Telemetry "n" fields stay RAW - the bot owns webcc rank.
        static string RankedName(Player p)
        {
            // 1.4.5: every consumer interpolates this straight into rich-text broadcast/tell lines,
            // and the raw name can be a rank-file NameFallback - a PLAYER-chosen Steam persona the
            // bot only pipe/newline-strips - so a name like "<color=red>" would recolour/break the
            // whole line. SafeText at this one chokepoint kills smuggled markup for all ~15 sites;
            // telemetry keeps raw names (they never come through RankedName).
            string raw = RawNameOf(p);
            if (CustomChat == null || !CustomChat.Value) return SafeText(raw);
            try { return SafeText(Prefixed(Sid(p), raw)); } catch { return SafeText(raw); }
        }

        // "[ABBR] raw" when the bot has pushed a rank for this player; plain "raw" otherwise (so
        // we never show a guessed/wrong rank). The total is capped at 32 chars (the game runs
        // SanitizeRichText(32) on the name) by trimming the raw tail, so the rank tag itself is
        // never the part that gets clipped. The full raw name is still cached in RawNames for
        // the bot, so this only affects the in-game display of very long names.
        static string Prefixed(string sid, string raw)
        {
            LoadRankMap();
            if (RankMap.TryGetValue(sid, out var rc) && !string.IsNullOrEmpty(rc.label))
            {
                string tag = "[" + rc.label + "] ";         // SHORTHAND rank in name: consistent with the kill feed,
                                                            // and avoids the full-rank "[Flying Officer]" duplicate the
                                                            // HUD lock / map marker show alongside the unitName label.
                int room = 32 - tag.Length;
                if (room < 1) return raw;                       // pathological: tag alone fills the cap
                if (raw.Length > room)
                {
                    raw = raw.Substring(0, room);
                    if (char.IsHighSurrogate(raw[raw.Length - 1]))     // 1.4.5: never cut an emoji in half -
                        raw = raw.Substring(0, raw.Length - 1);        // a lone high surrogate renders broken on every client
                }
                return tag + raw;
            }
            return raw;
        }

        // (1.1.28: InjectRankIntoName + its CmdSetPlayerName prefix are GONE - the game update
        //  deleted the whole client-proposed-name entry point. Rank injection now lives in the
        //  plugin-composed strings: FormatAndBroadcast chat reroute, AnnounceJoinFaction,
        //  RankedName swap/admin lines.)

        // -------- dismounted-pilot cleanup --------
        internal static void MaybeCleanupPilots()
        {
            try
            {
                if (CleanupPilots == null || !CleanupPilots.Value) return;
                float now = Time.time;
                if (now < _nextPilotSweep) return;
                _nextPilotSweep = now + 30f;                       // sweep at most every 30s
                float maxAge = Mathf.Max(30, PilotLifetime != null ? PilotLifetime.Value : 300);

                var live = UnityEngine.Object.FindObjectsOfType<PilotDismounted>();
                var seen = new HashSet<PilotDismounted>();
                int removed = 0;
                foreach (var pilot in live)
                {
                    if (pilot == null) continue;
                    seen.Add(pilot);
                    if (!PilotSeen.TryGetValue(pilot, out var first)) { PilotSeen[pilot] = now; continue; }
                    if (now - first < maxAge) continue;
                    try
                    {
                        if (pilot.Networkplayer != null) pilot.Networkplayer.RemovePilotDismounted(pilot);
                    }
                    catch { }
                    UnityEngine.Object.Destroy(pilot.gameObject);     // same despawn the game uses on capture/landing
                    removed++;
                }
                // forget pilots that are gone (captured/destroyed) so the dict doesn't grow
                foreach (var key in new List<PilotDismounted>(PilotSeen.Keys))
                    if (key == null || !seen.Contains(key)) PilotSeen.Remove(key);
                if (removed > 0) Log?.LogInfo($"[cleanup] despawned {removed} lingering pilot(s) (> {maxAge}s)");
            }
            catch (Exception e) { Log?.LogError("MaybeCleanupPilots: " + e); }
        }

        // -------- end of game: authoritative winner + awards --------
        internal void OnDeclareEndGame(FactionHQ hq, string endType)
        {
            try
            {
                if (Time.time - _lastEnd < 20f) return;                 // debounce paired/dup calls
                if (!string.Equals(endType, "Victory", StringComparison.OrdinalIgnoreCase)) return;
                _lastEnd = Time.time;
                ResetReconMeters();   // a breaker trip never outlives its match
                // NOTE: do NOT advance the balance game-counter here - that happens once per mission START
                // (AdvanceGame in StartingRankFloorPatch), so move-exemptions span whole games correctly.

                var players = Humans();
                string winFaction = hq != null && hq.faction != null ? hq.faction.factionName : "";
                EmitAll("snap");                                        // final authoritative scores
                Out("{\"t\":\"win\",\"f\":\"" + Esc(winFaction) + "\"}");

                foreach (var p in players)                              // +WinPoints to the winning side
                    if (p.HQ == hq) Award(p, WinPoints.Value, "win");

                var ranked = players.OrderByDescending(ScoreOf).ToList();   // placement bonuses
                int[] bonus = { FirstPlace.Value, SecondPlace.Value, ThirdPlace.Value };
                string[] tag = { "1st", "2nd", "3rd" };
                for (int i = 0; i < ranked.Count && i < 3; i++) Award(ranked[i], bonus[i], tag[i]);

                Out("{\"t\":\"end\"}");
                Log.LogInfo($"NukeStats: end-of-game, winner={winFaction}, {players.Count} players.");
                // NO match-end eject - a life PERSISTS across the match and ends
                // only on death or mid-air eject.
            }
            catch (Exception e) { Log?.LogError("OnDeclareEndGame: " + e); }
        }

        static double ScoreOf(Player p)
        {
            try { return Convert.ToDouble(p.PlayerScore, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        // Is this player the top IN-GAME scorer on their OWN team, right now? Owner's rule (2026-08-04):
        // "on each team the #1 player can't be auto balanced ... I clearly meant the in game score".
        //
        // Always recomputed, never cached: PlayerScore moves throughout a match, and a cached leader
        // would protect whoever led a minute ago while exposing whoever leads now. That matters most on
        // the DEFERRED path, where a queued pilot can take the lead during the very sortie the balancer
        // is waiting on.
        //
        // Scored 0 -> false. Early in a match everyone is on 0, and "the highest of all zeroes" is just
        // whoever the roster happens to enumerate first - an arbitrary player protected for no reason.
        // An exact tie at the top exempts BOTH, which is the right direction for a rule phrased as an
        // absolute: if two pilots are joint #1, neither of them is movable.
        static bool IsTeamTopScorer(Player p)
        {
            try
            {
                if (p == null) return false;
                FactionHQ hq = null; try { hq = p.HQ; } catch { }
                if (hq == null) return false;               // spectating: no team, nothing to protect
                double mine = ScoreOf(p);
                if (mine <= 0) return false;
                foreach (var q in Side(hq))
                    if (q != p && ScoreOf(q) > mine) return false;
                return true;
            }
            catch { return false; }                        // never let a scoring hiccup break balancing
        }

        static void Award(Player p, int pts, string reason)
        {
            if (p == null || pts == 0) return;
            string id = Sid(p);
            if (string.IsNullOrEmpty(id) || id == "0") return;
            Out("{\"t\":\"award\",\"id\":\"" + id + "\",\"n\":\"" + Esc(RawNameOf(p)) +
                "\",\"pts\":" + pts + ",\"reason\":\"" + reason + "\"}");
        }

        // ======================= life-event detection =======================
        // This plugin is the life EVENT detector: it emits a "life" event (reason "death" or "eject")
        // ONLY when the pilot DIES or EJECTS mid-air - the bot uses it to mark the live map's death
        // cross (the eject case has no "down" frame). A ground dismount, a disconnect, a match-end,
        // and a balance/admin move are all life-NEUTRAL (no event).

        // alive = a life is open; airborne = the aircraft was in the air last scan (tells a real mid-air
        // EJECT from a ground dismount). _balancing = SteamIDs ejected by an admin/balance move, so
        // LifeTick treats their aircraft-loss as life-NEUTRAL.
        sealed class Life { public bool alive; public bool airborne; }
        static readonly Dictionary<string, Life> _lives = new Dictionary<string, Life>(StringComparer.Ordinal);
        static readonly HashSet<string> _balancing = new HashSet<string>(StringComparer.Ordinal);
        // The AIRFRAME each _balancing entry was ejected from. On a dedicated server the pilotless jet
        // is NOT unlinked from Player.Aircraft by the ejection sequence - it lingers until the game's
        // ~30s WaitRemoveAircraft destroys it - so "p.Aircraft != null" does NOT mean "flying again".
        // Keying on identity is what separates "still sitting in the jet I was ejected from" from
        // "took a new aircraft", which a timer cannot do reliably. (round-3 audit 2026-08-01)
        static readonly Dictionary<string, int> _balancingAc = new Dictionary<string, int>(StringComparer.Ordinal);

        static int AcId(Player p)
        {
            try { return p != null && p.Aircraft != null ? p.Aircraft.GetInstanceID() : 0; }
            catch { return 0; }
        }

        /// <summary>Instance id of a Unit when it is an Aircraft, else 0. Lets a death be matched against
        /// the bail ledger by AIRFRAME rather than by player, so bailing and then dying in a NEW aircraft
        /// is still counted as a death.</summary>
        static int AcIdOf(Unit u)
        {
            try { return u is Aircraft a && a != null ? a.GetInstanceID() : 0; }
            catch { return 0; }
        }
        // AdminEject also stamps this guard (sid -> expiry time). The ON-DEATH path (CheckTeamkill, via the
        // ReportKilled patch) checks it and SKIPS the death bookkeeping for an admin- or
        // team-swap-ejected pilot - so an AIRBORNE eject (a balance move of a flyer, or the !swapteam/!forceteamswap
        // Cricket) is truly life-neutral. (_balancing alone only neutralises the slower 1Hz LifeTick
        // scan; the on-death patch fires first and would otherwise book a phantom death + spam chat.)
        static readonly Dictionary<string, float> _adminEjectGuard = new Dictionary<string, float>(StringComparer.Ordinal);
        internal static bool IsAdminEjecting(string sid) => !string.IsNullOrEmpty(sid) && _adminEjectGuard.TryGetValue(sid, out var exp) && Time.time < exp;
        internal static void GuardEject(string sid)
        {
            if (string.IsNullOrEmpty(sid) || sid == "0") return;
            float now = Time.time;
            _adminEjectGuard[sid] = now + 6f;                          // covers the async ReportKilled after StartEjectionSequence
            if (_adminEjectGuard.Count > 16)                           // opportunistic prune of expired entries
            {
                List<string> stale = null;
                foreach (var kv in _adminEjectGuard) if (kv.Value < now) (stale ?? (stale = new List<string>())).Add(kv.Key);
                if (stale != null) foreach (var s in stale) _adminEjectGuard.Remove(s);
            }
        }
        static Life LifeOf(string sid) { if (!_lives.TryGetValue(sid, out var l)) { l = new Life(); _lives[sid] = l; } return l; }
        static float _nextLifeScan;

        // Signal a completed life (drives the bot's live-map death cross). reason = "death" (any hull
        // ReportKilled with the pilot aboard - incl. mid-air bails since 1.3.34) or "eject" (the
        // no-ReportKilled fallback: teardown/anomaly; a real airborne plane). Ground dismounts,
        // disconnects, match-end and balance/admin moves do NOT end a life.
        static void EndLife(string sid, Life l, string reason)
        {
            if (l == null || !l.alive) return;
            l.alive = false;
            if (string.IsNullOrEmpty(sid) || sid == "0") return;
            _mapDead.Add(sid);                                            // live map: stop wreck pos; unlock on gap or live-ac timeout
            _mapDeadSawGap.Remove(sid);
            _mapDeadSince[sid] = Time.time;
            Out("{\"t\":\"life\",\"id\":\"" + sid + "\",\"r\":\"" + reason + "\"}");
            Log?.LogInfo($"[life] life ended {sid} ({reason})");
        }

        // Eject a player by ADMIN/BALANCE action (move/spectate/probation/teamkill-warn). Marks them so
        // LifeTick treats the resulting aircraft-loss as life-NEUTRAL (no phantom death/eject event).
        internal static void AdminEject(Player p)
        {
            try
            {
                if (p == null || p.Aircraft == null) return;
                string sid = Sid(p);
                if (!string.IsNullOrEmpty(sid) && sid != "0")
                {
                    _balancing.Add(sid);
                    _balancingAc[sid] = AcId(p);          // remember WHICH airframe, see _balancingAc
                    // 1.3.34: airborne ejections no longer stamp the bail ledger, so an admin move's
                    // life-neutrality would rest on the 6s guard alone - and multi-seat ejection tails
                    // can outlive it. Stamp here: this exit is SERVER-initiated, so the airborne-bail
                    // abuse the no-stamp rule exists for does not apply.
                    _bailedFrom[sid] = AcId(p);
                    _bailedAt[sid] = Time.time;
                    GuardEject(sid);
                }
                p.Aircraft.StartEjectionSequence();
            }
            catch (Exception e) { Log?.LogWarning("AdminEject: " + e); }
        }

        // Driven from HQTick (~1s): detect life START (got an aircraft) and airborne losses that never
        // saw a ReportKilled (1.3.34: real mid-air bails book as deaths there; this branch catches
        // airborne plane with no admin move). Only the discrete life-END events are emitted here (death
        // is emitted from the kill patch). Ground dismounts, disconnects, match-end and balance/admin
        // moves are all life-NEUTRAL. Also does map-dead unlocks and bail/balance ledger upkeep.
        internal static void LifeTick()
        {
            float now = Time.time;
            if (now < _nextLifeScan) return;
            _nextLifeScan = now + 1f;
            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);   // 1.1.37: sids actually enumerated THIS scan
                foreach (var p in Humans())
                {
                    string sid = Sid(p);
                    if (string.IsNullOrEmpty(sid) || sid == "0") continue;
                    seen.Add(sid);
                    var l = LifeOf(sid);
                    bool hasAc = false; try { hasAc = p.Aircraft != null; } catch { }
                    if (hasAc)
                    {
                        // Clear the admin-move marker ONLY when they are in a DIFFERENT airframe, i.e.
                        // genuinely flying again. Clearing it on "p.Aircraft != null" wiped it on the
                        // very next 1 Hz tick, because the jet they were ejected from stays linked for
                        // ~30s - so by the time it despawned the marker was gone, the eject branch took
                        // the REAL-eject path and booked an eject for a move that is documented as
                        // life-neutral (announcing a false "<player> ejected" once the 6s guard had
                        // also expired).
                        if (!_balancingAc.TryGetValue(sid, out int wasAc) || AcId(p) != wasAc)
                        {
                            _balancing.Remove(sid);
                            _balancingAc.Remove(sid);
                        }
                        // Bail-ledger clear: only once they are demonstrably in a DIFFERENT airframe
                        // AND the stamp is older than BAIL_STAMP_MIN_AGE. The age gate matters: a fast
                        // respawn (SetAircraft auto-eject + assign in one call) puts the pilot in the
                        // new plane within one tick while the abandoned hull's ejection-tail
                        // ReportKilled is still ~1.6s out - clearing eagerly re-created the adjudicated
                        // false-death through the respawn path. A stamp that lingers past its hull's
                        // demise is inert: it can only ever match that dead hull's instance id. (r3)
                        if (!_bailedFrom.TryGetValue(sid, out int bailedAc) || AcId(p) != bailedAc)
                        {
                            if (!_bailedFrom.ContainsKey(sid)
                                || !_bailedAt.TryGetValue(sid, out var bAt)
                                || Time.time - bAt >= BAIL_STAMP_MIN_AGE)
                            {
                                _bailedFrom.Remove(sid);
                                _bailedAt.Remove(sid);
                            }
                        }
                        // 1.3.34 safety valve: if the pilot is somehow FLYING the very airframe the
                        // stamp says they left (the game destroys exited hulls within seconds, so this
                        // should be unreachable - but a stamp that survived into a real flight would
                        // make CheckTeamkill skip a genuine death), clear it: the exit didn't stick.
                        else if (AcId(p) == bailedAc)
                        {
                            // The valve must NOT fire during the 1.5-2.5s exit window: the pilot stays
                            // Player-linked to the live hull until the async ejection tail disables it,
                            // and one 1 Hz tick always lands inside - a momentary IsLanded()=false there
                            // (deck bounce, radar-alt blip) would clear a legitimate stamp and resurrect
                            // the exact adjudicated bug. Aircraft.HasEjected() latches TRUE the instant
                            // the sequence starts and never resets, so a mid-sequence hull can never
                            // pass this gate; only a genuinely never-ejected flight can. (audit r2)
                            bool acAlive = false;
                            try { acAlive = !p.Aircraft.disabled; } catch { }
                            bool airborneNow = false;
                            try { airborneNow = !p.Aircraft.IsLanded(); } catch { }
                            bool exited = true;
                            try { exited = p.Aircraft.HasEjected(); } catch { }
                            if (acAlive && airborneNow && !exited)
                            {
                                _bailedFrom.Remove(sid);
                                _bailedAt.Remove(sid);
                                Log?.LogInfo($"[bail] {RawNameOf(p)} is flying the airframe they had exited - stamp cleared");
                            }
                        }
                        // live map: clear death lockout after null-aircraft gap, OR a live (!disabled)
                        // airframe past MapDeadLiveAcUnlock (respawn paths that never observe a gap).
                        if (_mapDead.Contains(sid))
                        {
                            bool disabled = false;
                            try { disabled = p.Aircraft.disabled; } catch { disabled = true; }
                            bool gapOk = _mapDeadSawGap.Contains(sid);
                            bool liveUnlock = !disabled && _mapDeadSince.TryGetValue(sid, out float since)
                                              && (now - since) >= MapDeadLiveAcUnlock;
                            if (gapOk || liveUnlock) ClearMapDead(sid);
                        }
                        // 1.2.6 - the phantom "eject ~30s after being shot down".
                        // The server NEVER nulls Player.Aircraft (Player.RemoveAircraft only runs under
                        // IsLocalAircraft), so this branch keeps running on the DISABLED WRECK for the ~30s
                        // until WaitRemoveAircraft destroys it. It used to wipe the death stamp there, so when
                        // the wreck finally despawned the eject branch below banked a second life for a player
                        // who had actually been shot down half a minute earlier.
                        // The life reopen MUST stay. Only the STAMP WIPE is wrong on a wreck: kept,
                        // EjectIsDeathSequence suppresses the phantom; a respawn's live airframe clears the
                        // stamp within a tick so the next genuine bail counts. Unreadable -> 1.2.5 behaviour.
                        bool acDead = false; try { acDead = p.Aircraft.disabled; } catch { acDead = false; }
                        if (!acDead) ClearDeathAnnounced(sid);   // only a LIVE airframe means the death is over
                        if (!l.alive) l.alive = true;
                        try { l.airborne = !p.Aircraft.IsLanded(); } catch { l.airborne = true; }
                    }
                    else
                    {
                        if (_mapDead.Contains(sid)) _mapDeadSawGap.Add(sid);
                        if (l.alive && l.airborne)
                        {
                            if (_balancing.Contains(sid))
                            {
                                // a balance/admin move ejected them - NOT a real eject: keep the life OPEN and
                                // count nothing (balancing never ruins a rank). Treat like a ground dismount.
                                _balancing.Remove(sid);
                                _balancingAc.Remove(sid);
                                l.airborne = false;
                            }
                            else
                            {
                                // lost an airborne plane with no admin move and NO ReportKilled seen -> the
                                // fallback eject close (teardown/anomaly; real bails book as deaths since 1.3.34).
                                // (A real death is closed earlier in the kill patch with reason "death".)
                                // 1.2.7: if this player was ALREADY announced as downed, this is not a bail at
                                // all - it is their wreck despawning ~30s later (the server never nulls
                                // Player.Aircraft at death, so the loss is only observed when the hull is
                                // destroyed). Emitting EndLife here booked a SECOND event for one death.
                                // Measured across both servers: 13-15% of all recorded lives were this
                                // phantom. Close the life quietly instead - no second event.
                                if (_bailedFrom.ContainsKey(sid))
                                {
                                    // 1.3.34: a LANDED-EXIT stamp means this "airborne loss" is really the
                                    // abandoned hull leaving the world (returned to inventory / despawned /
                                    // destroyed) with the 1 Hz airborne flag stale from the final approach.
                                    // Not a death, not an air-eject: the life stays OPEN,
                                    // exactly like the ground dismount it actually was.
                                    l.airborne = false;
                                    Log?.LogInfo($"[life] {RawNameOf(p)}'s landed-exit hull despawned - life stays open");
                                }
                                else if (EjectIsDeathSequence(sid)) { l.alive = false; l.airborne = false; }
                                else EndLife(sid, l, "eject");
                            }
                        }
                    }
                    // A GROUND dismount (l.airborne == false) does NOTHING: the life stays OPEN -
                    // it ends only on death or air-eject.
                }
                // 1.1.37: a MID-AIR DISCONNECTOR is no longer enumerated, so their Life kept
                // airborne=true and the first tick after a reconnect fired a phantom
                // EndLife("eject"). Clear ONLY the stale airborne flag on
                // unseen lives - l.alive is untouched, so the life still survives the disconnect
                // as designed and the reconnect takes the ground-dismount no-op path. Fail-open:
                // worst case a real air-eject during a transient enumeration gap goes life-neutral
                // (misses a count instead of inventing one).
                foreach (var kv in _lives)
                    if (kv.Value.airborne && !seen.Contains(kv.Key)) kv.Value.airborne = false;
                // Disconnects are deliberately NOT dropped - the life stays open across a
                // reconnect. _lives is bounded by unique SteamIDs per
                // session and is cleared on the daily server restart.
            }
            catch (Exception e) { Log?.LogError("LifeTick scan: " + e); }
        }

        // ======================= teamkill enforcement (friendly fire) =======================
        // Detection: Unit.ReportKilled runs for every death; the dead unit's top damager (from
        // damageCredit) who is a PLAYER on the SAME faction as the dead unit = a teamkill (covers
        // friendly buildings/vehicles/aircraft). Escalation PER MATCH: 1st = eject + private warning;
        // 2nd = kick ("next is a ban") + set in-game rank 0 on rejoin; 3rd = ban. Bans persist
        // (plugin_bans.txt) and are enforced by kicking on sight. Defensive: failures no-op (never
        // a false kick). TK is rare/intentional in this game, so auto-enforcement is safe.
        internal static ConfigEntry<bool> TeamkillEnforce;
        internal static ConfigEntry<float> TeamkillMinDamage;   // fairness floor: min credited damage for a friendly kill to COUNT (0 = off)
        internal static ConfigEntry<bool> TeamkillCollateralEnforce;
        internal static ConfigEntry<float> TeamkillCollateralWindow;
        internal static ConfigEntry<float> TeamkillCollateralWindowNuclear;   // forward window for nuke-scale blasts
        internal static ConfigEntry<int> TeamkillSilentMinEnemies;            // overwhelming collateral -> no Moderation entry (0 = tier off)
        internal static ConfigEntry<float> TeamkillSilentRatio;               // ... and enemies must also be >= ratio * friendlies
        internal static ConfigEntry<bool> TeamkillBigUnitExempt;
        internal static ConfigEntry<int> TeamkillCollateralMaxPerMatch;
        struct KillRec { public float t; public float dmg; public bool enemy; public bool big; public string name; }   // name/dmg feed the per-blast unit list in the mod log
        static readonly Dictionary<string, List<KillRec>> _killWin = new Dictionary<string, List<KillRec>>(StringComparer.Ordinal);  // killer sid -> recent kills (any faction) for the collateral window
        // back/fwd frozen at defer time (a live config change must not skew an in-flight verdict). fwd extends
        // PAST the friendly kill for nuke-scale blasts; victims accumulates same-blast friendly names.
        class TkPending { public string sid, victim, method, weapon, munition; public float dmg, eventT, dueAt, back, fwd; public List<string> victims; }
        static readonly List<TkPending> _tkJudge = new List<TkPending>();   // friendly kills awaiting their collateral verdict
        static readonly Dictionary<string, float> _tkReportStart = new Dictionary<string, float>(StringComparer.Ordinal);  // report-only per-event dedup anchor
        static readonly Dictionary<string, float> _tkCollatStart = new Dictionary<string, float>(StringComparer.Ordinal);  // collateral-verdict entry anchor
        // Each queued offence carries ITS OWN victim/method/weapon. method = the contract tag for the moderation
        // report: direct / splash / auto / "" when unknown.
        struct TkEvent { public string sid, victim, method, weapon, munition; public float dmg, eventT; }   // eventT = Time.time of the OFFENCE
        static readonly List<TkEvent> _tkQueue = new List<TkEvent>();
        const int TK_QUEUE_MAX = 64;         // bounded (drained every TkTick; overflow only under an absurd flood)
        static readonly Dictionary<string, int> _tkCount = new Dictionary<string, int>(StringComparer.Ordinal);   // per match
        static readonly HashSet<string> _tkBanned = new HashSet<string>(StringComparer.Ordinal);                  // persistent
        static readonly HashSet<string> _tkRankZero = new HashSet<string>(StringComparer.Ordinal);               // rank 0 on next sight
        static readonly List<KeyValuePair<string, float>> _tkKicks = new List<KeyValuePair<string, float>>();    // delayed kicks
        static readonly Dictionary<string, float> _tkEventStart = new Dictionary<string, float>(StringComparer.Ordinal);  // killer sid -> start of the current blast/event (per-EVENT dedup anchor)
        static readonly Dictionary<string, int> _tkCollateralCount = new Dictionary<string, int>(StringComparer.Ordinal); // per match: exonerating verdicts per sid, for the anti-abuse cap
        const float TK_EVENT_DEDUP = 1.5f;   // one blast/event (same instigator within this window OF THE FIRST kill) = AT MOST one offence; anchored
        static System.Reflection.FieldInfo _dmgCreditFI;
        static float _nextTkScan;

        static string BanFilePath => Path.Combine(Paths.GameRootPath, "plugin_bans.txt");
        internal static void LoadBans()
        {
            try { if (File.Exists(BanFilePath)) foreach (var l in File.ReadAllLines(BanFilePath)) { var s = l.Trim(); if (s.Length > 0) _tkBanned.Add(s); } }
            catch (Exception e) { Log?.LogError("LoadBans: " + e); }
        }
        static void SaveBans()
        {
            try { File.WriteAllText(BanFilePath, string.Join("\n", _tkBanned) + "\n"); }
            catch (Exception e) { Log?.LogError("SaveBans: " + e); }
        }
        internal static void ClearMatchTeamkills() { _tkCount.Clear(); _tkRankZero.Clear(); _tkEventStart.Clear(); _tkReportStart.Clear(); _tkCollatStart.Clear(); _killWin.Clear(); _tkJudge.Clear(); _tkQueue.Clear(); _tkKicks.Clear(); _tkCollateralCount.Clear(); _lastLaunch.Clear(); _lastNuclearLaunch.Clear(); _lastGunHit.Clear(); _lastSubmunition.Clear(); ClearPendingBalance(); }   // per-match reset (bans persist)

        // Emit a teamkill-moderation event to the bot ([NOSTATS] line it tails) -> activity log + the webcc
        // Moderation tab, recording WHAT caused the eject/kick/ban (the teammate killed + the offense count).
        // ts=0 -> the bot stamps the real time on ingest.
        static void EmitTkMod(string sid, Player p, string action, int count, TkEvent ev, string nc = "",
                              List<KillRec> units = null)
        {
            string nm = p != null ? RawNameOf(p) : sid;
            string victim = !string.IsNullOrEmpty(ev.victim) ? ev.victim : "a teammate";
            // nc = not-counted reason ("auto"/"no-weapon"/"below-floor"/"collateral"/"big-unit"); "" = counted.
            // ts = wall-clock time of the OFFENCE itself (back-dated from ev.eventT); ts<=0 -> the bot stamps ingest time.
            double ts = 0;
            try
            {
                float age = ev.eventT > 0f ? Mathf.Max(0f, Time.time - ev.eventT) : 0f;
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - age;
            }
            catch { }
            // units = every unit that died in the same blast window. f: e=enemy, f=friendly; d=credited damage.
            // Capped at 24 + an overflow count so a city-nuke can't emit a multi-KB frame.
            string ujson = "";
            if (units != null && units.Count > 0)
            {
                var sb = new StringBuilder(64 + units.Count * 40);
                sb.Append(",\"units\":[");
                int shown = Math.Min(units.Count, 24);
                for (int i = 0; i < shown; i++)
                {
                    var k = units[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"n\":\"").Append(Esc(k.name ?? "?")).Append("\",\"f\":\"").Append(k.enemy ? 'e' : 'f')
                      .Append("\",\"d\":").Append(k.dmg.ToString("0", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
                }
                sb.Append(']');
                if (units.Count > 24) sb.Append(",\"unitsMore\":").Append(units.Count - 24);
                ujson = sb.ToString();
            }
            Out("{\"t\":\"tk\",\"id\":\"" + sid + "\",\"n\":\"" + Esc(nm)
                + "\",\"victim\":\"" + Esc(victim) + "\",\"method\":\"" + Esc(ev.method ?? "")
                + "\",\"weapon\":\"" + Esc(ev.weapon ?? "")
                + "\",\"munition\":\"" + Esc(ev.munition ?? "") + "\",\"count\":" + count
                + ",\"dmg\":" + ev.dmg.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"action\":\"" + action + "\",\"nc\":\"" + Esc(nc ?? "")
                + "\",\"ts\":" + ts.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ujson + "}");
        }

        // ---- collateral kill window + munition launch tracking (ported from 0.9.46) ----
        // Record one player-attributed unit kill into the killer rolling window (feeds the collateral verdict
        // AND the per-blast unit list in the mod log).
        static void NoteKillForCollateral(string sid, bool enemy, bool big, string name, float dmg)
        {
            if (!_killWin.TryGetValue(sid, out var l)) { l = new List<KillRec>(); _killWin[sid] = l; }
            l.Add(new KillRec { t = Time.time, dmg = dmg, enemy = enemy, big = big, name = name });
            if (l.Count > 128) l.RemoveRange(0, l.Count - 128);   // bounded per player
        }

        // Munition launch tracking. damageCredit keys the DAMAGING UNIT - the firing aircraft for its own weapons, but the
        // CARRIER MISSILE for a submunition (Spawner sets the bomblet's ownerID to the carrier) - so the
        // weapon must be remembered at LAUNCH. Spawner.SpawnMissile is [Server]-only and every live missile/bomb
        // passes through it; keep the last launch per owner unit. blastYield > 200 is the game nuclear threshold.
        internal struct LaunchRec { public string weapon; public float yield, t; }
        static readonly Dictionary<long, LaunchRec> _lastLaunch = new Dictionary<long, LaunchRec>();          // owner unit persistentID.Id -> most recent launch
        static readonly Dictionary<long, LaunchRec> _lastNuclearLaunch = new Dictionary<long, LaunchRec>();   // ... and the most recent NUCLEAR one
        internal static void NoteLaunch(long ownerId, string weapon, float yield)
        {
            if (_lastLaunch.Count > 1024) _lastLaunch.Clear();   // bounded (keyed per OWNER unit, not per missile)
            var r = new LaunchRec { weapon = weapon, yield = yield, t = Time.time };
            _lastLaunch[ownerId] = r;
            if (yield > 200f)
            {
                if (_lastNuclearLaunch.Count > 128) _lastNuclearLaunch.Clear();
                _lastNuclearLaunch[ownerId] = r;
            }
        }
        // GUN / CANNON HITS. Guns never reach Spawner.SpawnMissile - Gun.cs takes the BulletSim path -
        // so a cannon kill has no launch record and falls back to the aeroplane name. That is the whole
        // residual after munition tracking. Kept in its OWN map: merging it into _lastLaunch would let a
        // pilot who fired missiles earlier be reported as killing with one. Short window on purpose - a
        // bullet's flight time is sub-second, so a recent gun hit is far stronger evidence than a
        // minutes-old launch.
        // Keyed by (SHOOTER, VICTIM), not by shooter alone. Keying on the shooter and picking by
        // recency let a strafing pass hijack the naming of a missile kill seconds later - the gun
        // record would win the race while describing a completely different engagement. Pairing it
        // with the victim means a gun record can only ever name the weapon that actually hit THAT
        // victim, so no damage gate and no recency race are needed to keep it honest.
        // Submunition launches, re-keyed onto the firing AIRCRAFT by the owner-chain walk. Deliberately
        // a SEPARATE map: writing them into _lastLaunch would overwrite the carrier weapon's record -
        // the only record still carrying that launch's yield - which is how a bomblet dispense could
        // silently change an existing resolution and, in the worst case, flip the nuclear collateral
        // window. Consulted BELOW _lastLaunch so the whole change stays strictly additive.
        static readonly Dictionary<long, LaunchRec> _lastSubmunition = new Dictionary<long, LaunchRec>();
        internal static void NoteSubmunition(long ownerId, string weapon, float yield)
        {
            if (_lastSubmunition.Count > 1024) _lastSubmunition.Clear();
            _lastSubmunition[ownerId] = new LaunchRec { weapon = weapon, yield = yield, t = Time.time };
        }
        static readonly Dictionary<long, LaunchRec> _lastGunHit = new Dictionary<long, LaunchRec>();
        static long GunKey(long shooterId, long victimId) => (shooterId << 32) ^ (victimId & 0xFFFFFFFFL);
        internal static void NoteGunHit(long shooterId, long victimId, string weapon)
        {
            if (_lastGunHit.Count > 4096) _lastGunHit.Clear();   // bounded; pairs grow faster than shooters
            _lastGunHit[GunKey(shooterId, victimId)] = new LaunchRec { weapon = weapon, yield = 0f, t = Time.time };
        }

        // Launch lookup for the credited killer unit. GATED on the kill credited damage being munition-plausible
        // (>=500: guns credit ~100-300, missile/bomb/shockwave kills ~1000+). A live NUCLEAR launch takes
        // precedence over later conventional launches so post-nuke defensive shots can't mask it.
        static bool TryGetRecentLaunch(object topKey, object victimKey, float kredit, out LaunchRec rec)
        {
            rec = default;
            try
            {
                if (topKey == null) return false;
                long id = ((PersistentID)topKey).Id;

                // STRICTLY ADDITIVE ORDERING. Paths 1 and 2 are HEAD's behaviour, byte for byte. The gun
                // probe is LAST and is only reached when neither matched, so it can only ever turn a
                // 'weapon not identified' into a name - it can never change, displace or contradict a
                // resolution the old build would have produced.
                //
                // That ordering IS the safety argument. Putting the gun probe any higher preempts the
                // munition record for the same victim, and in the 45-120s band that record is the ONLY
                // thing still carrying a nuclear yield: replacing it with a yield-0 gun record flips
                // `nuclear` false, collapses the collateral window 20s -> 2.5s, and files one blast's
                // friendly victims as SEPARATE offences - walking an innocent pilot up eject -> kick -> BAN.

                // 1. NUCLEAR (unchanged).
                if (kredit >= 500f && _lastNuclearLaunch.TryGetValue(id, out rec) && Time.time - rec.t <= 45f) return true;

                // 2. MUNITION LAUNCH (unchanged, >=500 gate intact). Also the 45-120s nuclear safety net,
                //    because NoteLaunch writes a nuclear launch into BOTH maps.
                if (kredit >= 500f && _lastLaunch.TryGetValue(id, out var lr) && Time.time - lr.t <= 120f)
                { rec = lr; return true; }

                // 3. SUBMUNITION launch re-keyed onto this aircraft - new, and only reached when the
                //    carrier's own record above did not resolve, so it can only fill a gap.
                if (kredit >= 500f && _lastSubmunition.TryGetValue(id, out var sr) && Time.time - sr.t <= 120f)
                { rec = sr; return true; }

                // 4. GUN HIT on THIS victim - new, and only reached when nothing above resolved. No damage
                //    gate: guns credit ~100-300, so the >=500 munition gate would make this unreachable for
                //    exactly the kills it exists to name. It needs none - the record is keyed to
                //    (shooter, victim), and the caller has ALREADY established this shooter as the top
                //    damager on this victim, so we are only naming which of that killer's weapons struck
                //    them. Short window: a bullet's flight time is sub-second.
                if (victimKey != null)
                {
                    long vid = ((PersistentID)victimKey).Id;
                    if (_lastGunHit.TryGetValue(GunKey(id, vid), out var gr) && Time.time - gr.t <= 5f)
                    { rec = gr; return true; }
                }
                // Path 1 writes straight into `rec`, so a >45s-old nuclear entry leaves a stale record
                // behind when its age test fails. Both callers short-circuit on the bool, but clear it
                // so a future caller cannot read a stale nuclear yield and widen the collateral window.
                rec = default;
                return false;
            }
            catch { return false; }
        }
        // Ungated live-nuke probe for DEDUP SPANS only (below-floor grazes carry tiny credit).
        static bool HasLiveNuclearLaunch(object topKey)
        {
            try
            {
                return topKey != null && _lastNuclearLaunch.TryGetValue(((PersistentID)topKey).Id, out var nl)
                    && Time.time - nl.t <= 45f;
            }
            catch { return false; }
        }

        // Alloc-cached name classifiers (bit0=strategic, bit1=auto-defence, bit2=big-objective), lower ONCE.
        static readonly Dictionary<string, byte> _nameClassCache = new Dictionary<string, byte>(StringComparer.Ordinal);
        static byte ClassifyUnitName(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            if (_nameClassCache.TryGetValue(name, out var b)) return b;
            if (_nameClassCache.Count > 512) _nameClassCache.Clear();   // bounded
            b = (byte)((IsStrategicLauncher(name) ? 1 : 0) | (IsAutoDefenseUnit(name) ? 2 : 0) | (IsBigObjectiveUnit(name) ? 4 : 0));
            _nameClassCache[name] = b;
            return b;
        }
        static bool CachedIsStrategic(string name) => (ClassifyUnitName(name) & 1) != 0;
        static bool CachedIsAutoDefense(string name) => (ClassifyUnitName(name) & 2) != 0;
        static bool CachedIsBigObjective(string name) => (ClassifyUnitName(name) & 4) != 0;

        // Big enemy OBJECTIVES (ship classes): killing one alongside a friendly = the friendly was collateral of a
        // real strike. Name-substring like IsAutoDefenseUnit.
        static bool IsBigObjectiveUnit(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("carrier") || n.Contains("destroyer") || n.Contains("corvette")
                || n.Contains("frigate") || n.Contains("cruiser") || n.Contains("argus");
        }

        // Heli-dropped SAM/CRAM/AA names that auto-engage (deployed defenses) -- their friendly kills are AI-tasked,
        // not a deliberate human trigger-pull. Kept name-only (the damaging unit's definition.unitName, already
        // resolved at the kill site) so this never depends on a game-API member that could break plugin load.
        static bool IsAutoDefenseUnit(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("sam") || n.Contains("cram") || n.Contains("c-ram") || n.Contains("phalanx")
                || n.Contains("flak") || n.Contains(" aa") || n.EndsWith("aa") || n.Contains("anti-air") || n.Contains("anti air");
        }

        // Classify HOW a friendly kill happened from the damaging unit's name + agency, AND whether it counts as a
        // DELIBERATE teamkill. A directly-piloted weapon (a pilot's gun/missile/bomb, a player ramming) IS
        // deliberate; an auto-engaging deployed defense (heli-dropped SAM/CRAM/AA) or an AI-tasked / strategic
        // launcher fired itself and must NOT escalate the human owner through warn->kick->BAN (the #6 innocent-ban
        // bug). Fail-open: on any ambiguity treat it as a deliberate weapon kill (preserves catch-genuine-TK).
        // `unitName` = the damaging unit's definition.unitName (may be null/empty); `killer` non-null = a resolved
        // human controller. Out `deliberate` => count it as an offence; returns the report-method tag.
        static string ClassifyTkMethod(string unitName, out bool deliberate)
        {
            deliberate = true;
            try
            {
                if (CachedIsStrategic(unitName) || CachedIsAutoDefense(unitName))
                {
                    deliberate = false;   // auto/AI-tasked -> report it, but do NOT escalate the owner
                    return "auto";
                }
            }
            catch { }
            return string.IsNullOrEmpty(unitName) ? "" : "direct";
        }

        static void Kick(Player p)
        {
            // KickPlayer(INetworkPlayer) is the void overload (KickPlayerAsync would pull in UniTask).
            // NOTE: Nuclear Option treats KickPlayer like kick-player RCON — the SteamID stays on the
            // session kick list and cannot rejoin until server restart OR unkick-player. Flood kicks
            // with HardBan=false rely on the bot auto-sending unkick-player after the report lands.
            try { if (p != null && p.Owner != null && NetworkManagerNuclearOption.i != null) NetworkManagerNuclearOption.i.KickPlayer(p.Owner); }
            catch (Exception e) { Log?.LogError("Kick: " + e); }
        }

        // From the ReportKilled hook (every unit death): life-end bookkeeping, kill-data emit to the
        // bot, and teamkill enforcement when the top damager is a friendly player. One damageCredit
        // scan serves all of it. (The native killfeed announces every death in-game; the plugin
        // draws no feed lines of its own.)
        internal static void CheckTeamkill(Unit dead)
        {
            try
            {
                Trace("CheckTeamkill");
                if (dead == null) return;
                bool tkOn = (TeamkillEnforce == null || TeamkillEnforce.Value);
                Player victim = null; try { if (dead is Aircraft dv) victim = dv.Player; } catch { }
                if (victim != null && IsAdminEjecting(Sid(victim))) return;
                bool closedLife = false;   // 1.1.29: this death ended an OPEN life -> a genuine shot-down (not an already-ejected pilot's empty hull)
                // The pilot may have LEFT this airframe already (1.3.34: the stamp now means a LANDED
                // exit - airborne bails stamp nothing and their deaths book right here). The dedicated
                // server never unlinks Aircraft.Player, so the empty hull still names them as its
                // occupant - without the ledger this looked identical to dying in the seat. If they
                // exited THIS airframe on the ground, its destruction is not their death: the life
                // stays OPEN (LifeTick's despawn branch is stamp-aware and closes nothing).
                bool bailedOut = victim != null && BailedFrom(Sid(victim), AcIdOf(dead));
                if (bailedOut)
                    Log?.LogInfo($"[life] hull of {RawNameOf(victim)}'s exited airframe destroyed - not their death, life stays open");
                if (victim != null && !bailedOut)
                {
                    try { string vs = Sid(victim); var vl = LifeOf(vs); if (vl.alive) { EndLife(vs, vl, "death"); closedLife = true; } }
                    catch (Exception e) { Log?.LogError("life-on-death: " + e); }
                }
                // 1.1.37: no early tkOn return - Teamkill.Enforce=false must NOT silently kill the
                // bot's "down" kill-data. Enforcement AND collateral recording remain gated on tkOn below.
                FactionHQ deadHQ = null; try { deadHQ = dead.NetworkHQ; } catch { }

                // PERF: recordOnly mode runs JUST the scan + NoteKillForCollateral (so a human ENEMY kill is
                // recorded for the collateral verdict) then returns; pure AI-vs-AI stays cheap.
                bool recordOnly = false;
                bool deadHasHumans = false; bool anyHumans = false;
                if (deadHQ != null)
                    foreach (var hp in Humans()) { anyHumans = true; try { if (hp.HQ == deadHQ) { deadHasHumans = true; break; } } catch { } }
                if (!deadHasHumans)
                {
                    if (!anyHumans) return;            // nothing to enforce and nobody to credit
                    recordOnly = true;                 // enemy-AI death: record a human kill, skip the rest
                }

                // top damager from damageCredit (generic-typed fast path; boxed IDictionary fallback).
                // ONE argmax, shared with IsTopDamager, with the sub-1 noise floor applied inside it.
                object topKey; float top, dmgTotal;
                TopDamager(dead, out topKey, out top, out dmgTotal);
                if (topKey == null && dmgTotal > 0f)
                    Log?.LogInfo($"[kill] unattributed: best credit {dmgTotal:0.###} < {CreditNoiseFloor} "
                                 + "(seeker-lock noise, no real damage) - reporting as a crash");

                if (recordOnly && topKey == null) return;

                Player killer = null; FactionHQ killerHQ = null; string killerName = null; string dmgUnitName = null;
                if (topKey != null && UnitRegistry.TryGetPersistentUnit((PersistentID)topKey, out var pu))
                {
                    try { killer = pu.player; } catch { }
                    try { killerHQ = pu.GetHQ(); } catch { }
                    try { dmgUnitName = pu.definition != null ? SafeText(pu.definition.unitName) : null; } catch { }   // KILLER aircraft/SAM/unit name
                    try { killerName = (killer != null) ? RawNameOf(killer) : dmgUnitName; } catch { }
                }
                if (recordOnly && killer == null) return;

                // VICTIM unit-type name (used by the down line, dmgcal, and the collateral big-unit check).
                string deadName = null; try { deadName = dead.definition != null ? SafeText(dead.definition.unitName) : null; } catch { }
                // WEAPON (munition) resolution for the kill snapshot + moderation log: damageCredit names the
                // AIRCRAFT, the munition comes from the launch map. Fallback: the damaging unit (aircraft).
                string killWeapon = dmgUnitName ?? "";
                if (TryGetRecentLaunch(topKey, dead != null ? (object)dead.persistentID : null, top, out var kl0)
                      && !string.IsNullOrEmpty(kl0.weapon)) killWeapon = kl0.weapon;

                if (killer != null && DamageCalibration != null && DamageCalibration.Value && !string.IsNullOrEmpty(deadName))
                {
                    try { Log.LogInfo($"[dmgcal] t={Time.time:0.0} victim={deadName} total={dmgTotal:0} top={top:0} by={dmgUnitName ?? "?"} killer={RawNameOf(killer)}"); } catch { }
                }

                // COLLATERAL WINDOW: only TRUSTED kills enter (deliberate, weapon-resolved, above floor).
                // 1.1.37: tkOn-gated - collateral recording is enforcement machinery and stays off with Enforce=false.
                if (tkOn && !bailedOut && killer != null && killer != victim && killerHQ != null && deadHQ != null)
                {
                    string ksid = Sid(killer);
                    if (!string.IsNullOrEmpty(ksid) && ksid != "0")
                    {
                        bool winDelib; ClassifyTkMethod(dmgUnitName, out winDelib);
                        float winMinDmg = TeamkillMinDamage != null ? TeamkillMinDamage.Value : 0f;
                        bool winTrusted = winDelib && !string.IsNullOrEmpty(dmgUnitName)
                                          && !(winMinDmg > 0f && top < winMinDmg);
                        if (winTrusted)
                            NoteKillForCollateral(ksid, killerHQ != deadHQ, CachedIsBigObjective(deadName), deadName, top);
                        else if (killerHQ != deadHQ && DamageCalibration != null && DamageCalibration.Value)
                            Log?.LogInfo($"[tk] window-reject enemy kill {deadName ?? "?"} dmg={top:0} by {killerName ?? "?"} ({(!winDelib ? "auto-classified" : string.IsNullOrEmpty(dmgUnitName) ? "no-weapon" : "below-floor")})");
                    }
                }

                if (recordOnly) return;   // enemy-AI death recorded; no enforcement applies

                // KILL DATA -> bot: every human death with who/what downed them. Adds killer_plane / victim_plane /
                // weapon (contract) alongside the legacy "w" field.
                // 1.3.34: NOT for an abandoned hull (bailedOut) - a landed exit's later hull destruction
                // is not the pilot's death, so no down frame (which drives the panel's death cross) for it.
                if (victim != null && !bailedOut)
                {
                    bool kPlayer = killer != null && killer != victim;
                    string kdisp = kPlayer ? RawNameOf(killer) : (killerName ?? "");
                    bool ff = kPlayer && killerHQ != null && deadHQ != null && killerHQ == deadHQ;
                    Out("{\"t\":\"down\",\"v\":\"" + Sid(victim) + "\",\"vn\":\"" + Esc(RawNameOf(victim))
                        + "\",\"k\":\"" + Esc(kdisp) + "\",\"ks\":\"" + (kPlayer ? Sid(killer) : "")
                        + "\",\"kp\":" + (kPlayer ? 1 : 0) + ",\"ff\":" + (ff ? 1 : 0)
                        + ",\"w\":\"" + Esc(dmgUnitName ?? "")
                        + "\",\"killer_plane\":\"" + Esc(dmgUnitName ?? "")
                        + "\",\"victim_plane\":\"" + Esc(deadName ?? "")
                        + "\",\"weapon\":\"" + Esc(killWeapon) + "\"}");

                    // Death stamp (1.3.21 rule): EVERY death that closed a life gets stamped -
                    // closedLife means the pilot was still in the seat when the aircraft died (the
                    // bail ledger above already excluded the empty-hull case). The stamp is what
                    // tells the LifeTick wreck-despawn branch ~30s later (EjectIsDeathSequence) to
                    // close the life quietly instead of reading the despawn as a fresh voluntary
                    // bail and banking a SECOND life for the same death - the phantom class that
                    // was measured at 13-15% of all recorded lives. A clean voluntary MID-AIR bail
                    // also reaches here with closedLife true (its hull's ReportKilled books the
                    // death), so its wreck-despawn is silenced the same way. NoteDeathAnnounced is
                    // an idempotent timestamp write.
                    if (closedLife)
                        NoteDeathAnnounced(Sid(victim));
                }

                // teamkill enforcement: top damager is a friendly player (and not the victim own aircraft)
                // 1.3.34: !bailedOut - a friendly blowing up an ABANDONED hull is equipment damage, not a
                // teamkill of the pilot who walked away from it; filing it as one is a false accusation.
                if (tkOn && !bailedOut && killer != null && killerHQ != null && deadHQ != null && killerHQ == deadHQ && killer != victim)
                {
                    string sid = Sid(killer);
                    if (!string.IsNullOrEmpty(sid) && sid != "0")
                    {
                        bool deliberate; string method = ClassifyTkMethod(dmgUnitName, out deliberate);
                        string weapon = dmgUnitName ?? "";
                        float minDmg = TeamkillMinDamage != null ? TeamkillMinDamage.Value : 0f;
                        // noWeapon is computed on the AIRFRAME name, before the munition upgrade below, so the
                        // `nc` classification keeps exactly the meaning it has always had.
                        bool noWeapon = string.IsNullOrEmpty(weapon);
                        // MUNITION LOOKUP, HOISTED ABOVE THE REPORT/COUNTED SPLIT (2026-08-11).
                        // This used to live only in the counted branch, so every report-only row - auto-defence,
                        // below-floor, no-weapon - was filed carrying the damaging UNIT's name (the aeroplane)
                        // even when the launch tracker knew exactly which munition had been fired. That is what
                        // put "Alkyon AB-4" in a field the moderation panel presents as the weapon. Doing the
                        // lookup once, here, means both branches report the real munition whenever it is known.
                        // It is a reorder of an existing call, not a new hook: same lookup, one extra victim
                        // argument, and the >=500 gate still applies to the nuclear/munition paths.
                        // `weapon` now stays the DAMAGING UNIT; the munition lands in its own field.
                        // `tkYield` is consumed by the counted branch below for the nuclear test.
                        // Resolve into a SEPARATE field. Overwriting `weapon` (as the first cut did) threw
                        // away the damaging unit's identity - an auto-defence row could no longer name the
                        // SAM that fired - and let nc=="no-weapon" be filed next to a populated weapon name.
                        string munition = "";
                        float tkYield = 0f;
                        if (TryGetRecentLaunch(topKey, dead != null ? (object)dead.persistentID : null, top, out var tkLaunch)
                            && !string.IsNullOrEmpty(tkLaunch.weapon))
                        { munition = tkLaunch.weapon; tkYield = tkLaunch.yield; }
                        bool belowFloor = minDmg > 0f && top < minDmg;
                        Log?.LogInfo($"[tk] friendly kill by {RawNameOf(killer)} -> {RawNameOf(victim)} dmg={top:0} method={(method.Length > 0 ? method : "?")} weapon={(weapon.Length > 0 ? weapon : "-")} munition={(munition.Length > 0 ? munition : "-")}");
                        if (!deliberate || noWeapon || belowFloor)
                        {
                            // REPORT-ONLY: flag in Moderation, never warn/kick/ban, never count. Deduped per blast.
                            string ncReason = !deliberate ? "auto" : (noWeapon ? "no-weapon" : "below-floor");
                            float nowR = Time.time;
                            float rspan = HasLiveNuclearLaunch(topKey) ? 40f : TK_EVENT_DEDUP;
                            bool rdup = _tkReportStart.TryGetValue(sid, out var rs) && Mathf.Abs(nowR - rs) < rspan;
                            if (!rdup)
                            {
                                _tkReportStart[sid] = nowR;
                                EmitTkMod(sid, killer, "report", 0,
                                    new TkEvent { sid = sid, victim = RawNameOf(victim), method = method, weapon = weapon, munition = munition, dmg = top, eventT = nowR }, ncReason);
                            }
                            Log?.LogInfo($"[tk] NOT counted ({ncReason}) - friendly kill by {RawNameOf(killer)} -> {RawNameOf(victim)} (dmg={top:0})");
                        }
                        else
                        {
                            // COLLATERAL CHECK: defer the verdict by a munition-sized forward window (nuclear = long).
                            // `weapon` and tkYield were already resolved above the split - the lookup used to be
                            // duplicated here, which is why only counted rows ever named the munition.
                            float yield = tkYield;
                            bool nuclear = yield > 200f;   // the game own Shockwave-spawn threshold
                            float backS = TeamkillCollateralWindow != null ? Mathf.Clamp(TeamkillCollateralWindow.Value, 0.5f, 10f) : 2.5f;
                            float fwdS = nuclear
                                ? (TeamkillCollateralWindowNuclear != null ? Mathf.Clamp(TeamkillCollateralWindowNuclear.Value, 5f, 40f) : 20f)
                                : backS;
                            if (nuclear) backS = fwdS;
                            // Same-blast merge: a second friendly victim of the SAME blast joins the pending verdict.
                            bool merged = false;
                            float nowQ = Time.time;
                            for (int i = 0; i < _tkJudge.Count; i++)
                            {
                                var q = _tkJudge[i];
                                if (q.sid == sid && nowQ - q.eventT < Mathf.Max(TK_EVENT_DEDUP, q.fwd))
                                {
                                    string vn = RawNameOf(victim);
                                    if (!string.IsNullOrEmpty(vn))
                                    {
                                        if (q.victims == null) q.victims = new List<string>();
                                        if (!string.IsNullOrEmpty(q.victim) && q.victims.Count == 0) q.victims.Add(q.victim);
                                        q.victims.Add(vn);
                                    }
                                    if (top > q.dmg) q.dmg = top;
                                    if (fwdS > q.fwd) { q.fwd = fwdS; q.back = Mathf.Max(q.back, backS); q.dueAt = q.eventT + fwdS; }
                                    merged = true; break;
                                }
                            }
                            if (merged) { }
                            else if (_tkJudge.Count < TK_QUEUE_MAX)
                            {
                                _tkJudge.Add(new TkPending { sid = sid, victim = RawNameOf(victim), method = method, weapon = weapon, munition = munition,
                                                             dmg = top, eventT = Time.time, dueAt = Time.time + fwdS, back = backS, fwd = fwdS });
                                Log?.LogInfo($"[tk] friendly kill by {RawNameOf(killer)} -> {RawNameOf(victim)} ({method}{((munition.Length > 0 ? munition : weapon).Length > 0 ? " " + (munition.Length > 0 ? munition : weapon) : "")}{(nuclear ? ", NUCLEAR" : "")}) - collateral verdict in {fwdS:0.#}s");
                            }
                            else
                            {
                                float nowT = Time.time;
                                bool fdup = _tkEventStart.TryGetValue(sid, out var fs) && Mathf.Abs(nowT - fs) < TK_EVENT_DEDUP;
                                if (!fdup)
                                {
                                    _tkEventStart[sid] = nowT;
                                    if (_tkQueue.Count < TK_QUEUE_MAX)
                                        _tkQueue.Add(new TkEvent { sid = sid, victim = RawNameOf(victim), method = method, weapon = weapon, munition = munition, dmg = top, eventT = nowT });
                                }
                                Log?.LogInfo($"[tk] judge queue FULL - counted {RawNameOf(killer)} -> {RawNameOf(victim)} via the legacy path (no collateral verdict)");
                            }
                        }
                    }
                }
            }
            catch (Exception e) { Log?.LogError("CheckTeamkill: " + e); }
        }

        // Off HQTick: judge collateral verdicts, escalate queued teamkills, fire delayed kicks, enforce bans + rank-0.
        internal static void TkTick()
        {
            float now = Time.time;
            // ---- collateral verdicts: judge friendly kills whose window has elapsed (oldest first). ----
            if (_tkJudge.Count > 0)
            {
                bool anyDue = false;
                for (int i = 0; i < _tkJudge.Count; i++) if (now >= _tkJudge[i].dueAt) { anyDue = true; break; }
                if (anyDue)
                {
                    var due = new List<TkPending>();
                    for (int i = _tkJudge.Count - 1; i >= 0; i--)
                        if (now >= _tkJudge[i].dueAt) { due.Add(_tkJudge[i]); _tkJudge.RemoveAt(i); }
                    due.Reverse();                                    // oldest first (dedup anchor = first victim)
                    bool enforce = TeamkillCollateralEnforce != null && TeamkillCollateralEnforce.Value;
                    bool bigExempt = TeamkillBigUnitExempt == null || TeamkillBigUnitExempt.Value;
                    int silentMin = TeamkillSilentMinEnemies != null ? TeamkillSilentMinEnemies.Value : 10;
                    float silentRatio = TeamkillSilentRatio != null ? Mathf.Max(1f, TeamkillSilentRatio.Value) : 5f;
                    int capMax = TeamkillCollateralMaxPerMatch != null ? TeamkillCollateralMaxPerMatch.Value : 3;
                    foreach (var p in due)
                    {
                        int enemies = 0, friendlies = 0; bool bigEnemy = false;
                        List<KillRec> units = null;
                        if (_killWin.TryGetValue(p.sid, out var kl))
                            foreach (var k in kl)
                                if (k.t >= p.eventT - p.back && k.t <= p.eventT + p.fwd)
                                {
                                    if (k.enemy) { enemies++; if (k.big) bigEnemy = true; }
                                    else friendlies++;
                                    if (enforce) (units = units ?? new List<KillRec>()).Add(k);
                                }
                        string verdict = (bigEnemy && bigExempt) ? "big-unit"
                                       : (enemies >= friendlies && enemies > 0 ? "collateral" : "deliberate");
                        bool silent = verdict == "collateral"
                                   && silentMin > 0 && enemies >= silentMin && enemies >= silentRatio * friendlies;
                        if (verdict != "deliberate" && capMax > 0 && enforce)
                        {
                            _tkCollateralCount.TryGetValue(p.sid, out var cc);
                            if (cc >= capMax)
                            {
                                if (silent) { silent = false; Log?.LogInfo($"[tk] collateral cap reached for {p.sid} ({cc}/{capMax}) -> silent verdict downgraded to logged"); }
                                else { Log?.LogInfo($"[tk] collateral cap reached for {p.sid} ({cc}/{capMax} this match) -> treating as deliberate"); verdict = "deliberate"; }
                            }
                            else _tkCollateralCount[p.sid] = cc + 1;
                        }
                        string method = (enemies + friendlies) >= 2 ? "splash" : p.method;
                        string victimList = p.victims != null ? string.Join(", ", p.victims) : p.victim;
                        Log?.LogInfo($"[tk] collateral-check {p.sid} -> {victimList}: enemies={enemies} friendlies={friendlies} big={(bigEnemy ? 1 : 0)} dmg={p.dmg:0} method={method} -> {verdict}{(silent ? " (silent)" : "")}{(enforce ? "" : " (log-only)")}");
                        if (verdict != "deliberate" && enforce)
                        {
                            _tkEventStart[p.sid] = p.eventT;
                            if (silent)
                            {
                                _tkCollatStart[p.sid] = p.eventT;
                                _tkReportStart[p.sid] = p.eventT;
                                continue;
                            }
                            bool rdup = _tkCollatStart.TryGetValue(p.sid, out var rs) && Mathf.Abs(p.eventT - rs) < TK_EVENT_DEDUP;
                            if (!rdup)
                            {
                                _tkCollatStart[p.sid] = p.eventT;
                                _tkReportStart[p.sid] = p.eventT;
                                EmitTkMod(p.sid, FindPlayerBySid(p.sid), "report", 0,
                                    new TkEvent { sid = p.sid, victim = victimList, method = method, weapon = p.weapon, munition = p.munition, dmg = p.dmg, eventT = p.eventT },
                                    verdict, units);
                            }
                            continue;
                        }
                        bool dup = _tkEventStart.TryGetValue(p.sid, out var startT)
                                && Mathf.Abs(p.eventT - startT) < Mathf.Max(TK_EVENT_DEDUP, p.back + p.fwd);
                        if (dup)
                        {
                            Log?.LogInfo($"[tk] {p.sid} -> {victimList} -- same event, deduped (already handled this blast)");
                        }
                        else
                        {
                            _tkEventStart[p.sid] = p.eventT;
                            if (_tkQueue.Count < TK_QUEUE_MAX)
                                _tkQueue.Add(new TkEvent { sid = p.sid, victim = victimList, method = method,
                                                           weapon = p.weapon, munition = p.munition, dmg = p.dmg, eventT = p.eventT });
                        }
                    }
                }
            }
            if (_tkQueue.Count > 0)
            {
                var batch = new List<TkEvent>(_tkQueue); _tkQueue.Clear();
                foreach (var ev in batch)
                {
                    string sid = ev.sid;
                    int n = (_tkCount.TryGetValue(sid, out var c) ? c : 0) + 1;
                    _tkCount[sid] = n;
                    var p = FindPlayerBySid(sid);
                    if (n == 1)
                    {
                        if (p != null)
                        {
                            AdminEject(p);   // life-neutral: a teamkill-warning eject must not end a life
                            Instance?.TellPlayer(p, "<color=#FF5555>FRIENDLY FIRE - first warning.</color> <color=#FFD200>Check your targets. Do it again this match and you'll be removed.</color>");
                        }
                        EmitTkMod(sid, p, "warn", n, ev);
                        Log?.LogInfo($"[tk] warn+eject {sid} (1)");
                    }
                    else if (n == 2)
                    {
                        _tkRankZero.Add(sid);
                        if (p != null) Instance?.TellPlayer(p, "<color=#FF5555>FRIENDLY FIRE - KICKED</color> <color=#FFD200>(2nd offense). Rank resets to 0 on rejoin. Next is a BAN.</color>");
                        _tkKicks.Add(new KeyValuePair<string, float>(sid, now + 2.5f));   // let the message land, then kick
                        EmitTkMod(sid, p, "kick", n, ev);
                        Log?.LogInfo($"[tk] kick {sid} (2)");
                    }
                    else
                    {
                        _tkBanned.Add(sid); SaveBans();
                        if (p != null) Instance?.TellPlayer(p, "<color=#FF0000>BANNED for repeated team killing.</color>");
                        _tkKicks.Add(new KeyValuePair<string, float>(sid, now + 2.5f));
                        EmitTkMod(sid, p, "ban", n, ev);
                        Log?.LogInfo($"[tk] BAN {sid} (3+)");
                    }
                }
            }
            if (_tkKicks.Count > 0)
                for (int i = _tkKicks.Count - 1; i >= 0; i--)
                    if (now >= _tkKicks[i].Value) { var k = _tkKicks[i]; _tkKicks.RemoveAt(i); Kick(FindPlayerBySid(k.Key)); }
            if (now < _nextTkScan) return;
            _nextTkScan = now + 2f;
            // prune stale collateral-window entries (horizon must outlive the longest nuclear verdict window).
            try
            {
                foreach (var kv in _killWin)
                {
                    var l = kv.Value;
                    for (int i = l.Count - 1; i >= 0; i--)
                        if (now - l[i].t > 60f) l.RemoveAt(i);
                }
            }
            catch { }
            try
            {
                foreach (var p in Humans())
                {
                    string sid = Sid(p);
                    if (string.IsNullOrEmpty(sid) || sid == "0") continue;
                    if (_tkBanned.Contains(sid)) { Kick(p); continue; }                  // enforce ban on rejoin
                    if (_tkRankZero.Contains(sid)) { try { p.SetRank(0, true); } catch { } _tkRankZero.Remove(sid); Log?.LogInfo($"[tk] rank->0 {RawNameOf(p)}"); }
                }
            }
            catch (Exception e) { Log?.LogError("TkTick: " + e); }
        }

        // ===== ANTI-GRIEF (1.2.4): THERE IS NO ORDER-RATE KICK PATH AT ALL any more. Layer A owned it and layer A
        // is gone (see the tombstone above FleetOrderFloodPatch): the game already caps move commands at ~5
        // accepted RPCs/s per player, ours counted clicks rather than wire RPCs, and it false-kicked an honest
        // player. GriefTick therefore no longer kicks or reports anything — it survives ONLY to roll
        // _orderAttempts into _griefStreak once a second, which NetHealthTick emits as the net frame's "streak"
        // field, plus the Command.DiagLog diagnostic line. NOTHING is triggered by the AMOUNT of units a player
        // owns either (retired in 1.2.0). The kick paths that remain live elsewhere: the dead-unit exploit strike
        // kick (NoteStaleNetIdRpc, layer B), the inbound-RPC flood kick (layer D) and the overflow-source kick
        // (layer E) — none of them route through here. Grief.ReportOnly + Grief.ExemptAdmins are LIVE
        // and gate layer B. Fail-open. =====
        internal static ConfigEntry<bool> GriefReportOnly, GriefExemptAdmins;

        // Position-feed enrichment — EnrichPos read by PosTick (adds y / ac / g to each t:pos player).
        internal static ConfigEntry<bool>  AnomalyEnrichPos;
        static readonly Dictionary<string, int>   _orderAttempts = new Dictionary<string, int>();   // CmdSetDestination attempts since last GriefTick
        static readonly Dictionary<string, int>   _griefStreak   = new Dictionary<string, int>();    // consecutive high-rate ticks per player — TELEMETRY ONLY (net frame "streak"); drives no action since 1.2.4
        static float _nextGriefScan, _lastGriefScan;
        const float GRIEF_INTERVAL = 1f;   // 1s window: the cadence at which order attempts roll into _griefStreak
        // 1.2.4: the telemetry-only trip line for _griefStreak. Was Flood.FleetOrdersPerSec, which no longer
        // exists. Set to vanilla Mirage's own ceiling (~5 accepted move-RPCs/s per player) so "streak" means
        // "this player is running at the rate the GAME will start dropping at", not "this player is in trouble" —
        // nothing is enforced off it. Do not lower it: NoteOrderAttempt now fires once per unit-RPC rather than
        // once per click, so a 6-unit group order legitimately reads 6.
        const int ORDER_RATE_TELEMETRY_TRIP = 5;

        // Called once per unit move-order RPC (1.2.4: no longer coalesced per click — layer A's coalescer went
        // with it), plus once per policy-dropped order. Feeds GriefTick's rate view and the net-health counter.
        internal static void NoteOrderAttempt(Player p)
        {
            try
            {
                if (p == null) return; string id = Sid(p);
                if (string.IsNullOrEmpty(id) || id == "0") return;
                _orderAttempts[id] = (_orderAttempts.TryGetValue(id, out var c) ? c : 0) + 1;
                try { _netOrders[id] = (_netOrders.TryGetValue(id, out var no) ? no : 0) + 1; } catch { }   // net-health: per-player order count since last emit (reset each emit)
            }
            catch { }
        }

        internal static void GriefTick()
        {
            try
            {
                if (CommandDiagLog == null) return;                // not yet bound
                float now = Time.time;
                if (now < _nextGriefScan) return;
                _nextGriefScan = now + GRIEF_INTERVAL;
                float elapsed = Mathf.Max(0.5f, now - _lastGriefScan);   // REAL window (a lag hitch can exceed GRIEF_INTERVAL)
                _lastGriefScan = now;

                bool diag         = CommandDiagLog != null && CommandDiagLog.Value;

                // snapshot + reset the per-player order-attempt counters for this window
                var attempts = new Dictionary<string, int>(_orderAttempts);
                _orderAttempts.Clear();

                // TELEMETRY ONLY (1.2.4): count consecutive seconds strictly above the vanilla ceiling. Emitted as
                // the net frame's "streak"; no kick, no report, no drop is driven by it anywhere.
                var streakKeys = new List<string>(_griefStreak.Keys);
                foreach (var id in streakKeys) if (!attempts.ContainsKey(id)) _griefStreak[id] = 0;   // decay idle
                foreach (var kv in attempts)
                {
                    float rate = kv.Value / elapsed;
                    _griefStreak[kv.Key] = rate > ORDER_RATE_TELEMETRY_TRIP
                        ? (_griefStreak.TryGetValue(kv.Key, out var st) ? st : 0) + 1
                        : 0;
                }

                if (!diag) return;   // the streak roll above is the whole job unless we are diagnosing

                // Owned-GroundVehicle count per player, ONE pass over allUnits. 1.2.0: DIAGNOSTIC ONLY — nothing
                // is triggered by it any more, so it is skipped entirely unless Command.DiagLog is on (it used to
                // run every second on every server just to feed a threshold that no longer exists).
                var ownedCount = new Dictionary<Player, int>();
                if (diag)
                {
                    try
                    {
                        foreach (var u in UnitRegistry.allUnits)
                            if (u is GroundVehicle gv)
                            {
                                var ow = SafeOwner(gv);
                                if (ow != null) ownedCount[ow] = (ownedCount.TryGetValue(ow, out var oc) ? oc : 0) + 1;
                            }
                    }
                    catch { }
                }

                foreach (var p in Humans())
                {
                    string sid = Sid(p);
                    if (string.IsNullOrEmpty(sid) || sid == "0") continue;
                    if (_tkBanned.Contains(sid)) continue;        // already banned; TkTick enforces it

                    int owned = ownedCount.TryGetValue(p, out var oc2) ? oc2 : 0;
                    int streak  = _griefStreak.TryGetValue(sid, out var s2) ? s2 : 0;
                    int rateNow = attempts.TryGetValue(sid, out var a2) ? (int)(a2 / elapsed) : 0;

                    // 1.2.4: this whole loop is a LOG LINE and nothing else. owned= has been diagnostic-only since
                    // 1.2.0, and as of 1.2.4 rate=/streak= are too — the kick that used to follow went with
                    // layer A. Nobody is kicked for ordering units, at any rate.
                    if (owned > 0 || rateNow > 0)
                        Log?.LogInfo($"[grief] {RawNameOf(p)} ({sid}) owned={owned} rate={rateNow}/s streak={streak} (DIAGNOSTIC ONLY — no action is taken on any of these)");
                }
            }
            catch (Exception e) { Log?.LogError("GriefTick: " + e); }
        }

        // ================= force-move / spectate + PvP auto-balance =================
        internal static ConfigEntry<bool> AutoMove;            // 1.2.0: MoveOnlyUnspawned deleted (never read; misleading)
        internal static ConfigEntry<int>  RecheckSeconds, MoveDebounce, BalanceMoveExemptGames;   // 1.2.0: BalanceGraceSeconds deleted (superseded by WarnSeconds)
        internal static ConfigEntry<int>  BalanceMinPlayers, BalanceWarnSeconds;   // never balance under MinPlayers; warn WarnSeconds before moving
        internal static ConfigEntry<bool> BalanceMoveOnlyGrounded;                 // defer a balance move until the pick lands/dies
        internal static ConfigEntry<int>  BalancePendingTimeout;                   // ...but not forever
        internal static ConfigEntry<bool> BalanceNeverMoveTop;                     // #1 on the points board is never moved
        internal static ConfigEntry<int>  BalanceNewJoinerSeconds;  // new-joiner protection window

        // numeric server-rank weight per SteamID (1..11), from plugin_ranks.txt 4th field.
        static readonly Dictionary<string, int> RankWeight = new Dictionary<string, int>();
        static float Weight(Player p)
        {
            try
            {
                LoadRankMap(); var id = Sid(p);                            // server-rank weight
                if (RankWeight.TryGetValue(id, out var w)) return w;
            }
            catch { }
            return 1f;   // last resort
        }

        static Player FindPlayerBySid(string sid)
        {
            if (string.IsNullOrEmpty(sid)) return null;
            foreach (var p in Humans()) if (Sid(p) == sid) return p;
            return null;
        }

        // ---- command channel (command centre -> bot -> here) ----
        // The bot drops ONE file per command in the game root: "plugin_cmd_<id>.txt" holding
        // "verb|steamId|faction". We process and DELETE each (so there's no dedup/replay to get
        // wrong). Writing those files needs SFTP/console access, so they're implicitly trusted.
        // A standalone persistent ticker so periodic plugin work keeps running even when HQ.Update is
        // absent (empty server, mission/scene transition, or a built-in PvP state that no longer ticks an
        // HQ). The HQ hook remains the fast path; this fallback ticks at ~2 Hz and PeriodicTick has a
        // per-frame guard, so being driven from both places is safe.
        internal class Ticker : MonoBehaviour
        {
            float _next;
            // WALL clock (2026-07-28): this is the fallback driver for exactly the case where
            // the mission is paused/stopped - and Time.time FREEZES there, so gating on it
            // stopped the fallback in the one situation it exists for. Nothing periodic ran on
            // an empty server: no command polling, no annihilate tick, no name pumps.
            void Update() { try { float now = Time.realtimeSinceStartup; if (now >= _next) { _next = now + 0.5f; PeriodicTick(); } } catch { } }
        }

        // EMPTY-SERVER CONFIG POLLER (1.2.0). PROVEN 2026-07-28: with no mission running the
        // Ticker's Update never executes and FactionHQ.Update has nothing to tick, so the
        // main-thread PollCommands has NO driver and panel changes queue up forever. This
        // thread consumes ONLY config commands - ConfigEntry writes and Config.Save are plain
        // C#, no Unity API - and deliberately leaves every player-targeted command on disk for
        // the main-thread poll, since kick/ban/tell/move mean nothing with nobody on.
        static System.Threading.Thread _cfgPollThread;
        static volatile bool _cfgPollStop;

        static void StartConfigPoller()
        {
            try
            {
                if (_cfgPollThread != null) return;
                _cfgPollThread = new System.Threading.Thread(ConfigPollLoop);
                _cfgPollThread.IsBackground = true;      // never keeps the process alive
                _cfgPollThread.Name = "NukeStatsConfigPoll";
                _cfgPollThread.Start();
                Log?.LogInfo("[diag] config poller up (applies setcfg with nobody on; player-targeted commands still wait for the main thread)");
            }
            catch (Exception e) { Log?.LogWarning("config poller failed to start: " + e.Message); }
        }

        static void ConfigPollLoop()
        {
            while (!_cfgPollStop)
            {
                try
                {
                    System.Threading.Thread.Sleep(1000);
                    string root = Paths.GameRootPath;
                    if (string.IsNullOrEmpty(root)) continue;
                    string[] files;
                    try { files = Directory.GetFiles(root, "plugin_cmd_*.txt"); }
                    catch { continue; }
                    Array.Sort(files, StringComparer.Ordinal);
                    foreach (var f in files)
                    {
                        string body;
                        try { body = File.ReadAllText(f).Trim(); }
                        catch { continue; }            // still being written - next pass
                        string verb = body.Split('|')[0].Trim().ToLowerInvariant();
                        // aircraftlist joins setcfg/dumpcfg on this thread because it targets NO player -
                        // it only reads the Encyclopedia and writes a log line. Left to the main thread it
                        // would sit unconsumed on an idle server (the main driver needs someone online),
                        // which is exactly what happened the first time it was asked for.
                        if (verb != "setcfg" && verb != "dumpcfg" && verb != "aircraftlist")
                            continue;                                         // not ours: leave for the main thread
                        bool ok = false;
                        try { ok = ApplyConfigCommand(body); }
                        catch (Exception e) { Log?.LogError("config poll apply: " + e); }
                        if (ok)
                        {
                            try { File.Delete(f); } catch { }
                        }
                    }
                }
                catch { }                              // a poll failure must never kill the thread
            }
        }

        // Config-only subset of the command handler, safe off the main thread. Returns true when
        // the file may be consumed. dumpcfg is acknowledged but left to the main thread to EMIT
        // (that path logs through Unity), so the value is applied now and the panel catches up.
        static bool ApplyConfigCommand(string body)
        {
            var parts = body.Split('|');
            string verb = parts[0].Trim().ToLowerInvariant();
            if (verb == "aircraftlist")
            {
                // Encyclopedia.i is a plain static asset reference and the catalogue is read-only, so
                // this is safe off the main thread - no Unity object is created, moved or destroyed.
                LogAircraftCatalog("admin request");
                return true;
            }
            // Handle it HERE. Returning false left the command for the main thread, which has no driver on
            // an empty server - so on an idle server the request was never answered and its plugin_cmd
            // file was never consumed, accumulating forever. Answering on an empty server is the config
            // poller's entire reason to exist, and DumpCfg is the same code path SetCfg already runs
            // safely on this thread.
            if (verb == "dumpcfg") { try { DumpCfg(); } catch (Exception e) { Log?.LogError("dumpcfg: " + e); } return true; }
            if (verb != "setcfg" || parts.Length < 3) return false;
            string key = parts[1].Trim(), val = parts[2].Trim();
            string result = SetCfg(key, val);          // returns "" / "ok"-ish on success, a reason on failure
            Log?.LogInfo($"[cfgpoll] {key} = {val} -> {(string.IsNullOrEmpty(result) ? "applied (server empty)" : result)}");
            return true;                                // consume either way; a bad key must not loop
        }

        static float _nextCmdPoll;
        internal static void PollCommands()
        {
            try
            {
                // WALL clock, not Time.time (regression re-found 2026-07-28): with nobody on, the
                // new game pauses the mission and Time.time FREEZES, so `now < _nextCmdPoll` stayed
                // true forever and queued panel commands sat unconsumed until someone joined -
                // exactly when an admin is most likely to be tuning the server. realtimeSinceStartup
                // keeps advancing whatever the mission clock does.
                float now = Time.realtimeSinceStartup;
                PumpPending(now);                                        // run any due delayed moves
                PumpSwaps(now);                                          // advance any in-progress !swapteam/!forceteamswap
                if (now < _nextCmdPoll) return;
                _nextCmdPoll = now + 1f;                                 // glance at the drop folder ~1/sec
                TrackPresence(now);                                      // ~1/sec: maintain each player's join clock (new-joiner protection)
                MaybeWelcome(now);                                       // ~1/sec: fire the one-time private "plugin vX is active" notice
                string[] files;
                try { files = Directory.GetFiles(Paths.GameRootPath, "plugin_cmd_*.txt"); }
                catch { return; }
                if (files.Length == 0) return;
                Array.Sort(files, StringComparer.Ordinal);              // id-prefixed name => chronological
                foreach (var f in files)
                {
                    try { foreach (var raw in File.ReadAllLines(f)) { var l = raw.Trim(); if (l.Length > 0 && l[0] != '#') ExecCommand(l); } }
                    catch (Exception e) { Log?.LogError("cmd read: " + e); }
                    try { File.Delete(f); } catch (Exception e) { Log?.LogError("cmd delete: " + e); }
                }
            }
            catch (Exception e) { Log?.LogError("PollCommands: " + e); }
        }

        // ===================== LIVE CONFIG (webcc settings menu) =====================
        // The webcc settings menu reads/writes plugin tunables WITHOUT a redeploy. We drive this
        // generically off BepInEx's own ConfigFile.Keys, so EVERY Config.Bind key (and any future
        // one) is covered automatically — no hand-maintained registry to drift. DumpCfg emits the
        // current values as one [NOSTATS] {"t":"cfg",...} line the bot tails; SetCfg type-parses +
        // applies a value live (ConfigEntry.BoxedValue) and Config.Save()s it. Range validation is
        // done UPSTREAM in cc_web against the shipped settings catalogue, so here we only type-parse.
        // NOTE: Flood.DropDeadNetIdRpcs applies LIVE — the guard-B Harmony patch is installed
        // unconditionally at load and reads its .Value inside the prefix each call, so toggling it
        // takes effect immediately. The ONE caveat: DropDeadNetIdRpcs fails open, so if
        // RpcHandler.HandleRpc never bound at load, turning it on later cannot retro-install the patch.
        static ConfigFile _cfgFile;   // cached in Awake; survives the plugin GameObject's destruction (Instance.Config would read Unity-null)
        static string CfgKey(ConfigDefinition d) => d.Section + "." + d.Key;
        static void AppendJsonVal(StringBuilder sb, object v)
        {
            if (v is bool b) sb.Append(b ? "true" : "false");
            else if (v is int || v is long || v is short || v is byte) sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
            else if (v is float f) sb.Append((float.IsNaN(f) || float.IsInfinity(f)) ? "0" : f.ToString("R", CultureInfo.InvariantCulture));
            else if (v is double d) sb.Append((double.IsNaN(d) || double.IsInfinity(d)) ? "0" : d.ToString("R", CultureInfo.InvariantCulture));
            else { sb.Append('"').Append((v != null ? v.ToString() : "").Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"'); }
        }
        internal static void DumpCfg()
        {
            try
            {
                if (_cfgFile == null) return;
                var sb = new StringBuilder("{\"t\":\"cfg\",\"v\":{");
                bool first = true;
                foreach (var def in _cfgFile.Keys)
                {
                    var e = _cfgFile[def]; if (e == null) continue;
                    if (!first) sb.Append(','); first = false;
                    sb.Append('"').Append(CfgKey(def)).Append("\":");
                    AppendJsonVal(sb, e.BoxedValue);
                }
                sb.Append("}}");
                Out(sb.ToString());
            }
            catch (Exception ex) { Log?.LogError("DumpCfg: " + ex); }
        }
        // returns null on success, else a short error code.
        internal static string SetCfg(string key, string raw)
        {
            try
            {
                if (_cfgFile == null) return "no-config";
                if (string.IsNullOrEmpty(key)) return "no-key";
                foreach (var def in _cfgFile.Keys)
                {
                    if (!CfgKey(def).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                    var e = _cfgFile[def];
                    var t = e.SettingType;
                    object val;
                    if (t == typeof(bool)) { string s = (raw ?? "").Trim().ToLowerInvariant(); val = (s == "1" || s == "true" || s == "on" || s == "yes"); }
                    else if (t == typeof(int)) { if (!int.TryParse((raw ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return "bad-int"; val = i; }
                    else if (t == typeof(float)) { if (!float.TryParse((raw ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ff)) return "bad-float"; val = ff; }
                    else val = raw ?? "";
                    e.BoxedValue = val;
                    _cfgFile.Save();
                    Log?.LogInfo($"[cfg] set {CfgKey(def)} = {val}");
                    DumpCfg();                                   // re-broadcast so the bot/webcc reflect the new value immediately
                    return null;
                }
                return "unknown-key";
            }
            catch (Exception ex) { Log?.LogError("SetCfg: " + ex); return "error"; }
        }

        static void ExecCommand(string line)
        {
            Trace("ExecCommand");
            try
            {
                var parts = line.Split('|');
                string verb = parts.Length > 0 ? parts[0].Trim().ToLowerInvariant() : "";
                if (verb.Length > 0 && verb.Length <= 24) Trace("ExecCommand_" + verb);   // per-verb coverage (bot/panel verbs are a small fixed set)
                Log?.LogInfo($"[cmd] recv: {(verb == "tell" ? "tell|…" : line)}");
                if (verb == "balance") { int n = BalanceOnce(true); Log?.LogInfo($"[cmd] balance -> {n} move(s)"); return; }
                if (verb == "dumpcfg") { DumpCfg(); return; }                  // webcc settings menu: re-emit current config
                // TARGET-LESS, so it must sit ABOVE the sid resolution + `target == null` guard below.
                // It used to sit under that guard, which made it dead for the only payload any producer
                // sends (aircraftlist|| - no sid), while the main-thread poll consumed and deleted the
                // command file, so the panel button did nothing at all.
                if (verb == "aircraftlist") { LogAircraftCatalog("admin request"); return; }
                if (verb == "ban" || verb == "unban")                          // webcc Reports tab: ban/unban a SteamID (immediate)
                {
                    string bsid = parts.Length > 1 ? parts[1].Trim() : "";
                    if (bsid.Length == 0) return;
                    if (verb == "ban")
                    {
                        _tkBanned.Add(bsid); SaveBans();
                        var bp = FindPlayerBySid(bsid);
                        if (bp != null) { try { Instance?.TellPlayer(bp, "<color=#FF0000>You have been banned from this server.</color>"); } catch { } _tkKicks.Add(new KeyValuePair<string, float>(bsid, Time.time + 1.5f)); }
                        Log?.LogInfo($"[cmd] BAN {bsid} (online={(bp != null)})");
                    }
                    else { _tkBanned.Remove(bsid); SaveBans(); Log?.LogInfo($"[cmd] UNBAN {bsid}"); }
                    return;
                }
                if (verb == "kick")                                            // anti-grief auto-kick (recoverable; NOT a ban). Used by the bot's command-flood detector.
                {
                    string ksid = parts.Length > 1 ? parts[1].Trim() : "";
                    if (ksid.Length == 0) return;
                    var kp = FindPlayerBySid(ksid);
                    if (kp != null) { try { Instance?.TellPlayer(kp, "<color=#FF0000>Removed: command flooding (server protection).</color>"); } catch { } _tkKicks.Add(new KeyValuePair<string, float>(ksid, Time.time + 1.0f)); }
                    Log?.LogInfo($"[cmd] KICK {ksid} (online={(kp != null)})");
                    return;
                }
                if (verb == "setcfg")                                          // webcc settings menu: setcfg|Section.Key|value
                {
                    string ck = parts.Length > 1 ? parts[1].Trim() : "";
                    string cv = parts.Length > 2 ? parts[2].Trim() : "";
                    var cerr = SetCfg(ck, cv);
                    Log?.LogInfo($"[cmd] setcfg {ck}={cv} -> {(cerr ?? "ok")}");
                    return;
                }
                if (verb == "tell")                                     // private message to one player (cuts chat spam)
                {
                    string tsid = parts.Length > 1 ? parts[1].Trim() : "";
                    string body = parts.Length > 2 ? string.Join("|", parts, 2, parts.Length - 2) : "";
                    var pl = FindPlayerBySid(tsid);
                    if (pl == null) { Log?.LogInfo($"[cmd] tell: player {tsid} not found ({Humans().Count} humans online)"); return; }
                    if (pl.Owner == null) { Log?.LogWarning($"[cmd] tell: {tsid} found but .Owner is null - cannot target"); return; }
                    Log?.LogInfo($"[cmd] tell -> {tsid} (Owner ok), delivering");
                    // 1.1.30: was `if (Instance != null)` - Unity fake-null, ALWAYS false once the
                    // plugin GameObject died, so panel/bot 'tell' silently delivered nothing.
                    // `?.` is a C# reference-null check and survives the destroyed GameObject.
                    foreach (var ln in body.Split('\u001f'))
                        if (!string.IsNullOrEmpty(ln)) Instance?.TellPlayer(pl, ln);
                    return;
                }
                string sid = parts.Length > 1 ? parts[1].Trim() : "";
                var target = FindPlayerBySid(sid);
                if (target == null) { Log?.LogInfo($"[cmd] {verb}: player {sid} not found/offline"); return; }
                if (verb == "spec" || verb == "spectate" || verb == "unteam")
                {
                    Instance?.RequestMove(target, null, true);          // immediate (ejects if flying)
                    return;
                }
                if (verb == "help")                                     // private command list, delivered like !spec's reply
                {
                    Instance?.SendHelp(target);
                    return;
                }
                if (verb == "move" || verb == "join" || verb == "team")
                {
                    var hq = FindFaction(parts.Length > 2 ? parts[2].Trim() : "");
                    if (hq == null) { Log?.LogInfo($"[cmd] {verb}: unknown faction '{(parts.Length > 2 ? parts[2] : "")}'"); return; }
                    Instance?.RequestMove(target, hq, false);
                    return;
                }
                if (verb == "setrank")                                  // setrank|sid|N  -> set in-game rank
                {
                    if (parts.Length > 2 && int.TryParse(parts[2].Trim(), out int rk)) Instance?.SetPlayerRank(target, rk);
                    else Log?.LogInfo($"[cmd] setrank: bad rank '{(parts.Length > 2 ? parts[2] : "")}'");
                    return;
                }
                if (verb == "setfunds" || verb == "addfunds")           // setfunds|sid|N (set) / addfunds|sid|N (delta)
                {
                    if (parts.Length > 2 && float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float amt))
                        Instance?.SetPlayerFunds(target, amt, verb == "addfunds");
                    else Log?.LogInfo($"[cmd] {verb}: bad amount '{(parts.Length > 2 ? parts[2] : "")}'");
                    return;
                }
                if (verb == "swapteam" || verb == "forceteamswap")                           // move the target to the other team (panel-relayed; chat path is TryHandleChatCommand)
                { Instance?.BeginSwap(target, target, verb == "forceteamswap"); return; }
                // Bot bridge: live plugin chat accepts forfeit/ff/surrender but not bare !f until 1.1.5.
                // Bot maps chat !f -> plugin_cmd forfeit|<sid> so HandleForfeit runs without a game restart
                // once this verb is loaded (pending 1.1.5). Same entry point as chat !forfeit.
                if (verb == "forfeit" || verb == "ff" || verb == "surrender" || verb == "f")
                { Instance?.HandleForfeit(target); return; }
                Log?.LogInfo($"[cmd] unknown verb '{verb}'");
            }
            catch (Exception e) { Log?.LogError("ExecCommand: " + e); }
        }

        // ---- move orchestration: spectate is immediate; a team move of a FLYING player gets a
        // 10s chat warning then ejects them out of the jet so the move actually takes effect. ----
        sealed class Pending { public Player p; public FactionHQ to; public float due; }
        static readonly List<Pending> _pendingMoves = new List<Pending>();

        internal void RequestMove(Player target, FactionHQ to, bool isSpec)
        {
            if (target == null) return;
            if (isSpec) { DoMoveNow(target, null); return; }            // spectate: now, no warning
            if (IsFlying(target))
            {
                // F4 hard rule: queue the move FIRST - the warning broadcast is cosmetic and must
                // never prevent the scheduled move (this exact ordering bug ate admin moves when
                // the name layer threw after the game update).
                _pendingMoves.RemoveAll(x => x.p == target);           // collapse repeats
                // realtimeSinceStartup: PumpPending is driven by PollCommands, which passes realtime.
                // A Time.time deadline is already in the past on the next pump, so the promised 10s
                // grace was zero - the player was moved the instant the warning went out.
                _pendingMoves.Add(new Pending { p = target, to = to, due = Time.realtimeSinceStartup + 10f });
                try
                {
                    string fn = to != null && to.faction != null ? to.faction.factionName : "the other team";
                    BroadcastAll($"<color=#FFC857>{RankedName(target)} is being moved to {fn} in 10 seconds.</color>");
                    Log?.LogInfo($"[cmd] scheduled flying move: {RawNameOf(target)} -> {fn} in 10s");
                }
                catch (Exception e) { Log?.LogError("RequestMove announce: " + e); }
            }
            else DoMoveNow(target, to);
        }

        internal void DoMoveNow(Player p, FactionHQ to)
        {
            if (p == null) return;
            AdminEject(p);   // leave the jet so the change shows (life-neutral: balance/admin move never ends a life)
            if (MovePlayer(p, to))
            {
                if (to == null) TellPlayer(p, "<color=#36FFD0>You've been moved to spectate (no team).</color>");
                else TellPlayer(p, $"<color=#36FFD0>You've been moved to {(to.faction != null ? to.faction.factionName : "the other team")}.</color>");
                // A plugin-driven move never goes through CmdSetFaction, so JoinFactionMsgPatch ate the
                // native "joined <faction>" line and nothing replaced it - an admin move, a swap or an
                // auto-balance simply had the player appear on the other side with no announcement at
                // all. The 3s per-sid dedupe inside AnnounceJoinFaction covers a real CmdSetFaction
                // racing this, so announcing here cannot double up.
                if (to != null) QueueJoinAnnounce(p);
            }
        }

        // DEAD - retained for reference only, NOT called from anywhere (verified: no callers).
        // This was the old auto-balance path: a 10s chat warning, then eject the player to spectate to
        // rejoin the smaller side themselves. BalanceOnce now switches the pick straight across with
        // BeginSwap, so nothing reaches this. Its wording outlived it in four explainers and two doc
        // pages before an audit caught them - if you delete this, grep for "10s warning" first.
        internal void RequestBalanceSpectate(Player p, string smallerName)
        {
            if (p == null) return;
            if (IsFlying(p))
            {
                // F4: queue FIRST, then the cosmetic warning (see RequestMove).
                _pendingMoves.RemoveAll(x => x.p == p);
                _pendingMoves.Add(new Pending { p = p, to = null, due = Time.realtimeSinceStartup + 10f });  // realtime: see RequestMove
                try
                {
                    BroadcastAll($"<color=#FFC857>{RankedName(p)} will be moved to spectate in 10s to balance teams - rejoin {smallerName} (fewer players), or type !spec now.</color>");
                    Log?.LogInfo($"[balance] scheduled spectate for {RawNameOf(p)} in 10s");
                }
                catch (Exception e) { Log?.LogError("RequestBalanceSpectate announce: " + e); }
            }
            else
            {
                DoMoveNow(p, null);
                TellPlayer(p, $"<color=#36FFD0>Teams were unbalanced - moved to spectate. Rejoin {smallerName}.</color>");
            }
        }

        static void PumpPending(float now)
        {
            for (int i = _pendingMoves.Count - 1; i >= 0; i--)
            {
                var pm = _pendingMoves[i];
                if (pm.p == null) { _pendingMoves.RemoveAt(i); continue; }
                if (now >= pm.due) { _pendingMoves.RemoveAt(i); Instance?.DoMoveNow(pm.p, pm.to); }
            }
        }

        // ---- join handling (the TEAM BLOCKER): returning false from the CmdSetFaction patch does NOT
        // reliably stop the join, so we ALLOW it and, on the very next tick, IMMEDIATELY move anyone who
        // joined the over-full side back to spectate - NO warning, NO grace period (a player can't have
        // spawned within one frame of joining, so this lands before they're in a jet). They get a short
        // note telling them to join the smaller side. This is the ONLY thing that fires on a join;
        // autobalance (MaybeBalance) is reserved for LEAVES. Cheap when idle. _joinProbation is now
        // vestigial (never populated) so OnPlayerSpawned is an inert safety net. ----
        static readonly List<Player> _bounceQueue = new List<Player>();
        static readonly HashSet<string> _joinProbation = new HashSet<string>(StringComparer.Ordinal);  // warned over-stackers
        internal static void QueueBounceCheck(Player p)
        {
            if (p == null) return;
            _bounceQueue.RemoveAll(x => x == p);
            _bounceQueue.Add(p);
        }

        internal static void PumpBounces()
        {
            if (_bounceQueue.Count == 0) return;
            for (int i = _bounceQueue.Count - 1; i >= 0; i--)
            {
                var p = _bounceQueue[i];
                _bounceQueue.RemoveAt(i);
                try
                {
                    if (p == null) continue;
                    string sid = Sid(p);
                    if (EnforceBalance == null || !EnforceBalance.Value) { _joinProbation.Remove(sid); continue; }
                    FactionHQ hq = null; try { hq = p.HQ; } catch { }
                    if (hq == null) { _joinProbation.Remove(sid); continue; }            // spectating / left
                    var other = OtherHQ(hq);
                    if (other == null || hq.preventJoin || other.preventJoin) { _joinProbation.Remove(sid); continue; }  // PvP only
                    int max = BalanceMaxDiff != null ? BalanceMaxDiff.Value : 2;
                    if (Side(hq).Count - Side(other).Count > max)                        // joined the over-full side -> INSTANT spectate (no warning)
                    {
                        _joinProbation.Remove(sid);                                      // not a probation case anymore - moved now
                        string smaller  = (other.faction != null) ? other.faction.factionName : "the other team";
                        string fullName = (hq.faction   != null) ? hq.faction.factionName    : "That team";
                        Instance?.DoMoveNow(p, null);                                    // straight to spectate, immediately
                        Instance?.TellPlayer(p, "<color=#FF5555>" + fullName + " has more players - moved to spectate.</color> " +
                            "<color=#FFD200>Reopen the map, click a faction, and join " + smaller + " (the smaller team).</color>");
                        Log?.LogInfo($"[balance] bounced {RawNameOf(p)} to spectate (joined the fuller side)");
                    }
                    else _joinProbation.Remove(sid);                                      // joined a fine side -> clear
                }
                catch (Exception e) { Log?.LogError("PumpBounces: " + e); }
            }
        }

        // Called when a player spawns (Player.SetAircraft). If they were warned for over-stacking and
        // the team is STILL too far ahead, eject them out of the jet and drop them to spectate.
        internal void OnPlayerSpawned(Player p)
        {
            try
            {
                if (p == null) return;
                string sid = Sid(p);
                if (!_joinProbation.Contains(sid)) return;
                FactionHQ hq = null; try { hq = p.HQ; } catch { }
                if (hq == null) { _joinProbation.Remove(sid); return; }
                var other = OtherHQ(hq);
                if (other == null || hq.preventJoin || other.preventJoin) { _joinProbation.Remove(sid); return; }
                int max = BalanceMaxDiff != null ? BalanceMaxDiff.Value : 2;
                _joinProbation.Remove(sid);
                if (Side(hq).Count - Side(other).Count > max)                            // still over-full -> eject to spectate
                {
                    string smaller = (other.faction != null) ? other.faction.factionName : "the smaller team";
                    AdminEject(p);   // life-neutral: balance probation eject must not end a life
                    if (MovePlayer(p, null))
                        TellPlayer(p, "<color=#36FFD0>That team was full - moved to spectate. Rejoin " + smaller +
                            " (open the map, click a faction).</color>");
                    Log?.LogInfo($"[balance] ejected {RawNameOf(p)} on spawn (still over-full)");
                }
            }
            catch (Exception e) { Log?.LogError("OnPlayerSpawned: " + e); }
        }

        void BroadcastAll(string msg)
        {
            // 1.1.30: `Cm ?? (...)` kept a DESTROYED (fake-null) ref forever; ResolveChatManager re-resolves.
            try { var cm = ResolveChatManager(); if (cm != null) cm.RpcServerMessage(msg, false); }
            catch (Exception e) { Log?.LogError("BroadcastAll: " + e); }
        }

        // Absolute faction-coloured "[TAG] Name joined Faction" (replaces the native
        // RpcPlayerJoinFactionMessage friend/foe line, which JoinFactionMsgPatch suppresses while
        // Chat.CustomChat is ON - re-enabled 1.1.28 from the CmdSetFaction join hook via
        // QueueJoinAnnounce below). 1.1.30: ChatFactionHex (absolute PALA #ffe294 / BDF #d4baff)
        // so join matches chat name colours. Dedup 3s/sid guards double-fires (resolution race + repeat joins).
        static readonly Dictionary<string, float> _joinFactionAnnounced = new Dictionary<string, float>();
        internal static void AnnounceJoinFaction(Player player, FactionHQ hq)
        {
            try
            {
                Trace("AnnounceJoinFaction");
                if (player == null || hq == null) return;
                string sid = Sid(player);
                float now = Time.time;
                if (!string.IsNullOrEmpty(sid) && sid != "0")
                {
                    if (_joinFactionAnnounced.TryGetValue(sid, out var last) && now - last < 3f) return;
                    _joinFactionAnnounced[sid] = now;
                    if (_joinFactionAnnounced.Count > 64)
                    {
                        foreach (var k in new List<string>(_joinFactionAnnounced.Keys))
                            if (now - _joinFactionAnnounced[k] > 30f) _joinFactionAnnounced.Remove(k);
                    }
                }
                string col = ChatFactionHex(hq);   // 1.1.30: absolute faction hex — matches chat names
                string name = SafeText(RawNameOf(player));
                if (string.IsNullOrEmpty(name)) return;
                LoadRankMap();
                if (!string.IsNullOrEmpty(sid) && RankMap.TryGetValue(sid, out var rc) && !string.IsNullOrEmpty(rc.label))
                    name = "[" + rc.label + "] " + name;
                string fac = "";
                try
                {
                    if (hq.faction != null)
                        fac = !string.IsNullOrEmpty(hq.faction.factionExtendedName)
                            ? hq.faction.factionExtendedName
                            : (hq.faction.factionName ?? "");
                }
                catch { }
                if (string.IsNullOrEmpty(fac)) return;   // never emit truncated "X joined" without a faction name
                fac = SafeText(fac);
                Instance?.BroadcastAll($"<color={col}>{name}</color> <color=#FFFFFF>joined</color> <color={col}>{fac}</color>");
            }
            catch (Exception e) { Log?.LogError("AnnounceJoinFaction: " + e); }
        }

        // 1.1.28 (F5): ranked join announces are resolution-aware. Names resolve asynchronously per
        // process now, so at the join instant the server may only have the "ID: 7656..." sentinel.
        // If resolved -> announce immediately; else queue {player, hq, deadline=now+8s} and let
        // PumpJoinAnnounces fire on resolution (RawNames fills via NameTick/OnNameResolved, usually
        // <2s) or at the deadline with the sentinel name. The 3s dedupe in AnnounceJoinFaction
        // guards double-fires. The game's own client-local "joined the game" line stays vanilla.
        sealed class PendingJoin { public Player p; public FactionHQ hq; public float deadline; }
        static readonly List<PendingJoin> _pendingJoins = new List<PendingJoin>();
        internal static void QueueJoinAnnounce(Player p)
        {
            try
            {
                if (CustomChat == null || !CustomChat.Value) return;   // feature off -> vanilla join lines (unsuppressed)
                if (p == null) return;
                FactionHQ hq = null; try { hq = p.HQ; } catch { }
                if (hq == null) return;                                // spectate/unjoined - nothing to announce
                string sid = Sid(p);
                if (string.IsNullOrEmpty(sid) || sid == "0") return;
                _pendingJoins.RemoveAll(j => j.p == p);                // collapse repeats (fast faction re-picks)
                if (IsResolved(RawNameOf(p))) { AnnounceJoinFaction(p, hq); return; }
                _pendingJoins.Add(new PendingJoin { p = p, hq = hq, deadline = Time.time + 8f });
            }
            catch (Exception e) { Log?.LogError("QueueJoinAnnounce: " + e); }
        }
        static void PumpJoinAnnounces()
        {
            if (_pendingJoins.Count == 0) return;
            try
            {
                float now = Time.time;
                for (int i = _pendingJoins.Count - 1; i >= 0; i--)
                {
                    var j = _pendingJoins[i];
                    if (j.p == null) { _pendingJoins.RemoveAt(i); continue; }   // left before announcing
                    string nm = RawNameOf(j.p);
                    if (!IsResolved(nm) && now < j.deadline) continue;          // still waiting on Steam
                    _pendingJoins.RemoveAt(i);
                    AnnounceJoinFaction(j.p, j.hq);                             // resolved (usual) or deadline sentinel
                }
            }
            catch (Exception e) { Log?.LogError("PumpJoinAnnounces: " + e); }
        }

        // ---- admin auth for the IN-GAME commands (config; the user named this SteamID) ----
        internal static ConfigEntry<string> AdminSteamIds;
        static bool IsAdmin(Player p)
        {
            try
            {
                if (AdminSteamIds == null) return false;
                string id = Sid(p);
                foreach (var a in AdminSteamIds.Value.Split(',', ' ', ';'))
                    if (a.Trim() == id && id.Length > 0) return true;
            }
            catch { }
            return false;
        }

        // resolve a player by name substring; messages the admin on no/ambiguous match.
        Player Resolve(Player admin, string namePart)
        {
            namePart = (namePart ?? "").Trim().ToLowerInvariant();
            if (namePart.Length == 0) { TellPlayer(admin, "name a player, e.g. !move bob primeva"); return null; }
            var hits = new List<Player>();
            foreach (var pl in Humans()) if (RawNameOf(pl).ToLowerInvariant().Contains(namePart)) hits.Add(pl);
            if (hits.Count == 0) { TellPlayer(admin, $"<color=#FF5555>No player matches '{namePart}'.</color>"); return null; }
            if (hits.Count > 1)
            {
                var names = new StringBuilder();
                foreach (var h in hits) { if (names.Length > 0) names.Append(", "); names.Append(RawNameOf(h)); }
                TellPlayer(admin, $"<color=#FFC857>Ambiguous '{namePart}': {names}. Be more specific.</color>");
                return null;
            }
            return hits[0];
        }

        // The two JOINABLE human factions (preventJoin == false). Co-op's AI side (preventJoin==true)
        // and any neutral/extra FactionHQ are skipped, so auto-balance picks the two real PvP teams
        // even on the BUILT-IN missions, which can expose more than two FactionHQs (the old "grab the
        // first two" version mis-detected those and silently disabled balancing). < 2 joinable sides =
        // co-op / not-a-PvP-match -> not balanceable.
        static bool TwoSides(out FactionHQ a, out FactionHQ b)
        {
            a = null; b = null;
            var joinable = new List<FactionHQ>();
            foreach (var hq in UnityEngine.Object.FindObjectsOfType<FactionHQ>())
                if (hq != null && hq.faction != null && !hq.preventJoin) joinable.Add(hq);
            if (joinable.Count < 2) return false;
            if (joinable.Count > 2)                                   // rare: pick the two most-populated teams
                joinable.Sort((x, y) => Side(y).Count.CompareTo(Side(x).Count));
            a = joinable[0]; b = joinable[1];
            return true;
        }

        // PvP mission = >= 2 JOINABLE factions (preventJoin == false). Co-op has one joinable side + a
        // preventJoin AI side. We try the MISSION DEFINITION first (timing-independent, reliable for our
        // custom JSON missions) and fall back to the live FactionHQs (covers BUILT-IN PvP maps whose
        // Mission.factions list may be constructed differently). Either signal saying >=2 -> PvP; co-op
        // yields 1 on both, so no false positive. Used by the rank floor.
        internal static bool IsPvpMission(Mission m)
        {
            try
            {
                if (m != null && m.factions != null)
                {
                    int joinable = 0;
                    foreach (var f in m.factions) if (f != null && !f.preventJoin) joinable++;
                    if (joinable >= 2) return true;
                }
            }
            catch { }
            try { return TwoSides(out _, out _); }      // live-FactionHQ backstop (built-in missions)
            catch { return false; }
        }

        static FactionHQ FindFaction(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            key = key.Trim().ToLowerInvariant();
            foreach (var hq in UnityEngine.Object.FindObjectsOfType<FactionHQ>())
            {
                if (hq == null || hq.faction == null) continue;
                string fn = (hq.faction.factionName ?? "").ToLowerInvariant();
                if (fn.Length == 0) continue;
                if (fn == key || fn.StartsWith(key) || key.StartsWith(fn)) return hq;
                if ((key == "bdf"  || key == "0") && fn.Contains("bosc")) return hq;   // Boscali = BDF
                if ((key == "pala" || key == "1") && fn.Contains("prim")) return hq;   // Primeva = PALA
            }
            return null;
        }

        // live humans on a side (skips ghosts from mid-disconnect)
        static List<Player> Side(FactionHQ hq)
        {
            var list = new List<Player>();
            if (hq == null) return list;
            try
            {
                foreach (var pr in hq.factionPlayers)
                {
                    var p = pr.Player; if (p == null) continue;
                    var s = Sid(p); if (!string.IsNullOrEmpty(s) && s != "0") list.Add(p);
                }
            }
            catch { }
            return list;
        }

        static bool IsFlying(Player p) { try { return p.Aircraft != null; } catch { return false; } }

        // The HQ SyncVar's public setter is named with angle brackets; the clean "HQ" property's
        // PRIVATE setter just forwards to it (and marks the SyncVar dirty -> syncs to clients).
        static readonly System.Reflection.MethodInfo HqSetter =
            typeof(Player).GetProperty("HQ", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetSetMethod(true);

        // Move a player to `to` (null => spectate / no team). The game's SetFaction refuses a
        // change once HQ is set, so we do the surgery ourselves: RemovePlayer (old) -> set HQ
        // SyncVar -> AddPlayer (new), mirroring ServerApplyFaction.
        internal bool MovePlayer(Player p, FactionHQ to)
        {
            if (p == null || HqSetter == null) { Log?.LogError("MovePlayer: no player / HQ setter missing"); return false; }
            FactionHQ from = null; try { from = p.HQ; } catch { }
            if (from == to) return false;
            try
            {
                if (from != null) from.RemovePlayer(p);
                HqSetter.Invoke(p, new object[] { to });
                if (to != null) { to.AddPlayer(p); try { to.RequestTrackingStates(p); } catch { } }
            }
            catch (Exception e) { Log?.LogError("MovePlayer: " + e); return false; }
            // F4: the surgery ran - report success no matter what the cosmetic log does.
            try { Log?.LogInfo($"[move] {RawNameOf(p)} {(from != null && from.faction != null ? from.faction.factionName : "none")} -> {(to != null && to.faction != null ? to.faction.factionName : "spectate")}"); } catch { }
            return true;
        }

        // ---- admin: set a player's IN-GAME rank / IN-GAME funds (the spendable Allocation) ----
        // Both call the game's own [Server] methods (we run server-side). SetRank(true) writes a
        // scoreOffset so the rank STICKS (the game only auto-bumps rank UP from score, never down, so
        // a set rank holds unless the player out-scores it). NOTE: separate from the bot's persistent
        // SERVER rank in ranks.json - this changes what the GAME shows this match. A mission restart
        // re-applies the mission's playerStartingRank floor (StartingRankFloorPatch).
        internal void SetPlayerRank(Player target, int rank)
        {
            try { if (target == null) return; target.SetRank(rank, true); }
            catch (Exception e) { Log?.LogError("SetPlayerRank: " + e); return; }
            // F4: the rank is set - the confirmation line must not turn that into a reported failure.
            try { Log?.LogInfo($"[admin] setrank {RawNameOf(target)} -> {target.PlayerRank}"); } catch { }
        }
        // funds = Player.Allocation (the player's personal spendable budget). add=false -> SetAllocation;
        // add=true -> AddAllocation (delta, may be negative).
        internal void SetPlayerFunds(Player target, float amount, bool add)
        {
            try
            {
                if (target == null) return;
                if (add) target.AddAllocation(amount); else target.SetAllocation(amount);
            }
            catch (Exception e) { Log?.LogError("SetPlayerFunds: " + e); return; }
            // F4: funds applied - cosmetic confirmation isolated.
            try { Log?.LogInfo($"[admin] {(add ? "addfunds" : "setfunds")} {RawNameOf(target)} {(add ? "+" : "=")}{amount:0} -> {target.Allocation:0}"); } catch { }
        }

        // ============ RANK CATCH-UP (rising start-rank floor over match time) ============
        // The starting-rank FLOOR rises +1 every PvpRankCatchupMinutes of match time (capped at
        // PvpRankCatchupMaxRank), so latecomers spawn at the risen floor and already-connected players below it
        // are raised too. A FLOOR only: nobody is ever lowered. 0 minutes = off.
        internal static float MatchStartTime = -1f;
        static int _catchupAnnounced = -1;
        static float _nextCatchupCheck = -1f;
        internal static void ResetCatchup() { MatchStartTime = Time.time; _catchupAnnounced = -1; }
        internal static int CatchupBonus()
        {
            try
            {
                int mins = PvpRankCatchupMinutes != null ? PvpRankCatchupMinutes.Value : 0;
                if (mins <= 0 || MatchStartTime < 0f) return 0;
                return (int)((Time.time - MatchStartTime) / (mins * 60f));
            }
            catch { return 0; }
        }
        // The game clamps in-game rank here; asking for more is silently ignored, which is what made a
        // ceiling of 6 loop forever.
        internal const int GAME_MAX_RANK = 5;

        internal static int CatchupFloor(int baseRank)
        {
            int bonus = CatchupBonus();
            if (bonus <= 0) return baseRank;
            int cap = PvpRankCatchupMaxRank != null ? PvpRankCatchupMaxRank.Value : GAME_MAX_RANK;
            // Clamp to what the game can actually reach. A cfg carrying the old default of 6 asked for a
            // rank that does not exist, so SetRank clamped to 5, the player never "arrived" at the floor,
            // and CatchupTick re-raised every player every 15s for the whole match.
            if (cap > GAME_MAX_RANK) cap = GAME_MAX_RANK;
            if (cap < baseRank) cap = baseRank;
            int f = baseRank + bonus;
            return f > cap ? cap : f;
        }
        internal static void CatchupTick()   // ~15s: raise ALREADY-CONNECTED players below the current floor
        {
            try
            {
                float now = Time.time;
                if (now < _nextCatchupCheck) return;
                _nextCatchupCheck = now + 15f;
                if (CatchupBonus() <= 0) return;
                Trace("CatchupTick");   // fires only when catch-up is ON and the floor has risen at least once
                Mission m = null; try { m = MissionManager.CurrentMission; } catch { }
                bool pvp = IsPvpMission(m);
                int baseRank = 0;
                try { if (m != null && m.missionSettings != null) baseRank = m.missionSettings.playerStartingRank; } catch { }
                if (pvp && PvpStartingRank != null && PvpStartingRank.Value > baseRank) baseRank = PvpStartingRank.Value;
                int floor = CatchupFloor(baseRank);
                if (floor <= baseRank) return;
                string fmode = FundsMode();
                int fper = RankFundsPerRank != null ? RankFundsPerRank.Value : 0;
                int raised = 0;
                foreach (var p in Humans())
                {
                    try
                    {
                        if (p == null || p.HQ == null) continue;
                        if (p.PlayerRank >= floor) continue;
                        int was = p.PlayerRank;
                        p.SetRank(floor, true);
                        raised++;
                        Log?.LogInfo($"[catchup] {RawNameOf(p)} {was} -> {floor} (rank catch-up floor)");
                        if (fper > 0 && fmode == "catchup_raised")
                            GrantRankFundsForLift(p, was, floor, "catch-up lift");
                    }
                    catch { }
                }
                if (floor != _catchupAnnounced)
                {
                    _catchupAnnounced = floor;
                    Instance?.BroadcastAll($"<color=#9AD1FF>Rank catch-up:</color> <color=#FFFFFF>the starting rank now floors at</color> <color=#FFD700>{floor}</color>");
                    Log?.LogInfo($"[catchup] floor now {floor} (base {baseRank}, raised {raised})");
                }
                if (fper > 0 && fmode == "catchup_all")
                {
                    if (_catchupPaidFloor < 0) _catchupPaidFloor = baseRank;   // baseline at the unraised base so the FIRST step pays everyone
                    if (floor > _catchupPaidFloor)
                    {
                        long amt = (long)(floor - _catchupPaidFloor) * fper;   // fper is already AddAllocation units (millions)
                        int paid = 0;
                        foreach (var p in Humans())
                        {
                            try
                            {
                                if (p == null) continue;
                                string fsid = Sid(p);
                                if (string.IsNullOrEmpty(fsid) || fsid == "0") continue;
                                p.AddAllocation(amt);
                                paid++;
                                Out("{\"t\":\"rankfunds\",\"id\":\"" + fsid + "\",\"n\":\"" + Esc(RawNameOf(p))
                                    + "\",\"rank\":" + floor + ",\"amt\":" + amt.ToString(CultureInfo.InvariantCulture) + "}");
                            }
                            catch { }
                        }
                        Log?.LogInfo($"[rankfunds] catch-up step -> +{amt:0} to all {paid} players (floor {_catchupPaidFloor} -> {floor})");
                        _catchupPaidFloor = floor;
                    }
                }
            }
            catch (Exception e) { Log?.LogError("CatchupTick: " + e); }
        }

        // ============ ACCUMULATIVE RANK FUNDS ============
        // amount = ranks_gained x RankFundsPerRank via Player.AddAllocation (Allocation millions — do NOT * 1e6).
        // NEVER pay the join/start floor itself (PvpStartingRank / mission playerStartingRank) — everyone starts
        // equal at that base. DO pay mid-match catch-up ranks: CatchupTick raises already-connected players, and
        // StartingRankFloorPatch late-join when CatchupFloor > base (incl. reconnect already at floor — 1.1.1
        // only paid inside the SetRank branch). 1.0.27 stopped ALL join pays and left latecomers raised-but-unpaid
        // because CatchupTick then skips PlayerRank>=floor. catchup_raised / catchup_all use those paths;
        // any_rankup pays later natural rank-ups in RankFundsTick (first sighting = unpaid baseline).
        // MONOTONIC per match via _rankFunded (survives reconnect); RESET on mission change. 0 = off.
        static readonly Dictionary<string, int> _rankFunded = new Dictionary<string, int>(StringComparer.Ordinal);
        static int _catchupPaidFloor = -999;   // catchup_all: last floor everyone was paid up to (reset per match)
        internal static void ResetRankFunds() { _rankFunded.Clear(); _catchupPaidFloor = -999; }
        // WHEN funds pay out: normalize the config value; anything unknown -> catchup_raised (the default).
        internal static string FundsMode()
        {
            string m = (RankFundsMode != null ? RankFundsMode.Value : "catchup_raised");
            m = (m ?? "").Trim().ToLowerInvariant();
            return (m == "any_rankup" || m == "catchup_all") ? m : "catchup_raised";
        }
        // Shared grant for a personal mid-match catch-up lift (CatchupTick only — NOT join/start floor).
        // Skips if already funded to newRank (reconnect / double-call safe). Marks _rankFunded so any_rankup
        // cannot pay the same ranks again.
        internal static void GrantRankFundsForLift(Player p, int was, int newRank, string reason)
        {
            try
            {
                int per = RankFundsPerRank != null ? RankFundsPerRank.Value : 0;
                if (per <= 0 || p == null || newRank <= was) return;
                string fsid = Sid(p);
                if (string.IsNullOrEmpty(fsid) || fsid == "0") return;
                if (_rankFunded.TryGetValue(fsid, out var funded) && funded >= newRank) return;
                int from = was;
                if (_rankFunded.TryGetValue(fsid, out funded) && funded > from) from = funded;
                if (newRank <= from) return;
                long amt = (long)(newRank - from) * per;   // per is already AddAllocation units (millions)
                _rankFunded[fsid] = newRank;
                p.AddAllocation(amt);
                Log?.LogInfo($"[rankfunds] +{amt:0} to {RawNameOf(p)} ({reason} {from} -> {newRank})");
                Out("{\"t\":\"rankfunds\",\"id\":\"" + fsid + "\",\"n\":\"" + Esc(RawNameOf(p))
                    + "\",\"rank\":" + newRank + ",\"amt\":" + amt.ToString(CultureInfo.InvariantCulture) + "}");
            }
            catch (Exception e) { Log?.LogError("GrantRankFundsForLift: " + e); }
        }
        internal static void RankFundsTick()
        {
            try
            {
                int per = RankFundsPerRank != null ? RankFundsPerRank.Value : 0;
                if (per <= 0) return;   // feature off
                if (FundsMode() != "any_rankup") return;   // catchup_raised / catchup_all pay from floor lifts, not this scan
                foreach (var p in Humans())
                {
                    try
                    {
                        if (p == null) continue;
                        string sid = Sid(p);
                        if (string.IsNullOrEmpty(sid) || sid == "0") continue;
                        int rank = p.PlayerRank;
                        if (!_rankFunded.TryGetValue(sid, out var funded))
                        {
                            // First sighting (incl. after join/start floor apply) = unpaid baseline only.
                            // CatchupTick mid-match lifts mark _rankFunded when they paid under catchup_raised.
                            _rankFunded[sid] = rank;
                            continue;
                        }
                        if (rank <= funded) continue;                  // no increase (monotonic)
                        long amount = (long)(rank - funded) * per;      // per is already AddAllocation units (millions)
                        _rankFunded[sid] = rank;                        // mark BEFORE granting (never double-grant)
                        p.AddAllocation(amount);                        // same primitive as admin !addfunds
                        Log?.LogInfo($"[rankfunds] +{amount:0} funds to {RawNameOf(p)} for reaching rank {rank} (was funded to {funded})");
                        // let the bot surface an announce
                        Out("{\"t\":\"rankfunds\",\"id\":\"" + sid + "\",\"n\":\"" + Esc(RawNameOf(p))
                            + "\",\"rank\":" + rank + ",\"amt\":" + amount.ToString(CultureInfo.InvariantCulture) + "}");
                    }
                    catch { }
                }
            }
            catch (Exception e) { Log?.LogError("RankFundsTick: " + e); }
        }

        // ---- mission-timeout resolution: end the match with a RESULT a bit BEFORE the game's MaxTime, so the
        // bot's map vote can run before the mission auto-rotates. PvE (1 human side vs AI) -> declare the humans
        // defeated (gated by TimeoutForceDefeat); PvP (2 joinable sides) -> the higher TOTAL in-game score wins,
        // exact tie = draw (gated by PvpTimeoutResult). Lead = TimeoutLeadSeconds. Double-end guarded; 1 Hz from
        // HQTickPatch. (Still named PvETimeoutTick for the existing call site.) ----
        static float _lastTimeoutCheck = -999f;
        static float _timeoutLevelMark = -1f;   // timeSinceLevelLoad at the last tick: detects a level load
        static bool _timeoutResolved = false;   // this match's timeout result has already been announced
        internal static void PvETimeoutTick()
        {
            try
            {
                float now = Time.time;
                if (now - _lastTimeoutCheck < 1f) return;            // 1 Hz, cheap
                _lastTimeoutCheck = now;

                // A LEVEL LOAD IS A NEW MATCH - re-arm the once-per-match latch below. Same idiom as
                // LimboWatchTick: timeSinceLevelLoad running BACKWARDS is the level-load signal.
                float tsl = Time.timeSinceLevelLoad;
                if (_timeoutLevelMark < 0f || tsl < _timeoutLevelMark) _timeoutResolved = false;
                _timeoutLevelMark = tsl;

                // THE SPAM GUARD. The gameResolution check below is NOT sufficient on its own: ForceDraw
                // only ends the game if the EndType enum actually has a Draw/Tie/Stalemate member, and when
                // it does not it just emits a frame and returns - leaving gameResolution == Ongoing. Every
                // later tick then re-satisfied the same timeout condition and re-broadcast the result, which
                // is what put "it's a DRAW" in chat over and over (owner report 2026-08-01). Announcing is
                // now latched to once per match independently of whether ending the game succeeded.
                if (_timeoutResolved) return;
                if (GameManager.gameResolution != GameResolution.Ongoing) return;  // already ended -> guard
                bool pveOn = TimeoutForceDefeat != null && TimeoutForceDefeat.Value;
                bool pvpOn = PvpTimeoutResult != null && PvpTimeoutResult.Value;
                if (!pveOn && !pvpOn) return;

                float maxTime = CurrentMissionMaxTime();
                if (maxTime <= 0f) return;
                int lead = TimeoutLeadSeconds != null ? Mathf.Max(0, TimeoutLeadSeconds.Value) : 120;
                if (Time.timeSinceLevelLoad <= maxTime - lead) return;   // not within the lead window yet

                // Enumerate ALL HQs (the AI side has preventJoin==true, which TwoSides() hides).
                FactionHQ aiHQ = null;
                int human = 0, ai = 0;
                foreach (var hq in UnityEngine.Object.FindObjectsOfType<FactionHQ>())
                {
                    if (hq == null || hq.faction == null) continue;
                    if (hq.preventJoin) { ai++; aiHQ = hq; }         // AI-only side
                    else human++;                                    // human-joinable side
                }

                if (human == 1 && ai >= 1 && aiHQ != null)
                {
                    if (!pveOn) return;                              // PvE, but the PvE defeat is off
                    _timeoutResolved = true;                        // latch BEFORE acting - never announce twice
                    Log?.LogInfo($"[timeout] PvE timer ({Time.timeSinceLevelLoad:F0}s, {lead}s before {maxTime:F0}s) -> declaring AI victory (humans defeated).");
                    ForceVictory(aiHQ);                             // humans see Mission Failed
                    return;
                }

                if (pvpOn && TwoSides(out var A, out var B))
                {
                    double sa = TeamScore(A), sb = TeamScore(B);
                    string na = A.faction != null ? A.faction.factionName : "Team A";
                    string nb = B.faction != null ? B.faction.factionName : "Team B";
                    _timeoutResolved = true;                        // latch BEFORE acting - never announce twice
                    if (Math.Abs(sa - sb) < 0.0001)
                    {
                        Log?.LogInfo($"[timeout] PvP timer ({Time.timeSinceLevelLoad:F0}s) -> DRAW ({na} {sa:F0} = {nb} {sb:F0}).");
                        Instance?.BroadcastAll($"<color=#FFD200>** Time's up - it's a DRAW! {na} {sa:F0} : {sb:F0} {nb} **</color>");
                        ForceDraw(A, B);
                    }
                    else
                    {
                        var winHQ = sa > sb ? A : B; string wn = sa > sb ? na : nb;
                        Log?.LogInfo($"[timeout] PvP timer ({Time.timeSinceLevelLoad:F0}s) -> {wn} wins on score ({na} {sa:F0} vs {nb} {sb:F0}).");
                        Instance?.BroadcastAll($"<color=#7CFFB0>** Time's up - {wn} wins on score! {na} {sa:F0} : {sb:F0} {nb} **</color>");
                        ForceVictory(winHQ);
                    }
                }
            }
            catch (Exception e) { Log?.LogError("PvETimeoutTick: " + e); }
        }

        // -------- rejoin-limbo watchdog (1.1.30 diag; 1.2.1 auto-release) --------
        // 07-26 finding: after an ungraceful mid-match disconnect the game auto-restored the
        // rejoining player's faction from save data, but his client never re-entered the mission -
        // the server processed ZERO spawn requests for 185s and then kicked him ("Disconnected by
        // server"), with nothing in any log to say why. This watchdog makes the next occurrence
        // self-evident: a Human who has an HQ but NO aircraft for LIMBO_AFTER continuous seconds
        // gets a loud [limbo] line (re-logged every LIMBO_RELOG while it persists) + a
        // {"t":"limbo"} frame for the bot/activity feed.
        //
        // 1.2.1 AUTO-RELEASE (2026-07-28 root cause): the wedge is the client authenticating,
        // receiving LoadMapMessage, and its MAP LOAD FAILING (stale build/content after a game
        // update) - it never sends SceneReadyMessage, no spawn request ever arrives, and the
        // client eventually dies into the unrecoverable "Local client stopped" state that only a
        // full game-client restart clears. After Limbo.ReleaseSeconds in that state the server
        // closes the session with a PLAIN transport disconnect (INetworkPlayer.Disconnect(), the
        // exact call the game's own ErrorRateLimitReached uses) -> the client gets a clean
        // "Disconnected by server" and can reconnect immediately.
        //   * KICK-LIST SAFE (decompile-verified): only authenticator OnKick/OnMissionKick/
        //     BanPlayer touch the session kick lists; plain Disconnect() never does, so this can
        //     never convert a wedge into a rejoin lockout. It also never routes through the
        //     Disconnect(reason) overload guard E patches.
        //   * IDLER-SAFE: the release requires server-side Owner.SceneIsReady == FALSE. The game
        //     sets it false when it sends LoadMapMessage and true only when the client's
        //     SceneReadyMessage lands (decompile: NetworkManagerNuclearOption.HandleSceneReadyMessage)
        //     - so a player merely idling in the spawn menu (scene loaded) is NEVER released,
        //     and an unreadable/unknown state fails open to log-only.
        //   * LOOP-SAFE: per-sid cooldown LIMBO_RELEASE_COOLDOWN (10 min, survives the
        //     release-reconnect cycle); a second wedge inside it is logged, never disconnected.
        //     Releases are also suppressed for a grace window after a map change
        //     (Time.timeSinceLevelLoad) so a slow-loading client mid-rotation is never hit.
        static readonly Dictionary<string, float> _limboSince = new Dictionary<string, float>(StringComparer.Ordinal);
        static readonly Dictionary<string, float> _limboNextLog = new Dictionary<string, float>(StringComparer.Ordinal);
        static readonly Dictionary<string, float> _limboReleasedAt = new Dictionary<string, float>(StringComparer.Ordinal);
        // sid -> Time.time of last auto-release; NOT pruned on leave (the cooldown must outlive the
        // release-reconnect cycle). Stale entries are swept below once they are older than the cooldown.
        static float _limboStormLog;   // rate-limit the storm warning
        // sid -> Time.unscaledTime when we last SAW this player. Time.time stops while the server is
        // empty (NetworkPause), so the leaver prune below cannot run and a departing player's wedge
        // clock would otherwise survive straight into his next session - and be already expired the
        // moment he reconnects, while his client is legitimately loading. The unscaled clock does not
        // stop, so a gap in sightings is detectable and starts a fresh episode.
        static readonly Dictionary<string, float> _limboLastSeen = new Dictionary<string, float>(StringComparer.Ordinal);
        const float LIMBO_SESSION_GAP = 10f;   // unseen this long = a new session, not the same one
        static float _nextLimboScan;
        static float _limboLevelMark = -1f;          // timeSinceLevelLoad at the last scan: detects a level load
        const float LIMBO_AFTER = 60f, LIMBO_RELOG = 120f;
        // Several players unready AT ONCE is a SERVER event (map load, host hitch), not N separate
        // wedges - releasing on that would disconnect a lobby full of healthy people.
        const int LIMBO_STORM_PLAYERS = 3;
        const int LIMBO_MAX_RELEASES_PER_SCAN = 1;   // a misfire can never cascade
        const float LIMBO_RELEASE_COOLDOWN = 600f;   // release each sid at most once per 10 min
        internal static void LimboWatchTick()
        {
            float now = Time.time;
            if (now < _nextLimboScan) return;
            _nextLimboScan = now + 5f;
            try
            {
                // A LEVEL LOAD RESTARTS EVERY CLOCK. Without this the wedge timer runs straight
                // through a map rotation, so the moment the map-change grace lifts, every client
                // still loading looks like it has been wedged for minutes.
                try
                {
                    float tsl = Time.timeSinceLevelLoad;
                    if (_limboLevelMark < 0f || tsl < _limboLevelMark)
                    {
                        if (_limboSince.Count > 0)
                            Log?.LogInfo($"[limbo] level load - wedge clocks reset for {_limboSince.Count} player(s)");
                        _limboSince.Clear(); _limboNextLog.Clear();
                    }
                    _limboLevelMark = tsl;
                }
                catch { }
                bool arOn = LimboAutoRelease != null && LimboAutoRelease.Value;   // null config -> OFF; never disconnect on a default we could not read
                float relAfter = Mathf.Max(LIMBO_AFTER, LimboReleaseSeconds != null ? LimboReleaseSeconds.Value : 180f);
                // map-change grace: right after a level load EVERY client is legitimately reloading
                // (SceneIsReady false across the board); no release until the dust settles.
                // Defaults FALSE: if we cannot read how long ago the level loaded we must assume we are
                // still inside the map-change grace, because the alternative is disconnecting people.
                bool mapGraceOver = false; try { mapGraceOver = Time.timeSinceLevelLoad >= relAfter + 60f; } catch { }
                var present = new HashSet<string>(StringComparer.Ordinal);
                // Count who is unready BEFORE deciding anything: a storm means "the server did
                // this", and the one thing we must never do is disconnect the room.
                int unready = 0;
                try
                {
                    foreach (var q in Humans())
                    {
                        try
                        {
                            if (q == null || q.Aircraft != null || q.HQ == null) continue;
                            var qo = q.Owner;
                            if (qo != null && !qo.IsHost && !qo.SceneIsReady) unready++;
                        }
                        catch { }
                    }
                }
                catch { }
                bool storm = unready >= LIMBO_STORM_PLAYERS;
                if (storm && now - _limboStormLog > 60f)
                {
                    _limboStormLog = now;
                    Log?.LogWarning($"[limbo] {unready} players unready at once - treating this as a SERVER event "
                                  + "(map load / host hitch), NOT individual wedges: auto-release suppressed this scan");
                }
                if (_limboLastSeen.Count > 256)
                {
                    try
                    {
                        float rt = Time.unscaledTime; var gone = new List<string>();
                        foreach (var kv in _limboLastSeen) if (rt - kv.Value > 3600f) gone.Add(kv.Key);
                        foreach (var k in gone) _limboLastSeen.Remove(k);
                    }
                    catch { }
                }
                if (_limboReleasedAt.Count > 64)   // bounded across a long uptime; expired entries mean nothing
                {
                    try
                    {
                        var dead = new List<string>();
                        foreach (var kv in _limboReleasedAt) if (now - kv.Value > LIMBO_RELEASE_COOLDOWN) dead.Add(kv.Key);
                        foreach (var k in dead) _limboReleasedAt.Remove(k);
                    }
                    catch { }
                }
                int releasedThisScan = 0;
                foreach (var p in Humans())
                {
                    string sid = Sid(p);
                    if (string.IsNullOrEmpty(sid) || sid == "0") continue;
                    present.Add(sid);
                    // NEW SESSION? Restart the episode. See _limboLastSeen above.
                    try
                    {
                        float rt = Time.unscaledTime;
                        if (_limboLastSeen.TryGetValue(sid, out var seen) && rt - seen > LIMBO_SESSION_GAP)
                        {
                            _limboSince.Remove(sid); _limboNextLog.Remove(sid);
                            Log?.LogInfo($"[limbo] {RawNameOf(p)} was away {rt - seen:0}s - wedge clock restarted for the new session");
                        }
                        _limboLastSeen[sid] = rt;
                    }
                    catch { }
                    FactionHQ hq = null; try { hq = p.HQ; } catch { }
                    bool hasAc = false; try { hasAc = p.Aircraft != null; } catch { }
                    if (hq == null || hasAc) { _limboSince.Remove(sid); _limboNextLog.Remove(sid); continue; }
                    if (!_limboSince.TryGetValue(sid, out var since)) { _limboSince[sid] = since = now; }
                    float t = now - since;
                    if (t < LIMBO_AFTER) continue;

                    // scene-ready discriminator: 0 = wedged (map load never completed), 1 = scene
                    // loaded (spawn-menu idler), -1 = unreadable -> fail open, never release.
                    int ready = -1;
                    try { var o = p.Owner; if (o != null && !o.IsHost) ready = o.SceneIsReady ? 1 : 0; } catch { }

                    bool relDue = arOn && ready == 0 && t >= relAfter && mapGraceOver
                                  && !storm && releasedThisScan < LIMBO_MAX_RELEASES_PER_SCAN;
                    bool onCooldown = _limboReleasedAt.TryGetValue(sid, out var relAt) && (now - relAt) < LIMBO_RELEASE_COOLDOWN;
                    if (relDue && !onCooldown)
                    {
                        bool ok = false;
                        try
                        {
                            var o = p.Owner;
                            if (o != null)
                            {
                                _limboReleasedAt[sid] = now;          // stamp BEFORE the disconnect so a re-scan can never double-release
                                releasedThisScan++;                   // one per scan: a misfire can never cascade through the lobby
                                o.Disconnect();                       // plain transport close; NEVER KickPlayer / the kick list
                                ok = true;
                            }
                        }
                        catch (Exception ex) { Log?.LogError("[limbo] release failed sid=" + sid + ": " + ex); }
                        if (ok)
                        {
                            Log?.LogInfo($"[limbo] AUTO-RELEASE sid={sid} ({RawNameOf(p)}) wedged {t:0}s: authenticated + faction restored but the client NEVER completed the map load (no SceneReadyMessage, no spawn request received) - session closed with a plain transport disconnect (kick list untouched) so the client gets a clean 'Disconnected by server' and can rejoin immediately");
                            EmitLimbo(sid, p, t, true, ready);
                            _limboSince.Remove(sid); _limboNextLog.Remove(sid);
                            continue;
                        }
                    }

                    if (_limboNextLog.TryGetValue(sid, out var nl) && now < nl) continue;
                    _limboNextLog[sid] = now + LIMBO_RELOG;
                    string state = ready == 0 ? "client NEVER completed the map load (no SceneReadyMessage) - the 'Local client stopped' wedge"
                                 : ready == 1 ? "scene loaded; sitting in the spawn menu (idle - NOT releasable)"
                                 : "scene state unreadable (fail-open: log only)";
                    string why = relDue && onCooldown ? " [release DUE but on 10-min cooldown - log only]"
                               : arOn && ready == 0 && t >= relAfter && !mapGraceOver ? " [release held: map-change grace window]"
                               : !arOn && ready == 0 && t >= relAfter ? " [release due but Limbo.AutoRelease=false]" : "";
                    Log?.LogInfo($"[limbo] rejoin-limbo sid={sid} ({RawNameOf(p)}) {t:0}s on a faction with NO aircraft - no spawn request reached the server; {state}{why}");
                    EmitLimbo(sid, p, t, false, ready);
                }
                if (_limboSince.Count > 0)
                    foreach (var k in new List<string>(_limboSince.Keys))
                        if (!present.Contains(k)) { _limboSince.Remove(k); _limboNextLog.Remove(k); }   // leaver -> reset episode
                if (_limboReleasedAt.Count > 64)   // bound the cooldown registry; only expired entries are dropped
                    foreach (var k in new List<string>(_limboReleasedAt.Keys))
                        if (now - _limboReleasedAt[k] >= LIMBO_RELEASE_COOLDOWN) _limboReleasedAt.Remove(k);
            }
            catch (Exception e) { Log?.LogError("LimboWatchTick: " + e); }
        }

        // {"t":"limbo"} evidence frame -> bot activity feed + monitoring (emitted on every detection
        // re-log AND on every release, so the wedge is never invisible again). ready: 1 = scene
        // loaded (idler), 0 = map load never completed (the wedge), -1 = unknown.
        static void EmitLimbo(string sid, Player p, float secs, bool released, int ready)
        {
            try
            {
                var sb = new StringBuilder(140);
                sb.Append("{\"t\":\"limbo\",\"id\":\"").Append(sid)
                  .Append("\",\"n\":\"").Append(Esc(RawNameOf(p)))
                  .Append("\",\"secs\":").Append((int)secs)
                  .Append(",\"released\":").Append(released ? "true" : "false")
                  .Append(",\"ready\":").Append(ready)
                  .Append(",\"ts\":0}");
                Out(sb.ToString());
            }
            catch (Exception e) { Log?.LogError("EmitLimbo: " + e); }
        }

        // -------- guard F plumbing (1.2.1): reflection into the game's TimeoutManager --------
        // TimeoutManager._players (private Dictionary<CSteamID, PlayerTimeout>) holds the rejoin
        // lockouts; PlayerTimeout's fields are public. All access is cached-FieldInfo, null-guarded
        // and fail-open: any miss degrades guard F to log-only, never breaks the game's own flow.
        static FieldInfo _tmPlayersFI;                      // TimeoutManager._players
        static FieldInfo _ptExpiryFI, _ptVioFI, _ptEkcFI;   // PlayerTimeout.TimeoutExpiry / ViolationLevel / ErrorKickCount
        static bool _tmReflectTried;
        static object TimeoutEntryFor(object tm, object steamIdBoxed)
        {
            if (tm == null || steamIdBoxed == null) return null;
            if (_tmPlayersFI == null)
            {
                if (_tmReflectTried) return null;
                _tmReflectTried = true;
                _tmPlayersFI = AccessTools.Field(tm.GetType(), "_players");
                if (_tmPlayersFI == null) { Log?.LogWarning("[errkick] TimeoutManager._players not found; guard F degrades to log-only (no lift, no remaining-seconds)"); return null; }
            }
            var dict = _tmPlayersFI.GetValue(tm) as System.Collections.IDictionary;
            if (dict == null || !dict.Contains(steamIdBoxed)) return null;
            var entry = dict[steamIdBoxed];
            if (entry != null && _ptExpiryFI == null)
            {
                var et = entry.GetType();
                _ptExpiryFI = AccessTools.Field(et, "TimeoutExpiry");
                _ptVioFI    = AccessTools.Field(et, "ViolationLevel");
                _ptEkcFI    = AccessTools.Field(et, "ErrorKickCount");
            }
            return entry;
        }

        // Called (postfix) every time the game's error-kick fires. banPath = OnKickFromError returned
        // true (instant-ban flag or repeated-error-kick ban) - deliberately NOT lifted/vetoed, only
        // reported loudly. Otherwise: record the evidence, and when ErrorKick.LiftTimeout is ON clear
        // the just-created rejoin lockout + roll back the ban-ladder counters so the disconnect stays
        // a one-off clean reset instead of a 300s "Local client stopped" lockout.
        internal static void NoteErrorKick(object tm, object steamIdBoxed, object errorFlag, bool banPath)
        {
            try
            {
                string sid = steamIdBoxed != null ? steamIdBoxed.ToString() : "";
                string flags = errorFlag != null ? errorFlag.ToString() : "?";
                var p = FindPlayerBySid(sid);
                string name = p != null ? RawNameOf(p) : (RawNames.TryGetValue(sid, out var rn) ? rn : "");
                bool lift = (ErrorKickLiftTimeout == null || ErrorKickLiftTimeout.Value) && !banPath;
                double secs = 0; bool lifted = false; int vio = -1, ekc = -1;
                var entry = TimeoutEntryFor(tm, steamIdBoxed);
                if (entry != null)
                {
                    try { if (_ptVioFI != null) vio = (int)_ptVioFI.GetValue(entry); } catch { }
                    try { if (_ptEkcFI != null) ekc = (int)_ptEkcFI.GetValue(entry); } catch { }
                    try { if (_ptExpiryFI != null) secs = Math.Max(0.0, (double)_ptExpiryFI.GetValue(entry) - Time.unscaledTimeAsDouble); } catch { }
                    if (lift)
                    {
                        try
                        {
                            if (_ptExpiryFI != null) { _ptExpiryFI.SetValue(entry, 0.0); lifted = true; }   // the lockout itself
                            if (lifted && _ptEkcFI != null && ekc > 0) _ptEkcFI.SetValue(entry, ekc - 1);   // undo the +1 step toward 'Error Auto Ban'
                            if (lifted && _ptVioFI != null && vio >= 2) _ptVioFI.SetValue(entry, vio - 2);  // undo OnKickFromError's +2
                        }
                        catch (Exception ex) { Log?.LogError("[errkick] lift failed sid=" + sid + ": " + ex); }
                    }
                }
                if (banPath)
                    Log?.LogWarning($"[errkick] sid={sid} ({name}) error-kick ESCALATED TO THE BAN PATH (flags={flags}) - instant-ban flag or repeated error kicks; guard F does NOT veto bans. If this is a legitimate player, check/undo ban_list NOW");
                else if (lifted)
                    Log?.LogInfo($"[errkick] sid={sid} ({name}) error-kicked by the game (flags={flags}); rejoin lockout WAS {secs:0}s -> LIFTED (expiry cleared, ErrorKickCount {ekc}->{Math.Max(0, ekc - 1)}, ViolationLevel {vio}->{Math.Max(0, vio - 2)}). The disconnect itself proceeds (clean client reset); the player can rejoin immediately");
                else
                    Log?.LogWarning($"[errkick] sid={sid} ({name}) error-kicked by the game (flags={flags}); rejoin lockout {secs:0}s ACTIVE ({(lift ? "timeout entry not reachable" : "ErrorKick.LiftTimeout=false")}) - rejoins are silently refused until it expires; the client shows 'Local client stopped'");
                var sb = new StringBuilder(170);
                sb.Append("{\"t\":\"errkick\",\"id\":\"").Append(sid)
                  .Append("\",\"n\":\"").Append(Esc(name))
                  .Append("\",\"flags\":\"").Append(Esc(flags))
                  .Append("\",\"secs\":").Append((int)secs)
                  .Append(",\"lifted\":").Append(lifted ? "true" : "false")
                  .Append(",\"ban\":").Append(banPath ? "true" : "false")
                  .Append(",\"ts\":0}");
                Out(sb.ToString());
            }
            catch (Exception e) { Log?.LogError("NoteErrorKick: " + e); }
        }

        // Called (postfix, read-only) when TimeoutManager.HasTimeout refuses a join. Rate-limited to
        // one report per sid per 30s (the game refuses EVERY connect attempt while the lockout runs,
        // and each refusal re-adds the spam penalty - the loop that kept the owner out on 07-28).
        static readonly Dictionary<string, double> _joinBlockNext = new Dictionary<string, double>(StringComparer.Ordinal);
        internal static void NoteJoinBlocked(object tm, object steamIdBoxed)
        {
            try
            {
                string sid = steamIdBoxed != null ? steamIdBoxed.ToString() : "";
                if (string.IsNullOrEmpty(sid) || sid == "0") return;
                double now = 0; try { now = Time.unscaledTimeAsDouble; } catch { }
                if (_joinBlockNext.TryGetValue(sid, out var nx) && now < nx) return;
                _joinBlockNext[sid] = now + 30.0;
                if (_joinBlockNext.Count > 64)   // bound the registry; expired entries only
                    foreach (var k in new List<string>(_joinBlockNext.Keys))
                        if (now >= _joinBlockNext[k]) _joinBlockNext.Remove(k);
                double secs = 0;
                var entry = TimeoutEntryFor(tm, steamIdBoxed);
                if (entry != null) { try { if (_ptExpiryFI != null) secs = Math.Max(0.0, (double)_ptExpiryFI.GetValue(entry) - now); } catch { } }
                string name = RawNames.TryGetValue(sid, out var rn) ? rn : "";
                Log?.LogWarning($"[errkick] JOIN REFUSED sid={sid}{(string.IsNullOrEmpty(name) ? "" : " (" + name + ")")} - active TimeoutManager lockout, ~{secs:0}s remaining (every attempt re-adds the spam penalty; the client shows 'Local client stopped')");
                var sb = new StringBuilder(120);
                sb.Append("{\"t\":\"joinblock\",\"id\":\"").Append(sid)
                  .Append("\",\"n\":\"").Append(Esc(name))
                  .Append("\",\"secs\":").Append((int)secs)
                  .Append(",\"ts\":0}");
                Out(sb.ToString());
            }
            catch (Exception e) { Log?.LogError("NoteJoinBlocked: " + e); }
        }

        // ---- Annihilate auto-win: when one side has zero aircraft AND cannot spawn (no hangars),
        // sustained for GraceSeconds, ForceVictory for the other side. Classic AND (1.1.11 undoes
        // 1.1.10 OR). RequireNoSpawn=true (default): annihilated = !canSpawn && planes==0;
        // false = planes==0 only. Spawn probe = FactionHQ.GetAirbases + Airbase.AnyHangarsAvailable. ----
        static float _lastAnnihilateCheck = -999f;
        static readonly Dictionary<int, float> _annihilateSince = new Dictionary<int, float>(); // HQ instanceId -> first seen dead
        static int _annihilateMissionGen = -1;
        static float _nextAnnihilateDiag = -999f;   // 1.1.30: 30s rate limit for the [annihilate] status diagnostic

        // True iff this faction still owns at least one FIXED-WING-capable hangar.
        // Helipads (heli-only availableAircraft, or name contains "helipad") do NOT count —
        // AnyHangarsAvailable treated them as spawn and blocked Annihilate (audit P2).
        static bool DefIsHeliCatalog(AircraftDefinition d)
        {
            if (d == null) return true;
            try
            {
                string jk = ""; try { jk = d.jsonKey ?? ""; } catch { }
                if (!string.IsNullOrEmpty(jk) && _heliKeys.Contains(jk)) return true;
                string un = ""; try { un = d.unitName ?? ""; } catch { }
                if (un.IndexOf("helo", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (un.IndexOf("heli", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (jk.IndexOf("Helo", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (jk.IndexOf("Heli", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch { }
            return false;
        }
        // Classify one pad: 0 = gone/unusable, 1 = fixed-wing hangar, 2 = helipad (heli-only).
        // This is the single place that decides what a pad IS. HangarCountsAsFixedWingSpawn (the
        // Annihilate probe) and CountPads (the WebCC team-data panel) both read it, so the auto-win
        // condition and the numbers shown on the panel can never disagree about what is still standing.
        internal const int PAD_NONE = 0, PAD_HANGAR = 1, PAD_HELIPAD = 2;
        internal static int ClassifyPad(Hangar h)
        {
            if (h == null) return PAD_NONE;
            try { if (h.Disabled) return PAD_NONE; } catch { return PAD_NONE; }
            try
            {
                bool avail = false;
                try { avail = h.Available; } catch { }
                if (!avail) { try { avail = h.IsFunctional(); } catch { } }
                if (!avail) return PAD_NONE;
            }
            catch { return PAD_NONE; }
            AircraftDefinition[] defs = null;
            try { defs = h.GetAvailableAircraft(); } catch { }
            if (defs != null && defs.Length > 0)
            {
                foreach (var d in defs)
                    if (!DefIsHeliCatalog(d)) return PAD_HANGAR;   // can spawn at least one fixed-wing
                return PAD_HELIPAD;                                // heli-only = helipad
            }
            try
            {
                string n = h.name ?? "";
                if (n.IndexOf("helipad", StringComparison.OrdinalIgnoreCase) >= 0) return PAD_HELIPAD;
            }
            catch { }
            return PAD_HANGAR; // unknown catalog, non-helipad name — fail-open (carriers / empty lists)
        }
        static bool HangarCountsAsFixedWingSpawn(Hangar h)
        {
            return ClassifyPad(h) == PAD_HANGAR;
        }

        // Standing pads per faction, for the WebCC team-data panel. Counts what it can ENUMERATE;
        // an airbase that exposes no hangar list (carriers, and the init race) but still reports a
        // usable pad contributes 1, matching what FactionCanSpawn already treats as spawnable there.
        internal static void CountPads(FactionHQ hq, out int hangars, out int helipads)
        {
            hangars = 0; helipads = 0;
            if (hq == null) return;
            try
            {
                foreach (var ab in hq.GetAirbases())
                {
                    if (ab == null) continue;
                    try { if (ab.disabled) continue; } catch { }
                    try
                    {
                        var hs = ab.hangars;
                        if (hs != null && hs.Count > 0)
                        {
                            foreach (var h in hs)
                            {
                                int k = ClassifyPad(h);
                                if (k == PAD_HANGAR) hangars++;
                                else if (k == PAD_HELIPAD) helipads++;
                            }
                            continue;
                        }
                    }
                    catch { }
                    try { if (ab.AnyHangarsAvailable()) hangars++; } catch { }
                }
            }
            catch { }
        }
        static bool FactionCanSpawn(FactionHQ hq)
        {
            if (hq == null) return false;
            try
            {
                foreach (var ab in hq.GetAirbases())
                {
                    if (ab == null) continue;
                    try { if (ab.disabled) continue; } catch { }
                    try
                    {
                        var hs = ab.hangars;
                        if (hs != null && hs.Count > 0)
                        {
                            foreach (var h in hs)
                                if (HangarCountsAsFixedWingSpawn(h)) return true;
                            continue; // enumerated hangars; none fixed-wing — do not use AnyHangarsAvailable
                        }
                        // null/empty hangars list: fall through to legacy probe (carriers / init race)
                    }
                    catch { }
                    try { if (ab.AnyHangarsAvailable()) return true; } catch { } // API miss / empty-list fallback
                }
            }
            catch { }
            return false;
        }

        internal static void AnnihilateTick()
        {
            try
            {
                float now = Time.time;
                if (now - _lastAnnihilateCheck < 1f) return;          // 1 Hz
                _lastAnnihilateCheck = now;
                Trace("AnnihilateTick");

                if (AnnihilateEnabled == null || !AnnihilateEnabled.Value) { _annihilateSince.Clear(); return; }
                if (GameManager.gameResolution != GameResolution.Ongoing) { _annihilateSince.Clear(); return; }

                // Mission restart: level-load clock jumped backwards
                if (_annihilateMissionGen >= 0 && Time.timeSinceLevelLoad < 2f && _annihilateMissionGen > 10)
                    _annihilateSince.Clear();
                _annihilateMissionGen = (int)Time.timeSinceLevelLoad;

                int minMatch = AnnihilateMinMatchSeconds != null ? Mathf.Max(0, AnnihilateMinMatchSeconds.Value) : 120;
                if (Time.timeSinceLevelLoad < minMatch) { _annihilateSince.Clear(); return; }

                int minPl = AnnihilateMinPlayers != null ? Mathf.Max(0, AnnihilateMinPlayers.Value) : 1;
                if (Humans().Count < minPl) { _annihilateSince.Clear(); return; }

                bool pvpOnly = AnnihilatePvPOnly != null && AnnihilatePvPOnly.Value;
                var sides = new List<FactionHQ>();
                if (pvpOnly)
                {
                    if (!TwoSides(out var A, out var B)) { _annihilateSince.Clear(); return; }
                    sides.Add(A); sides.Add(B);
                }
                else
                {
                    // Multi-HQ: all joinable human sides + at most one AI (preventJoin) HQ.
                    FactionHQ aiHq = null;
                    foreach (var hq in UnityEngine.Object.FindObjectsOfType<FactionHQ>())
                    {
                        if (hq == null || hq.faction == null) continue;
                        if (hq.preventJoin)
                        {
                            if (aiHq == null) aiHq = hq;
                            continue;
                        }
                        sides.Add(hq);
                    }
                    if (aiHq != null) sides.Add(aiHq);
                    if (sides.Count < 2) { _annihilateSince.Clear(); return; }
                }

                bool countAi = AnnihilateCountAI == null || AnnihilateCountAI.Value;
                // Default ON: classic AND (!canSpawn && planes==0). OFF = planes==0 only (hangars ignored).
                bool requireNoSpawn = AnnihilateRequireNoSpawn == null || AnnihilateRequireNoSpawn.Value;
                float grace = AnnihilateGraceSeconds != null ? Mathf.Max(1f, AnnihilateGraceSeconds.Value) : 20f;

                // One aircraft scan for all sides (avoid N× FindObjectsOfType).
                var planeCount = new Dictionary<FactionHQ, int>();
                foreach (var hq in sides) planeCount[hq] = 0;
                try
                {
                    foreach (var ac in UnityEngine.Object.FindObjectsOfType<Aircraft>())
                    {
                        if (ac == null) continue;
                        try { if (ac.disabled) continue; } catch { }
                        FactionHQ ahq = null; try { ahq = ac.NetworkHQ; } catch { }
                        if (ahq == null || !planeCount.ContainsKey(ahq)) continue;
                        Player pl = null; try { pl = ac.Player; } catch { }
                        if (pl != null || countAi) planeCount[ahq] = planeCount[ahq] + 1;
                    }
                }
                catch { }

                // Three buckets: healthy / pending(grace) / dead(past grace).
                // Never treat an in-grace side as "alive winner".
                var dead = new List<FactionHQ>();
                var pending = new List<FactionHQ>();
                var healthy = new List<FactionHQ>();
                var liveIds = new HashSet<int>();
                var spawnable = new Dictionary<FactionHQ, bool>();   // 1.1.30: kept for the status diag below

                foreach (var hq in sides)
                {
                    int id = hq.GetInstanceID();
                    liveIds.Add(id);
                    bool canSpawn = FactionCanSpawn(hq);
                    spawnable[hq] = canSpawn;
                    int planes = planeCount.TryGetValue(hq, out var pc) ? pc : 0;
                    // Classic AND (1.1.11): default wipe = !canSpawn && planes==0. No hangars-alone / planes-alone OR.
                    bool annihilated = (planes == 0) && (!requireNoSpawn || !canSpawn);
                    if (annihilated)
                    {
                        if (!_annihilateSince.ContainsKey(id)) _annihilateSince[id] = now;
                        if (now - _annihilateSince[id] >= grace) dead.Add(hq);
                        else pending.Add(hq);
                    }
                    else
                    {
                        _annihilateSince.Remove(id);
                        healthy.Add(hq);
                    }
                }
                if (_annihilateSince.Count > 0)
                {
                    var gone = new List<int>();
                    foreach (var k in _annihilateSince.Keys) if (!liveIds.Contains(k)) gone.Add(k);
                    foreach (var k in gone) _annihilateSince.Remove(k);
                }

                // 1.1.30 STATUS DIAGNOSTIC (rate-limited, <=1 line / 30s): the tick used to be 100%
                // silent until the moment it fired, so no live test could show WHY annihilate was or
                // wasn't arming (07-26 finding: on stock Escalation the condition is unreachable -
                // CountAI keeps planes>0 forever and hangars keep canSpawn=1). While ANY side sits at
                // planes==0 or in grace, log one per-side status line naming the blocking gate.
                try
                {
                    bool interesting = pending.Count > 0 || dead.Count > 0;
                    if (!interesting)
                        foreach (var kv in planeCount) if (kv.Value == 0) { interesting = true; break; }
                    if (interesting && now >= _nextAnnihilateDiag)
                    {
                        _nextAnnihilateDiag = now + 30f;
                        var sbA = new StringBuilder("[annihilate] status:");
                        foreach (var hq in sides)
                        {
                            string nm = "?"; try { nm = hq.faction != null ? hq.faction.factionName : "?"; } catch { }
                            int pl2 = planeCount.TryGetValue(hq, out var pv) ? pv : 0;
                            bool cs = spawnable.TryGetValue(hq, out var sv) && sv;
                            string state;
                            int id2 = hq.GetInstanceID();
                            if (_annihilateSince.TryGetValue(id2, out var since))
                                state = (now - since >= grace) ? "DEAD" : $"grace {now - since:0}/{grace:0}s";
                            else if (pl2 == 0 && requireNoSpawn && cs) state = "blocked: canSpawn (RequireNoSpawn)";
                            else if (pl2 > 0) state = "alive";
                            else state = "alive?";
                            sbA.Append($" {nm} planes={pl2} canSpawn={(cs ? 1 : 0)} [{state}];");
                        }
                        sbA.Append($" cfg: countAI={(countAi ? 1 : 0)} requireNoSpawn={(requireNoSpawn ? 1 : 0)} grace={grace:0}s");
                        Log?.LogInfo(sbA.ToString());
                    }
                }
                catch { }   // diagnostic only - never disturb the gate logic

                // Both-dead / all-wiped ONLY when nothing healthy and nothing still in grace.
                if (healthy.Count == 0 && pending.Count == 0)
                {
                    if (dead.Count == 0) return;
                    string mode = AnnihilateBothDead != null ? (AnnihilateBothDead.Value ?? "noop") : "noop";
                    if (mode.Equals("draw", StringComparison.OrdinalIgnoreCase) && sides.Count >= 2)
                    {
                        Log?.LogInfo("[annihilate] all sides wiped (no planes and no hangars) -> DRAW");
                        // ASCII '-' not an em dash: the game font has no glyph for U+2014 and draws a square.
                        Instance?.BroadcastAll("<color=#FFD200>** Annihilate - both sides wiped. DRAW. **</color>");
                        ForceDraw(sides[0], sides[1]);
                    }
                    _annihilateSince.Clear();
                    return;
                }

                // ForceVictory ONLY when exactly one healthy side remains, at least one dead, and
                // no side still sitting in grace (pending==0).
                if (healthy.Count == 1 && dead.Count >= 1 && pending.Count == 0)
                {
                    var winHQ = healthy[0];
                    string wn = "?"; try { wn = winHQ.faction != null ? winHQ.faction.factionName : "?"; } catch { }
                    var deadNames = new List<string>();
                    foreach (var d in dead)
                    {
                        string dn = "?"; try { dn = d.faction != null ? d.faction.factionName : "?"; } catch { }
                        deadNames.Add(dn);
                    }
                    Log?.LogInfo($"[annihilate] {string.Join(",", deadNames)} wiped (no planes and no hangars for {grace:0}s) -> {wn} wins");
                    Instance?.BroadcastAll($"<color=#7CFFB0>** Annihilate - {string.Join(" & ", deadNames)} has no planes and no hangars left. {wn} wins! **</color>");
                    ForceVictory(winHQ);
                    _annihilateSince.Clear();
                }
                // healthy>1 with some dead, or any pending: wait — no arbitrary pick / no false crown
            }
            catch (Exception e) { Log?.LogError("AnnihilateTick: " + e); }
        }

        // total in-game score for a side = the faction's own score (FactionHQ.factionScore),
        // which is EXACTLY the per-faction total the game displays on the leaderboard / join menu /
        // aircraft-selection (e.g. PALA 118 vs BDF 117). factionScore is a faction-wide accumulation
        // (kills, successful sorties, captures, wreck collection) and is a SEPARATE, much larger value
        // than any single player's PERSONAL PlayerScore. 0.9.48 summed each player's PlayerScore
        // instead, which is a different number entirely and produced the wrong 12/0 readout - fixed
        // to read factionScore so the announced/compared totals match the scoreboard.
        static double TeamScore(FactionHQ hq)
        {
            if (hq == null) return 0;
            try { return hq.factionScore; }
            catch { return 0; }
        }

        // End a PvP match as a DRAW. Prefer a real Draw/Tie/Stalemate EndType if the game has one.
        // NEVER fall back to Defeat — that crowns the OTHER side Victory. If no Draw EndType exists:
        // emit a plugin draw frame and skip DeclareEndGame (caller already announced; rotation handles).
        static void ForceDraw(FactionHQ a, FactionHQ b)
        {
            try
            {
                if (GameManager.gameResolution != GameResolution.Ongoing) return;
                var m = typeof(FactionHQ).GetMethod("DeclareEndGame");
                if (m != null)
                {
                    var et = m.GetParameters()[0].ParameterType;
                    object drawVal = null;
                    foreach (var name in new[] { "Draw", "Tie", "Stalemate" })
                    {
                        try { drawVal = System.Enum.Parse(et, name); break; } catch { }
                    }
                    if (drawVal != null)
                    {
                        m.Invoke(a ?? b, new object[] { drawVal });
                        return;
                    }
                }
                // No Draw EndType — do NOT use Defeat. Chat announce is already done by the caller.
                EmitDrawFrame();
                Log?.LogInfo("[draw] no Draw/Tie/Stalemate EndType — emitted plugin draw frame, skipped DeclareEndGame (no false Victory/Defeat crown)");
            }
            catch (Exception e) { Log?.LogError("ForceDraw: " + e); }
        }

        // Plugin-side draw marker for the bot (no win awards). Debounced like OnDeclareEndGame.
        static void EmitDrawFrame()
        {
            try
            {
                // 1.1.30: the old `Instance == null` Unity fake-null gate silently killed this
                // frame once the plugin GameObject died; _lastEnd is static now - no gate needed.
                if (Time.time - _lastEnd < 20f) return;
                _lastEnd = Time.time;
                EmitAll("snap");
                Out("{\"t\":\"draw\"}");
                Out("{\"t\":\"end\"}");
            }
            catch (Exception e) { Log?.LogError("EmitDrawFrame: " + e); }
        }

        // Reflection helpers for game internals the plugin can't reference directly (EndType is internal;
        // DedicatedServerManager sits in an un-imported namespace). All FAIL SAFE (null/-1) so a wrong
        // name can never fire a false defeat - it just no-ops until the names are verified in testing.
        static System.Type _dsmType; static bool _dsmResolved;
        static System.Type FindGameType(string simpleName)
        {
            foreach (var a in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type[] ts; try { ts = a.GetTypes(); } catch { continue; }
                foreach (var t in ts) if (t.Name == simpleName) return t;
            }
            return null;
        }
        static object GetMember(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            var p = t.GetProperty(name); if (p != null) return p.GetValue(o);
            var f = t.GetField(name);    if (f != null) return f.GetValue(o);
            return null;
        }
        static float CurrentMissionMaxTime()
        {
            try
            {
                if (!_dsmResolved) { _dsmType = FindGameType("DedicatedServerManager"); _dsmResolved = true; }
                if (_dsmType == null) return -1f;
                object inst = null;
                var ip = _dsmType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (ip != null) inst = ip.GetValue(null);
                else { var f = _dsmType.GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static); if (f != null) inst = f.GetValue(null); }
                object opt = GetMember(inst, "CurrentMissionOption");
                object mt = GetMember(opt, "MaxTime");
                return mt == null ? -1f : System.Convert.ToSingle(mt);
            }
            catch { return -1f; }
        }

        // ---- auto-balancer (PvP only), polled from HQTickPatch ----
        // DESIGN (2026-06-26): autobalance fires ONLY in response to a player LEAVING (a side's
        // human count drops) - NOT on joins, NOT continuously. Joining the fuller side is handled
        // separately + instantly by the join blocker (PumpBounces -> immediate spectate). So the two
        // mechanisms are cleanly split: LEAVE -> autobalance moves one to even up; JOIN over-full ->
        // the joiner is bounced. We arm on a population decrease, then HOLD for GraceSeconds (a few
        // minutes) so the gap can self-correct (a rejoin / someone filling the smaller side) before the
        // first move, and keep trying (debounce-paced) until teams are within MaxDifference, then disarm.
        static float _nextBalance, _lastMove = -999f;
        static int   _lastSideTotal = -1;     // last observed (A+B) human count; a DECREASE = someone left
        static bool  _balanceArmed;           // a leave armed autobalance; cleared once teams are even
        static float _unevenSince = -1f;      // Time.time the still-standing imbalance first appeared (grace anchor)
        internal static void MaybeBalance()
        {
            try
            {
                if (EnforceBalance == null || !EnforceBalance.Value) return;
                if (AutoMove == null || !AutoMove.Value) return;
                float now = Time.time;
                if (now < _nextBalance) return;
                _nextBalance = now + Mathf.Max(2, RecheckSeconds != null ? RecheckSeconds.Value : 6);
                Trace("BalanceTick");   // an actual balance evaluation pass ran (feature ON + rate gate passed)
                if (!TwoSides(out var A, out var B)) { _lastSideTotal = -1; _balanceArmed = false; _unevenSince = -1f; return; }
                // MIN-PLAYERS GATE (user 2026-06-27): never auto-balance a small lobby. Counts ALL humans on the
                // server (incl. spectators). Below the threshold -> disarm + reset so nothing is ever moved/warned.
                int people = Humans().Count;
                int minP = BalanceMinPlayers != null ? BalanceMinPlayers.Value : 6;
                if (people < minP) { _lastSideTotal = Side(A).Count + Side(B).Count; _balanceArmed = false; _unevenSince = -1f; return; }
                int total = Side(A).Count + Side(B).Count;
                if (_lastSideTotal >= 0 && total < _lastSideTotal) _balanceArmed = true;   // a player left -> arm
                _lastSideTotal = total;
                if (!_balanceArmed) return;                                                // ONLY act after a leave
                int max = BalanceMaxDiff != null ? BalanceMaxDiff.Value : 2;
                if (Math.Abs(Side(A).Count - Side(B).Count) <= max)                        // teams even (self-corrected or fixed)
                    { _balanceArmed = false; _unevenSince = -1f; return; }                 // -> disarm + reset the warn clock
                // armed AND uneven: broadcast a one-time warning, then HOLD for WarnSeconds (a 5-minute warning by
                // default) so the gap can self-correct (a rejoin / someone filling the smaller side) before any move.
                float warn = BalanceWarnSeconds != null ? BalanceWarnSeconds.Value : 300;
                if (_unevenSince < 0f)                                                      // first detection of THIS imbalance episode
                {
                    _unevenSince = now;
                    int bigC = Math.Max(Side(A).Count, Side(B).Count), smallC = Math.Min(Side(A).Count, Side(B).Count);
                    int mins = Mathf.Max(1, Mathf.RoundToInt(warn / 60f));
                    Instance?.BroadcastAll($"<color=#FFC857>Teams are unbalanced ({bigC} v {smallC}). If it doesn't even out, a player will be moved to balance in {mins} minute{(mins == 1 ? "" : "s")}.</color>");
                    Log?.LogInfo($"[balance] imbalance {bigC}v{smallC} with {people} on server; warned, will move in {warn:0}s if unresolved");
                }
                if (now - _unevenSince < warn) return;                                      // still inside the warning window -> wait
                BalanceOnce(false);                                                        // move one; stay armed until even
            }
            catch (Exception e) { Log?.LogError("MaybeBalance: " + e); }
        }

        // A player auto-balanced in game G is EXEMPT from being moved again until MoveExemptGames games
        // later (default 2 => "at most once per 2 games"), so the same person isn't repeatedly the one
        // moved. _gameNum advances once per mission start (AdvanceGame); expired exemptions are pruned.
        static readonly Dictionary<string, int> _movedAtGame = new Dictionary<string, int>(StringComparer.Ordinal);
        static int _gameNum;
        internal static void AdvanceGame()
        {
            _gameNum++;
            // Every netId in the world was just invalidated: a client's in-flight orders will legitimately
            // reference ids the server no longer knows. Suppress the stale-netId exploit strikes for a while.
            OpenStaleNetGrace(30f);
            int span = (BalanceMoveExemptGames != null ? BalanceMoveExemptGames.Value : 2);
            List<string> stale = null;
            foreach (var kv in _movedAtGame)
                if (_gameNum - kv.Value >= span) (stale ?? (stale = new List<string>())).Add(kv.Key);
            if (stale != null) foreach (var s in stale) _movedAtGame.Remove(s);   // exemption expired -> movable again
        }
        static bool MoveExempt(string sid)            // moved within the last MoveExemptGames games?
        {
            int span = (BalanceMoveExemptGames != null ? BalanceMoveExemptGames.Value : 2);
            return _movedAtGame.TryGetValue(sid, out var g) && (_gameNum - g) < span;
        }

        // ===================== NEW-JOINER PROTECTION (2026-06-27) =====================
        // Auto-balance protection layers, STRONGEST first (all sit INSIDE the MoveExempt filter, so a
        // player moved within the last MoveExemptGames games is never the pick while anyone non-exempt
        // remains; "everyone else moved within a couple of games" is what unlocks dipping into a
        // protected player):
        //   1) NEW JOINER - connected < NewJoinerSeconds (15 min) ago. Protected first and foremost;
        //      moved only if EVERY other non-exempt big-side player is also a new joiner.
        //   0) unprotected.
        // Within the least-protected non-empty tier we still pick whoever evens the teams' total rank weight
        // best (the existing weight/target logic).

        // ---- presence / first-seen clock (drives new-joiner protection) ----
        static readonly Dictionary<string, float> _firstSeen = new Dictionary<string, float>(StringComparer.Ordinal);
        // per-session "Nuke-Option Plugin Version X is active" PRIVATE welcome: scheduled ~6s after first
        // sighting (so the joining client's chat UI is ready), shown ONCE per session, and reset on leave
        // so a rejoin re-shows it. Parallels the _firstSeen presence clock above.
        static readonly Dictionary<string, float> _welcomeDue = new Dictionary<string, float>(StringComparer.Ordinal);
        static readonly HashSet<string> _welcomed = new HashSet<string>(StringComparer.Ordinal);
        static void TrackPresence(float now)
        {
            try
            {
                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in Humans()) { var s = Sid(p); if (!string.IsNullOrEmpty(s) && s != "0") present.Add(s); }
                foreach (var s in present) if (!_firstSeen.ContainsKey(s)) { _firstSeen[s] = now; _welcomeDue[s] = now + 6f; }   // first sighting -> join clock + schedule welcome
                if (_firstSeen.Count > present.Count)                                               // someone left -> forget them so a rejoin resets the clock + re-welcomes
                {
                    List<string> gone = null;
                    foreach (var kv in _firstSeen) if (!present.Contains(kv.Key)) (gone ?? (gone = new List<string>())).Add(kv.Key);
                    if (gone != null) foreach (var s in gone) { _firstSeen.Remove(s); _welcomeDue.Remove(s); _welcomed.Remove(s); }
                }
            }
            catch (Exception e) { Log?.LogError("TrackPresence: " + e); }
        }

        // Fire the one-time per-session PRIVATE "plugin version is active" notice for any player whose
        // scheduled welcome time has arrived. Called ~1/sec from PollCommands, right after TrackPresence.
        static void MaybeWelcome(float now)
        {
            if (_welcomeDue.Count == 0) return;
            try
            {
                foreach (var p in Humans())
                {
                    var s = Sid(p);
                    if (string.IsNullOrEmpty(s) || _welcomed.Contains(s)) continue;
                    if (!_welcomeDue.TryGetValue(s, out var due) || now < due) continue;
                    Instance?.TellPlayer(p, $"<color=#6cc8ff>Nuke-Option Plugin Version {Version} is active on this server.</color>");
                    _welcomed.Add(s);
                    _welcomeDue.Remove(s);
                }
            }
            catch (Exception e) { Log?.LogError("MaybeWelcome: " + e); }
        }
        static bool IsNewJoiner(string sid)
        {
            int win = BalanceNewJoinerSeconds != null ? BalanceNewJoinerSeconds.Value : 900;
            if (win <= 0) return false;
            if (string.IsNullOrEmpty(sid)) return false;
            if (!_firstSeen.TryGetValue(sid, out var t)) return true;          // just appeared this frame -> treat as new (protected)
            // MUST match the clock TrackPresence stamped _firstSeen with (PollCommands passes
            // realtimeSinceStartup). Reading it as Time.time compared two different clocks: realtime
            // runs ahead of the mission clock, so this went negative and EVERY player looked like a
            // new joiner forever - the strongest protection tier applied to everyone, so auto-balance
            // could never pick anybody to move and was silently inert. (round-2 audit 2026-08-01)
            return (Time.realtimeSinceStartup - t) < win;
        }

        // auto-balance protection tier (LOWER = moved sooner). See the region header above.
        static int ProtTier(string sid)
        {
            if (IsNewJoiner(sid)) return 1;     // strongest
            return 0;                           // unprotected
        }

        // ---- deferred balance moves (Balance.MoveOnlyWhenGrounded) ----------------------------------
        // A picked player who is airborne is queued here instead of being ejected mid-sortie. The pump
        // applies the move the moment they are no longer flying (died, ejected, despawned) or their
        // aircraft is LANDED - and cancels it if the reason to move them has gone away.
        sealed class PendingBalance { public Player p; public string sid; public FactionHQ destHQ; public string destName; public float since; }
        static readonly List<PendingBalance> _pendingBalance = new List<PendingBalance>();
        static float _nextPendingScan;

        // How many moves the CURRENT imbalance still needs, counting everything already queued. Each
        // move shifts the gap by 2 (one off the big side, one onto the small side).
        //
        // This is the guard the first version of this feature lacked, and it was a serious bug: a
        // deferred pick does not change any team count, so MaybeBalance stayed armed and BalanceOnce
        // queued ANOTHER player every MoveDebounce seconds until the entire big side was pending.
        // Then one busy moment - a mission end, a big engagement - landed or killed several at once and
        // every one of them swapped in the same pass, throwing 9v4 straight past even. (audit 2026-08-02)
        static int BalanceMovesStillNeeded()
        {
            if (!TwoSides(out var A, out var B)) return 0;
            int a = Side(A).Count, b = Side(B).Count;
            int maxd = BalanceMaxDiff != null ? BalanceMaxDiff.Value : 2;
            // Apply the queued moves to the projection: each pending player leaves the bigger side.
            foreach (var j in _pendingBalance)
            {
                if (j == null || j.p == null) continue;
                FactionHQ hq = null; try { hq = j.p.HQ; } catch { }
                if (hq == null) continue;
                if (ReferenceEquals(hq, A)) { a--; b++; }
                else if (ReferenceEquals(hq, B)) { b--; a++; }
            }
            int gap = Math.Abs(a - b);
            if (gap <= maxd) return 0;
            return (gap - maxd + 1) / 2;
        }

        internal static void PumpPendingBalance()
        {
            if (_pendingBalance.Count == 0) return;
            float now = Time.realtimeSinceStartup;      // WALL clock: this deadline must not freeze on pause
            if (now < _nextPendingScan) return;
            _nextPendingScan = now + 1f;
            try
            {
                int timeout = BalancePendingTimeout != null ? Math.Max(0, BalancePendingTimeout.Value) : 900;
                // Mirror MaybeBalance's own gates. Without these the pump would keep moving people after
                // the operator turned balancing off, or once the lobby dropped below MinPlayers - the very
                // conditions under which MaybeBalance refuses to pick anyone in the first place.
                bool enforce = (EnforceBalance == null || EnforceBalance.Value)
                            && (AutoMove == null || AutoMove.Value);
                int people = 0; try { people = Humans().Count; } catch { }
                int minP = BalanceMinPlayers != null ? BalanceMinPlayers.Value : 6;
                bool twoSides = TwoSides(out var A2, out var B2);
                if (!enforce || !twoSides || people < minP)
                {
                    if (_pendingBalance.Count > 0)
                        Log?.LogInfo($"[balance] dropping {_pendingBalance.Count} deferred move(s) - "
                                     + (!enforce ? "balancing is off" : (!twoSides ? "not a PvP match" : $"only {people} on server")));
                    foreach (var j in _pendingBalance) ReleaseBalanceSlot(j);
                    _pendingBalance.Clear();
                    return;
                }

                bool appliedThisPass = false;           // AT MOST ONE move per pass - see below
                for (int i = _pendingBalance.Count - 1; i >= 0; i--)
                {
                    var j = _pendingBalance[i];
                    if (j == null || j.p == null) { ReleaseBalanceSlot(j); _pendingBalance.RemoveAt(i); continue; }   // release like every sibling drop path (audit 13)

                    // Their team's top scorer may have CHANGED since this move was queued - and with an
                    // IN-GAME score that is the likely case, not the edge case: the pilot is still flying
                    // the sortie the balancer is waiting on, and every kill they take can put them top.
                    // BalanceOnce can only filter at pick time, so re-check here too, or a pilot who took
                    // the lead mid-flight still gets moved the moment they land. Release the slot so the
                    // move-exemption is not spent on a move that never happened.
                    if ((BalanceNeverMoveTop == null || BalanceNeverMoveTop.Value) && IsTeamTopScorer(j.p))
                    {
                        Log?.LogInfo($"[balance] deferred move for {RawNameOf(j.p)} dropped - they are now "
                                     + "their team's top scorer (Balance.NeverMoveTopPlayer)");
                        ReleaseBalanceSlot(j); _pendingBalance.RemoveAt(i); continue;
                    }

                    // Gone from the server -> nothing to move. The join guard funnels them to the
                    // smaller side if they come back, so dropping it here is correct.
                    bool present = false;
                    try { present = Humans().Contains(j.p); } catch { present = false; }
                    if (!present)
                    {
                        Log?.LogInfo($"[balance] deferred move for {j.sid} dropped - they left");
                        ReleaseBalanceSlot(j); _pendingBalance.RemoveAt(i); continue;
                    }

                    // No longer needed -> cancel, and say so. Moving them now would CREATE the imbalance
                    // the move existed to fix. BalanceMovesStillNeeded already discounts the queue, so
                    // this also cancels the surplus when several are pending for one needed move.
                    if (BalanceMovesStillNeeded() <= 0)
                    {
                        try { Instance?.TellPlayer(j.p, "<color=#7CFFB0>Teams evened out - you're staying "
                                                      + "where you are. Ignore the earlier message.</color>"); }
                        catch { }
                        Log?.LogInfo($"[balance] deferred move for {RawNameOf(j.p)} cancelled - no longer needed");
                        ReleaseBalanceSlot(j); _pendingBalance.RemoveAt(i); continue;
                    }

                    // STILL ON THE OVER-FULL SIDE? They may have switched themselves (!swapteam is legal
                    // in exactly the lopsided state that created this pending) or been bounced to
                    // spectate. BeginSwap derives the destination from where they are NOW, so applying
                    // blindly would send a player who already helped straight back to the fuller team.
                    FactionHQ curHQ = null; try { curHQ = j.p.HQ; } catch { }
                    if (curHQ == null || !ReferenceEquals(curHQ, j.destHQ == A2 ? B2 : A2))
                    {
                        Log?.LogInfo($"[balance] deferred move for {RawNameOf(j.p)} dropped - they are no "
                                     + "longer on the side it was going to take them off");
                        ReleaseBalanceSlot(j); _pendingBalance.RemoveAt(i); continue;
                    }

                    bool flying = IsFlying(j.p);
                    bool landed = false;
                    if (flying) { try { landed = IsGrounded(j.p.Aircraft); } catch { landed = false; } }
                    bool overdue = timeout > 0 && (now - j.since) >= timeout;

                    if (flying && !landed && !overdue) continue;      // still up there - leave them alone

                    // ONE PER PASS. BeginSwap only QUEUES the faction flip (it lands ~1s later in
                    // PumpSwaps), so Side() counts do not move within this loop. Applying two entries in
                    // one pass would therefore both read the same pre-move counts and over-correct.
                    if (appliedThisPass) continue;
                    appliedThisPass = true;

                    string why = !flying ? "died/despawned" : (landed ? "landed" : "pending-move timeout");
                    try
                    {
                        Instance?.TellPlayer(j.p, $"<color=#FFC857>Moving you to {j.destName} now to even the "
                                                + "teams. Thanks for the patience.</color>");
                    }
                    catch { }
                    // Same mechanic as an immediate balance move: team swap + Cricket spawned high over open ocean (Swap.Altitude) + eject, so
                    // their spawn UI resets to the new side. Life/points-neutral (GuardEject inside).
                    Instance?.BeginSwap(j.p, null, true);
                    Log?.LogInfo($"[balance] deferred move applied for {RawNameOf(j.p)} -> {j.destName} ({why})");
                    _pendingBalance.RemoveAt(i);
                }
            }
            catch (Exception e) { Log?.LogError("PumpPendingBalance: " + e); }
        }

        // A pending move that is cancelled or dropped never actually moved anyone, so it must give back
        // the move-exemption BalanceOnce reserved for it - otherwise every cancelled deferral quietly
        // burns a player out of the candidate pool for MoveExemptGames games and the balancer runs out
        // of people it is allowed to move.
        // A deferred move is only meaningful within the match whose imbalance created it. Left in
        // place across a mission change it carries a DESTROYED FactionHQ: Unity's op_Equality never
        // matches a destroyed object, so the direction guard silently collapses and the move can be
        // applied the WRONG WAY into the fresh match. Called from ClearMatchTeamkills. Each entry
        // gives back the move-exemption it reserved - it never actually moved anyone. (audit 13)
        internal static void ClearPendingBalance()
        {
            try
            {
                foreach (var j in _pendingBalance) ReleaseBalanceSlot(j);
                if (_pendingBalance.Count > 0)
                    Log?.LogInfo($"[balance] dropped {_pendingBalance.Count} deferred move(s) at match end");
                _pendingBalance.Clear();
            }
            catch { }
        }

        static void ReleaseBalanceSlot(PendingBalance j)
        {
            try { if (j != null && !string.IsNullOrEmpty(j.sid)) _movedAtGame.Remove(j.sid); }
            catch { }
        }

        // Performs at most one move; returns moves done. force=true ignores the debounce (used by
        // !balance). Picks the not-already-moved, non-exempt player whose rank weight best evens
        // the totals, then SWITCHES them straight to the smaller side via BeginSwap - the old path sent
        // them to spectate to rejoin, which cost them the plane and a re-pick. When the pick is airborne
        // and Balance.MoveOnlyWhenGrounded is on, the switch is deferred to PumpPendingBalance instead.
        internal static int BalanceOnce(bool force)
        {
            Trace("BalanceOnce");
            if (!TwoSides(out var A, out var B)) return 0;
            if (A.preventJoin || B.preventJoin) return 0;                 // PvP only (co-op AI side blocks)
            var pa = Side(A); var pb = Side(B);
            int max = BalanceMaxDiff != null ? BalanceMaxDiff.Value : 2;
            if (Math.Abs(pa.Count - pb.Count) <= max) return 0;
            float now = Time.time;
            if (!force && now - _lastMove < Mathf.Max(2, MoveDebounce != null ? MoveDebounce.Value : 20)) return 0;

            FactionHQ big   = pa.Count > pb.Count ? A : B;
            FactionHQ small = big == A ? B : A;
            var bigPlayers   = big == A ? pa : pb;
            var smallPlayers = small == A ? pa : pb;

            float sumBig = 0f, sumSmall = 0f;
            foreach (var p in bigPlayers) sumBig += Weight(p);
            foreach (var p in smallPlayers) sumSmall += Weight(p);
            float target = (sumBig - sumSmall) / 2f;                      // ideal weight of the player to move

            // Eligible = anyone on the big side NOT move-exempt (i.e. not auto-balanced within the last
            // MoveExemptGames games). Flying players are still eligible to be PICKED - MoveOnlyWhenGrounded
            // only defers the switch until they land or die, it does not exempt them - so the
            // balancer keeps working mid-match when everyone's airborne, and naturally falls through to the
            // next-best pick when the ideal one is exempt. (The retired Balance.MoveOnlyUnspawned bind
            // never gated this and was deleted in 1.2.0.)
            // TOP SCORER EXEMPTION - the team's current in-game score leader is filtered out here,
            // alongside the recently-moved, so every downstream step (protection tiers, the weight pick,
            // the deferred queue) simply never sees them. Only the BIG side can lose a player, so in
            // practice this protects the big side's leader; IsTeamTopScorer is per-team so the rule
            // reads the same from either side and still holds after the sides swap.
            bool exemptTop = BalanceNeverMoveTop == null || BalanceNeverMoveTop.Value;
            var movable = new List<Player>();
            Player skippedTop = null;
            foreach (var p in bigPlayers)
            {
                if (MoveExempt(Sid(p))) continue;
                if (exemptTop && IsTeamTopScorer(p)) { skippedTop = p; continue; }
                movable.Add(p);
            }
            if (movable.Count == 0)
            {
                // Distinguish the two reasons in the log: "everyone was recently moved" self-clears in a
                // game or two, whereas "the only candidate is the protected top scorer" persists for as
                // long as they lead, and an operator seeing repeated no-ops deserves to know which.
                if (skippedTop != null)
                    Log?.LogInfo($"[balance] no move - the only movable player is {RawNameOf(skippedTop)}, "
                                 + "their team's top scorer (Balance.NeverMoveTopPlayer). Teams left uneven on purpose.");
                return 0;                                                 // everyone on the big side is exempt -> wait
            }

            // Protection tiers (move the LEAST-protected first): 0 = unprotected, 1 = new joiner
            // <NewJoinerSeconds (strongest). Pick the LOWEST non-empty tier, so a new joiner is only
            // moved when every other non-exempt option is also a new joiner. (See the NEW-JOINER
            // region.) Then choose, within that tier, whoever evens the teams' total rank weight best.
            int minTier = int.MaxValue;
            foreach (var p in movable) { int t = ProtTier(Sid(p)); if (t < minTier) minTier = t; }
            var pool = new List<Player>();
            foreach (var p in movable) if (ProtTier(Sid(p)) == minTier) pool.Add(p);

            Player pick = pool[0];
            float best = Math.Abs(Weight(pick) - target);
            foreach (var p in pool) { float d = Math.Abs(Weight(p) - target); if (d < best) { best = d; pick = p; } }

            // Reserve the slot NOW (debounce + mark moved) so a 2nd player isn't scheduled while this one is
            // still pending, then SWITCH them straight to the smaller side via BeginSwap. (The old path sent
            // them to spectate to rejoin; the swap mechanic resets their UI properly, so they keep their
            // points and open life instead of losing the plane and re-picking.) If they are airborne and
            // MoveOnlyWhenGrounded is on, the switch is DEFERRED to PumpPendingBalance instead.
            _lastMove = now;
            _movedAtGame[Sid(pick)] = _gameNum;        // exempt this player from another move for MoveExemptGames games
            string tn = small.faction != null ? small.faction.factionName : "the smaller team";

            // MoveOnlyWhenGrounded: a pick who is genuinely AIRBORNE is not yanked out of their sortie.
            // Tell them it is coming and hand off to PumpPendingBalance, which applies it the moment they
            // land or die. A pick sitting on the runway or in the spawn menu is NOT deferred - promising
            // "not mid-flight" to someone parked on the tarmac and then swapping them a second later is
            // a lying message, so those fall through to the immediate swap below.
            bool onlyGrounded = BalanceMoveOnlyGrounded == null || BalanceMoveOnlyGrounded.Value;
            bool pickAirborne = false;
            if (IsFlying(pick)) { try { pickAirborne = !IsGrounded(pick.Aircraft); } catch { pickAirborne = true; } }
            if (onlyGrounded && pickAirborne)
            {
                // QUEUE CAP. Deferring does not change any team count, so MaybeBalance stays armed and
                // would otherwise queue another player every MoveDebounce until the whole side was
                // pending - then swap them all at once. Never queue more than the imbalance still needs.
                // ALREADY QUEUED -> leave the original entry alone. Removing and re-adding reset
                // `since`, and with Balance.MoveExemptGames = 0 the same weight-optimal airborne pilot is
                // re-picked every debounce, so the PendingMoveTimeout clock was pushed forward forever and
                // the documented "applied anyway" cap could never fire. Also stops re-whispering the same
                // notice every 20s. (audit 12)
                if (_pendingBalance.Exists(j => j.p == pick))
                {
                    Log?.LogInfo($"[balance] {RawNameOf(pick)} is already queued - keeping the original timer");
                    // 0, not 1: nothing moved and nothing new was queued. Returning 1 made "!balance"
                    // tell an admin "moved 1 player" for a pass that did literally nothing. The slot
                    // stays reserved (the ORIGINAL queue entry holds it); the debounce stays spent,
                    // which is what paces MaybeBalance's retries. (audit 13)
                    return 0;
                }
                if (BalanceMovesStillNeeded() <= 0)
                {
                    _movedAtGame.Remove(Sid(pick));    // nothing queued -> do not spend their exemption
                    Log?.LogInfo($"[balance] {RawNameOf(pick)} not queued - {_pendingBalance.Count} deferred "
                                 + "move(s) already cover the gap");
                    return 0;
                }
                _pendingBalance.Add(new PendingBalance { p = pick, sid = Sid(pick), destHQ = small, destName = tn,
                                                         since = Time.realtimeSinceStartup });
                try
                {
                    // Name the CAP as well as the promise. PumpPendingBalance applies the switch anyway
                    // once PendingMoveTimeout elapses, so "as soon as you land or die" on its own is a
                    // promise the balancer does not keep for a very long sortie. (audit 11)
                    int _ptSec = BalancePendingTimeout != null ? BalancePendingTimeout.Value : 900;
                    // Sub-minute values are reachable (the catalogue allows 0-3600) and rounding them to
                    // minutes announced "after 0 min", which reads as a bug and is a promise we would not
                    // keep. Fall back to seconds under a minute. (audit 12)
                    string _ptCap = _ptSec >= 60 ? $"{Mathf.RoundToInt(_ptSec / 60f)} min" : $"{_ptSec} s";
                    Instance?.TellPlayer(pick, $"<color=#FFC857>Teams are uneven, so you're being moved to {tn} "
                        + "- but not mid-flight. You'll be switched over as soon as you land or die"
                        + ((BalancePendingTimeout != null && BalancePendingTimeout.Value > 0)
                           ? $", or after {_ptCap} if you're still up." : ".")
                        + " Finish your sortie.</color>");
                }
                catch (Exception e) { Log?.LogError("BalanceOnce notify: " + e); }
                Log?.LogInfo($"[balance] picked {RawNameOf(pick)} (tier {minTier}, weight {Weight(pick):0.0}"
                             + $"/target {target:0.0}) -> DEFERRED to {tn} (airborne; waiting for land/death)");
                return 1;                              // counted as handled: the slot is taken
            }
            // Move the picked player STRAIGHT to the smaller side via the forceteamswap mechanic (team swap +
            // Cricket spawned high over open ocean (Swap.Altitude) spawn + eject -> their UI resets to the new team), instead of sending them to
            // spectate to rejoin. BeginSwap recomputes dest = the side that is NOT theirs = the smaller side
            // here. admin=null (no admin-chat; BeginSwap notifies the moved player). Keeps points + open life.
            Instance?.BeginSwap(pick, null, true);
            Log?.LogInfo($"[balance] picked {RawNameOf(pick)} (tier {minTier} [0=open,1=newjoiner], weight {Weight(pick):0.0}/target {target:0.0}) -> force-swap to {tn}; flying={IsFlying(pick)}");
            return 1;
        }

        static string Join(string[] a, int start, int end)
        {
            var sb = new StringBuilder();
            for (int i = start; i < end && i < a.Length; i++) { if (sb.Length > 0) sb.Append(' '); sb.Append(a[i]); }
            return sb.ToString();
        }

        // ============ ADMIN TEST: !swapteam / !forceteamswap (move team, keep points+life) ============
        // Two competing implementations of "move a player to the other team and reset their client spawn-menu
        // UI to the new faction, WITHOUT them losing points or their open life". The trick (verified):
        // Spawner.SpawnAircraft(... spawningHangar=null, destHQ, explicit GlobalPosition ...) is a [Server]
        // method we can call directly; Aircraft.OnStartServer auto-binds the player and the owning client's
        // OnStartClient teleports its local plane there + attaches the HUD + DynamicMap.SetFaction (the UI
        // reset), then we AdminEject so they drop back to the now-correct spawn menu. Every eject is
        // GuardEject-protected so it's life-neutral.
        //   !swapteam     : spectate -> wait despawn -> swap team -> spawn Cricket -> wait ~2s -> eject.
        //   !forceteamswap: swap team -> wait ~1s -> spawn Cricket -> wait ~2s -> eject (no initial spectate).
        // Cricket spawns HIGH over OPEN OCEAN in a quiet corner of the current map (far from every base and the
        // fight), so the brief un-piloted moment + auto-eject can never crash into terrain, a base, or another
        // plane. One ocean corner per map (verified open water via the terrain atlas).
        struct SpawnXZ { public float x, z; public SpawnXZ(float x, float z) { this.x = x; this.z = z; } }
        static readonly SpawnXZ HEART_OCEAN = new SpawnXZ(-33000f, -40000f);   // Heartland SW open ocean (nearest base ~27km)
        static readonly SpawnXZ IGNUS_OCEAN = new SpawnXZ(  8000f, -33000f);   // Ignus deep-south open ocean (nearest base ~35km)

        // Parse an "x,z" drop-point config value; malformed -> the per-map ocean fallback.
        static SpawnXZ ParseXZ(ConfigEntry<string> e, SpawnXZ fb)
        {
            try
            {
                var s = e != null ? (e.Value ?? "") : "";
                var parts = s.Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
                    && !float.IsNaN(x) && !float.IsInfinity(x) && !float.IsNaN(z) && !float.IsInfinity(z))
                    return new SpawnXZ(x, z);   // finite-only: a pasted NaN/Infinity falls back, never a NaN spawn
            }
            catch { }
            return fb;
        }

        // Faction-safe drop point for the CURRENT map + the DESTINATION team: a swapped player
        // spawns far behind their own side, at the map edge (Heartland: BDF north / PALA south;
        // Ignus: BDF east / PALA west - per Tomo's original design). Unknown faction -> the neutral
        // open-ocean corner, so odd missions never spawn anyone somewhere worse than before.
        static SpawnXZ FactionDropPos(FactionHQ hq)
        {
            bool ignus = DetectIgnus();
            string fac = "";
            try { fac = (hq != null && hq.faction != null) ? (hq.faction.factionName ?? "") : ""; } catch { }
            fac = fac.ToLowerInvariant();
            // Same faction predicate as the chat colour code (StartsWith prim/bosc or the short
            // abbreviations) so missions naming factions "PALA"/"BDF" never fall back to the ocean corner.
            if (fac.StartsWith("prim") || fac.Contains("primeva") || fac == "pala" || fac.Contains("pala"))
                return ignus ? ParseXZ(SkyDropIgnusPala, IGNUS_OCEAN) : ParseXZ(SkyDropHeartlandPala, HEART_OCEAN);
            if (fac.StartsWith("bosc") || fac.Contains("boscali") || fac == "bdf" || fac.Contains("bdf"))
                return ignus ? ParseXZ(SkyDropIgnusBdf, IGNUS_OCEAN) : ParseXZ(SkyDropHeartlandBdf, HEART_OCEAN);
            return ignus ? IGNUS_OCEAN : HEART_OCEAN;
        }

        static AircraftDefinition _cricketDef;
        static bool _cricketCatalogLogged;
        static AircraftDefinition ResolveCricket()
        {
            if (_cricketDef != null) return _cricketDef;
            try
            {
                var list = Encyclopedia.i != null ? Encyclopedia.i.aircraft : null;
                if (list != null)
                    foreach (var d in list)
                    {
                        if (d == null) continue;
                        string un = d.unitName ?? "", co = d.code ?? "";
                        if (un.IndexOf("Cricket", StringComparison.OrdinalIgnoreCase) >= 0
                         || co.IndexOf("CI-22", StringComparison.OrdinalIgnoreCase) >= 0
                         || co.Replace("-", "").IndexOf("CI22", StringComparison.OrdinalIgnoreCase) >= 0)
                        { _cricketDef = d; break; }
                    }
                if (_cricketDef != null) Log?.LogInfo($"[swap] Cricket resolved: '{_cricketDef.unitName}' (code {_cricketDef.code})");
                else if (!_cricketCatalogLogged && list != null)        // dump the catalog ONCE so we can find the real name
                {
                    _cricketCatalogLogged = true;
                    var sb = new StringBuilder("[swap] CI-22 Cricket not found. aircraft catalog: ");
                    foreach (var d in list) if (d != null) sb.Append(d.unitName).Append('|').Append(d.code).Append("  ");
                    Log?.LogWarning(sb.ToString());
                }
            }
            catch (Exception e) { Log?.LogError("ResolveCricket: " + e); }
            return _cricketDef;
        }

        // Map = Heartland vs Ignus, from the mission name (mirrors the bot's cc_web mapping:
        // Escalation => Heartland, Terminal Control => Ignus). Default Heartland when unknown.
        static bool DetectIgnus()
        {
            try
            {
                string n = null;
                try { n = MissionManager.CurrentMission != null ? MissionManager.CurrentMission.Name : null; } catch { }
                if (string.IsNullOrEmpty(n)) return false;
                n = n.ToLowerInvariant();
                return n.Contains("terminal") || n.Contains("ignus") || n.Contains("carrier duel");   // Carrier Duel runs on Ignus too (mirrors cc_web)
            }
            catch { return false; }
        }

        static GlobalPosition SwapPos(FactionHQ destHQ)
        {
            SpawnXZ c = FactionDropPos(destHQ);                          // over the DESTINATION team's own side
            float alt = SwapAltitude != null ? SwapAltitude.Value : 3000f;   // high up -> nothing to crash into
            return new GlobalPosition(c.x, alt, c.z);
        }

        // Spawn player p into a CI-22 Cricket HIGH over open ocean in a quiet corner of the map. A couple seconds
        // later AdminEject runs -> the client UI resets to the new team. The airborne eject is kept life-/points-
        // neutral by the _adminEjectGuard (no death booked), and being far out over the
        // sea means the brief un-piloted plane can never hit terrain, a base, or another aircraft. Returns the
        // Aircraft, or null on failure.
        static Aircraft SpawnCricket(Player p, FactionHQ destHQ)
        {
            try
            {
                if (p == null) return null;
                var def = ResolveCricket();
                if (def == null || def.unitPrefab == null) { Log?.LogError("[swap] no Cricket prefab"); return null; }
                var spawner = NetworkSceneSingleton<Spawner>.i;
                if (spawner == null) { Log?.LogError("[swap] no Spawner singleton yet"); return null; }
                GuardEject(Sid(p));                                          // airborne eject -> the guard keeps it life/points-neutral
                string nm = RawNameOf(p);                                    // F4: name BEFORE the action - the log can never fail a real spawn
                var gpos = SwapPos(destHQ);                                  // high over the destination team's side of this map
                var ac = spawner.SpawnAircraft(p, def.unitPrefab, default(Loadout), 1f, default(LiveryKey),
                             gpos, Quaternion.identity, Vector3.zero, null /*spawningHangar -> airborne*/, destHQ,
                             null /*uniqueName*/, 1f /*skill*/, 0.5f /*bravery*/);
                try { Log?.LogInfo($"[swap] spawned Cricket for {nm} @ ({gpos.x:0},{gpos.y:0},{gpos.z:0}) over-ocean on {(destHQ != null && destHQ.faction != null ? destHQ.faction.factionName : "?")}"); } catch { }
                return ac;                                                   // F4: a non-null spawn is returned unconditionally
            }
            catch (Exception e) { Log?.LogError("SpawnCricket: " + e); return null; }
        }

        // ---- step scheduler (parallel to _pendingMoves, pumped 1Hz from PollCommands) ----
        enum SwapPhase { Eject0, WaitDespawn, MoveToDest, Spawn, WaitThenEject, Done }
        sealed class SwapJob { public Player p, admin; public FactionHQ destHQ; public bool force; public SwapPhase phase; public float due, deadline; }
        static readonly List<SwapJob> _swaps = new List<SwapJob>();

        // PUBLIC: a player moves THEMSELVES to the other team via bare !swapteam, allowed ONLY when that team
        // has FEWER players (so it can never make PvP more lopsided). PvE is excluded automatically (TwoSides
        // is false with only one joinable faction). Keeps their points + life. Admin !swapteam <player> /
        // !forceteamswap (no balance check) stay separate, below the admin gate.
        internal void HandlePublicSwap(Player p)
        {
            try
            {
                if (!TwoSides(out var A, out var B)) { TellPlayer(p, "<color=#FFC857>!swapteam only works in a PvP match with two teams.</color>"); return; }
                FactionHQ mine = null; try { mine = p.HQ; } catch { }
                if (mine == null || (!ReferenceEquals(mine, A) && !ReferenceEquals(mine, B)))
                { TellPlayer(p, "<color=#FFC857>Pick a team first, then type !swapteam to switch to the smaller side.</color>"); return; }
                FactionHQ other = ReferenceEquals(mine, A) ? B : A;
                int mineN = Side(mine).Count, otherN = Side(other).Count;
                string on = other.faction != null ? other.faction.factionName : "the other team";
                if (otherN >= mineN)
                {
                    TellPlayer(p, $"<color=#FFC857>Can't swap to {on} - it isn't smaller ({otherN} vs your {mineN}). !swapteam only lets you move to the team with FEWER players, to keep it fair.</color>");
                    return;
                }
                BeginSwap(p, p, false);   // self-swap (swapteam mechanic): spectate -> swap -> brief Cricket -> eject; keeps points + life
            }
            catch (Exception e) { Log?.LogError("HandlePublicSwap: " + e); }
        }

        internal void BeginSwap(Player tgt, Player admin, bool force)
        {
            try
            {
                if (tgt == null) return;
                if (!TwoSides(out var A, out var B)) { TellPlayer(admin, "<color=#FFC857>Swap needs a PvP match with two joinable teams.</color>"); return; }
                FactionHQ orig = null; try { orig = tgt.HQ; } catch { }
                FactionHQ dest = ReferenceEquals(orig, A) ? B : A;       // the side that is NOT theirs
                if (dest == null || ReferenceEquals(dest, orig)) { TellPlayer(admin, "<color=#FFC857>Couldn't pick an other team to swap to.</color>"); return; }
                if (ResolveCricket() == null) { TellPlayer(admin, "<color=#FF5555>Can't swap: CI-22 Cricket not found in the aircraft catalog (see log).</color>"); return; }
                _swaps.RemoveAll(j => j.p == tgt);                       // collapse repeats / restart cleanly
                // realtimeSinceStartup, NOT Time.time: PumpSwaps is driven by PollCommands, which
                // passes realtime. Stamping the first phase deadline from the mission clock made it
                // already-elapsed on the very next pump, so every engineered settle delay between swap
                // phases collapsed to zero. (round-2 audit 2026-08-01)
                float now = Time.realtimeSinceStartup;
                GuardEject(Sid(tgt));
                var job = new SwapJob { p = tgt, admin = admin, destHQ = dest, force = force };
                if (force)
                {
                    if (IsFlying(tgt)) AdminEject(tgt);                  // keep the spawn-replace auto-eject life-neutral
                    job.phase = SwapPhase.MoveToDest; job.due = now + 1f;
                }
                else { job.phase = SwapPhase.Eject0; job.due = now; }
                _swaps.Add(job);
                // F4: the job is queued - the start notices are cosmetic and isolated.
                try
                {
                    string df = dest.faction != null ? dest.faction.factionName : "the other team";
                    if (admin != null) TellPlayer(admin, $"<color=#36FFD0>{(force ? "forceteamswap" : "swapteam")} {RankedName(tgt)} -> {df} started...</color>");
                    else TellPlayer(tgt, $"<color=#FFC857>You're being moved to {df} to balance the teams - you keep your points and progress.</color>");
                    Log?.LogInfo($"[swap] begin {(force ? "force " : "")}{RawNameOf(tgt)} -> {df}{(admin == null ? " [autobalance]" : "")}");
                }
                catch (Exception e) { Log?.LogError("BeginSwap notice: " + e); }
            }
            catch (Exception e) { Log?.LogError("BeginSwap: " + e); }
        }

        internal static void PumpSwaps(float now)
        {
            for (int i = _swaps.Count - 1; i >= 0; i--)
            {
                var j = _swaps[i];
                if (j.p == null) { _swaps.RemoveAt(i); continue; }
                if (now < j.due) continue;
                try
                {
                    Trace("SwapPhase_" + j.phase);   // per-phase coverage: Eject0/WaitDespawn/MoveToDest/Spawn/WaitThenEject
                    switch (j.phase)
                    {
                        case SwapPhase.Eject0:
                            AdminEject(j.p); Instance?.MovePlayer(j.p, null);            // life-neutral eject -> spectate
                            j.phase = SwapPhase.WaitDespawn; j.due = now + 1f; j.deadline = now + 5f; break;
                        case SwapPhase.WaitDespawn:
                            bool gone = false; try { gone = j.p.Aircraft == null; } catch { gone = true; }
                            if (gone || now >= j.deadline) { j.phase = SwapPhase.MoveToDest; j.due = now; }
                            else j.due = now + 1f; break;
                        case SwapPhase.MoveToDest:
                            Instance?.MovePlayer(j.p, j.destHQ);                          // server-side faction flip (UI not reset yet)
                            j.phase = SwapPhase.Spawn; j.due = now + 0.2f; break;
                        case SwapPhase.Spawn:
                            var ac = SpawnCricket(j.p, j.destHQ);
                            if (ac == null)
                            {
                                try { Instance?.TellPlayer(j.admin, "<color=#FF5555>Swap failed: couldn't spawn the Cricket (see log).</color>"); }
                                catch (Exception e) { Log?.LogError("PumpSwaps fail confirm: " + e); }
                                j.phase = SwapPhase.Done;
                            }
                            else { j.phase = SwapPhase.WaitThenEject; j.due = now + 2f; } break;
                        case SwapPhase.WaitThenEject:
                            AdminEject(j.p);                                              // eject -> client drops to the NEW team's spawn menu
                            // F4: the eject already ran - the completion message is cosmetic and isolated
                            try
                            {
                                string df = j.destHQ != null && j.destHQ.faction != null ? j.destHQ.faction.factionName : "the new team";
                                Instance?.TellPlayer(j.admin, $"<color=#36FFD0>Swap complete: {RankedName(j.p)} is now on {df}.</color>");
                            }
                            catch (Exception e) { Log?.LogError("PumpSwaps complete confirm: " + e); }
                            j.phase = SwapPhase.Done; break;
                    }
                    if (j.phase == SwapPhase.Done) _swaps.RemoveAt(i);
                }
                catch (Exception e) { Log?.LogError("PumpSwaps: " + e); _swaps.RemoveAt(i); }
            }
        }

        // Dump the live aircraft catalog to the log. There is no other way to LEARN the list: the
        // display names live in Unity asset data, not in the assembly, so they cannot be read offline.
        internal static void LogAircraftCatalog(string why)
        {
            try
            {
                var list = Encyclopedia.i != null ? Encyclopedia.i.aircraft : null;
                if (list == null) { Log?.LogWarning($"[aircraft] catalog unavailable ({why})"); return; }
                var sb = new StringBuilder($"[aircraft] catalog ({why}): ");
                int n = 0;
                foreach (var d in list)
                {
                    if (d == null) continue;
                    string jk = ""; try { jk = d.jsonKey ?? ""; } catch { }
                    sb.Append('{').Append("code=").Append(d.code ?? "")
                      .Append(" name=").Append(d.unitName ?? "")
                      .Append(" json=").Append(jk).Append("} ");
                    n++;
                }
                Log?.LogWarning(sb.Append($"  [{n} entries]").ToString());
            }
            catch (Exception e) { Log?.LogError("LogAircraftCatalog: " + e); }
        }

        // ================= !forfeit / !f : a team votes to SURRENDER (PvP only) =================
        // A player types !forfeit / !f to start (or add to) a vote among THEIR team to end the match as a
        // loss for them / a win for the other team. Passes when a MAJORITY of the team's current players
        // have agreed. The vote stays open for a short window; a fresh vote can't START until the cooldown
        // (default 90s, measured from the previous vote's start) elapses. Keyed by faction name, reset on
        // a new mission. Forfeit = the OTHER team's HQ declares Victory (same path as a normal win).
        sealed class ForfeitVote { public readonly HashSet<string> voters = new HashSet<string>(StringComparer.Ordinal); public float startedAt; }
        static readonly Dictionary<string, ForfeitVote> _forfeitVotes = new Dictionary<string, ForfeitVote>(StringComparer.Ordinal);
        const float ForfeitWindow = 60f;            // seconds a started vote keeps collecting agreement
        internal static void ClearForfeitVotes() { _forfeitVotes.Clear(); }

        // Forfeit telemetry -> logs/console.log -> bot -> activity feed. The in-game messages are
        // deliberately private to the forfeiting side so the enemy is not tipped off; that secrecy
        // does not apply to the operator's own log, which is why every vote is emitted here.
        // Never throws into the caller: a telemetry failure must not break a forfeit.
        static void EmitForfeit(Player p, string myFac, string foeFac, int yes, int need, int teamSize,
                                bool started, bool passed)
        {
            try
            {
                Out("{\"t\":\"forfeit\",\"id\":\"" + Sid(p) + "\",\"n\":\"" + Esc(RawNameOf(p))
                    + "\",\"f\":\"" + Esc(myFac ?? "") + "\",\"foe\":\"" + Esc(foeFac ?? "")
                    + "\",\"yes\":" + yes + ",\"need\":" + need + ",\"team\":" + teamSize
                    + ",\"started\":" + (started ? "true" : "false")
                    + ",\"passed\":" + (passed ? "true" : "false") + "}");
            }
            catch (Exception e) { Log?.LogError("EmitForfeit: " + e); }
        }

        internal void HandleForfeit(Player p)
        {
            try
            {
                if (ForfeitEnabled != null && !ForfeitEnabled.Value) { TellPlayer(p, "<color=#FFC857>Forfeit is disabled.</color>"); return; }
                FactionHQ callerHQ = null; try { callerHQ = p.HQ; } catch { }
                if (callerHQ == null) { TellPlayer(p, "<color=#FFC857>Join a team first - spectators can't call a forfeit.</color>"); return; }
                if (!TwoSides(out var A, out var B)) { TellPlayer(p, "<color=#FFC857>Forfeit votes are only for PvP matches.</color>"); return; }
                // MINIMUM TEAM SIZE. need = team.Count/2 + 1, so a team of ONE needed exactly one vote:
                // a single player alone on a side could end the entire match instantly for everyone on
                // the other side. A forfeit is a team decision, so require a team.
                {
                    int myTeamSize = 0; try { myTeamSize = Side(callerHQ).Count; } catch { }
                    int minTeam = ForfeitMinTeam != null ? ForfeitMinTeam.Value : 3;
                    if (myTeamSize < minTeam)
                    {
                        TellPlayer(p, $"<color=#FFC857>Not enough players on your team to call a forfeit "
                                    + $"({myTeamSize}/{minTeam}).</color>");
                        return;
                    }
                }
                FactionHQ otherHQ = (callerHQ == A) ? B : (callerHQ == B) ? A : null;
                if (otherHQ == null) { TellPlayer(p, "<color=#FFC857>Couldn't find your opposing team.</color>"); return; }
                string myFac  = callerHQ.faction != null ? callerHQ.faction.factionName : "your team";
                string foeFac = otherHQ.faction  != null ? otherHQ.faction.factionName  : "the other team";

                float now = Time.time;
                float cd  = ForfeitCooldownSeconds != null ? ForfeitCooldownSeconds.Value : 90;
                float window = Math.Min(ForfeitWindow, cd);
                _forfeitVotes.TryGetValue(myFac, out var vote);
                bool active = vote != null && (now - vote.startedAt) < window;
                bool started = false;
                if (!active)                                                 // need to START a new vote
                {
                    if (vote != null && (now - vote.startedAt) < cd)         // still cooling down
                    {
                        int left = (int)Math.Ceiling(cd - (now - vote.startedAt));
                        TellPlayer(p, $"<color=#FFC857>Forfeit vote on cooldown - try again in {left}s.</color>");
                        return;
                    }
                    vote = new ForfeitVote { startedAt = now };
                    _forfeitVotes[myFac] = vote;
                    started = true;
                }
                vote.voters.Add(Sid(p));

                // tally against the CURRENT team (someone who left no longer counts; threshold tracks live size)
                var team = Side(callerHQ);
                var teamSids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var tp in team) teamSids.Add(Sid(tp));
                int yes = 0; foreach (var v in vote.voters) if (teamSids.Contains(v)) yes++;
                int need = team.Count / 2 + 1;                               // majority of the current team

                if (yes >= need)
                {
                    _forfeitVotes.Remove(myFac);
                    BroadcastAll($"<color=#FF6A6A>** {myFac} has FORFEITED the match - {foeFac} wins! **</color>");
                    Log?.LogInfo($"[forfeit] {myFac} forfeited ({yes}/{team.Count}) -> declaring {foeFac} victory");
                    // 1.2.5: the feed never saw forfeits at all - the line above goes to
                    // BepInEx/LogOutput.log, which the bot does not tail. Emit a real frame.
                    EmitForfeit(p, myFac, foeFac, yes, need, team.Count, false, true);
                    ForceVictory(otherHQ);
                    return;
                }
                // not passed yet: tell the FORFEITING team only (don't tip off the enemy).
                // F4: the vote is already registered - the notify pass is cosmetic and isolated.
                EmitForfeit(p, myFac, foeFac, yes, need, team.Count, started, false);
                try
                {
                    string lead = started ? $"{RankedName(p)} called a FORFEIT vote. " : "";
                    foreach (var tp in team)
                        TellPlayer(tp, $"<color=#FFC857>{lead}Forfeit (surrender) vote: {yes}/{need} of {myFac}. Type <color=#55FF55>!forfeit</color> / <color=#55FF55>!f</color> to agree.</color>");
                }
                catch (Exception e) { Log?.LogError("forfeit notify: " + e); }
            }
            catch (Exception e) { Log?.LogError("HandleForfeit: " + e); }
        }

        // Declare `winner`'s faction the victor -> ends the match (same call the PvE timeout uses).
        static void ForceVictory(FactionHQ winner)
        {
            try
            {
                if (winner == null) return;
                if (GameManager.gameResolution != GameResolution.Ongoing) return;   // already ended -> guard
                var m = typeof(FactionHQ).GetMethod("DeclareEndGame");
                if (m == null) { Log?.LogError("[forfeit] DeclareEndGame not found"); return; }
                object victory;
                try { victory = System.Enum.Parse(m.GetParameters()[0].ParameterType, "Victory"); }
                catch (Exception e) { Log?.LogError("[forfeit] EndType parse: " + e); return; }
                m.Invoke(winner, new object[] { victory });
            }
            catch (Exception e) { Log?.LogError("ForceVictory: " + e); }
        }

        // ---- in-game chat commands ----
        // PUBLIC: !autobalance/!ab (explainer). ADMIN (SteamID in [Admin] SteamIds):
        // !move <player> <faction>, !spec [player], !join <player> <faction>, !balance.
        internal bool TryHandleChatCommand(ChatManager cm, Player p, string msg)
        {
            try
            {
                string t = (msg ?? "").TrimStart();
                if (t.Length == 0 || t[0] != '!') return false;
                Trace("ChatCommand");   // a !command reached the chat-command head
                var parts = t.Substring(1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return false;
                string cmd = parts[0].ToLowerInvariant();

                if (cmd == "autobalance" || cmd == "ab") { Cm = cm; ExplainAutobalance(p); return true; }

                // PUBLIC: any player may call/second a forfeit (surrender) vote for their own team.
                if (cmd == "forfeit" || cmd == "f" || cmd == "ff" || cmd == "surrender") { Cm = cm; HandleForfeit(p); return true; }

                // PUBLIC: anyone may send THEMSELVES to spectate with a bare !spec / !spectate.
                if ((cmd == "spec" || cmd == "spectate") && parts.Length == 1)
                {
                    Cm = cm; RequestMove(p, null, true); return true;
                }
                // PUBLIC: a bare !swapteam moves YOU to the other team, but only if it has FEWER players (PvP
                // balance). Admin "!swapteam <player>" / !forceteamswap fall through to the admin gate below.
                if (cmd == "swapteam" && parts.Length == 1) { Cm = cm; HandlePublicSwap(p); return true; }

                bool ours = cmd == "move" || cmd == "team" || cmd == "join"
                         || cmd == "spec" || cmd == "spectate" || cmd == "unteam" || cmd == "balance"
                         || cmd == "setrank" || cmd == "setfunds" || cmd == "addfunds"
                         || cmd == "swapteam" || cmd == "forceteamswap";
                if (!ours) return false;                                  // not ours -> normal chat
                Cm = cm;
                if (!IsAdmin(p)) { TellPlayer(p, "<color=#FF5555>You're not authorised to use that command.</color>"); return true; }

                if (cmd == "balance")
                {
                    int n = BalanceOnce(true);
                    TellPlayer(p, n > 0 ? "<color=#36FFD0>Balance pass: moved 1 player.</color>"
                                        : "<color=#FFC857>Balance pass: nothing to do (need a lopsided PvP match with someone movable).</color>");
                    return true;
                }
                if (cmd == "setrank")                                     // !setrank <player> <n> : set in-game rank
                {
                    if (parts.Length < 3) { TellPlayer(p, "<color=#FFC857>usage: !setrank <player> <number></color>"); return true; }
                    if (!int.TryParse(parts[parts.Length - 1], out int rk)) { TellPlayer(p, "<color=#FF5555>Rank must be a whole number.</color>"); return true; }
                    var tgt = Resolve(p, Join(parts, 1, parts.Length - 1));
                    if (tgt != null) { SetPlayerRank(tgt, rk); TellPlayer(p, $"<color=#36FFD0>Set {RankedName(tgt)}'s in-game rank to {tgt.PlayerRank}.</color>"); }
                    return true;
                }
                if (cmd == "setfunds" || cmd == "addfunds")              // !setfunds/!addfunds <player> <amount> : in-game funds
                {
                    bool add = cmd == "addfunds";
                    if (parts.Length < 3) { TellPlayer(p, $"<color=#FFC857>usage: !{cmd} <player> <amount></color>"); return true; }
                    if (!float.TryParse(parts[parts.Length - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float amt))
                    { TellPlayer(p, "<color=#FF5555>Amount must be a number.</color>"); return true; }
                    var tgt = Resolve(p, Join(parts, 1, parts.Length - 1));
                    if (tgt != null) { SetPlayerFunds(tgt, amt, add); TellPlayer(p, $"<color=#36FFD0>{(add ? "Added" : "Set")} {RankedName(tgt)}'s funds {(add ? "by " : "to ")}{amt:0} (now {tgt.Allocation:0}).</color>"); }
                    return true;
                }
                if (cmd == "spec" || cmd == "spectate" || cmd == "unteam")
                {
                    Player tgt = parts.Length >= 2 ? Resolve(p, Join(parts, 1, parts.Length)) : p;
                    if (tgt != null) { RequestMove(tgt, null, true); if (tgt != p) TellPlayer(p, $"<color=#36FFD0>Moved {RankedName(tgt)} to spectate.</color>"); }
                    return true;
                }
                if (cmd == "swapteam" || cmd == "forceteamswap")          // ADMIN TEST: move team + brief Cricket spawn + eject (resets the client UI)
                {
                    if (parts.Length < 2) { TellPlayer(p, $"<color=#FFC857>usage: !{cmd} <player></color>"); return true; }
                    var tgt = Resolve(p, Join(parts, 1, parts.Length));
                    if (tgt != null) BeginSwap(tgt, p, cmd == "forceteamswap");
                    return true;
                }
                // move / team / join :  <player> <faction>   (faction is the last token)
                if (parts.Length < 3) { TellPlayer(p, $"<color=#FFC857>usage: !{cmd} <player> <boscali|primeva></color>"); return true; }
                string facKey = parts[parts.Length - 1];
                var hq = FindFaction(facKey);
                if (hq == null) { TellPlayer(p, $"<color=#FF5555>Unknown faction '{facKey}' (use boscali / primeva).</color>"); return true; }
                var target = Resolve(p, Join(parts, 1, parts.Length - 1));
                if (target != null)
                {
                    RequestMove(target, hq, false);
                    string fn = hq.faction != null ? hq.faction.factionName : "the team";
                    TellPlayer(p, IsFlying(target)
                        ? $"<color=#36FFD0>{RankedName(target)} -> {fn} (airborne: 10s warning sent).</color>"
                        : $"<color=#36FFD0>Moved {RankedName(target)} to {fn}.</color>");
                }
                return true;
            }
            catch (Exception e) { Log?.LogError("TryHandleChatCommand: " + e); return false; }
        }

        void ExplainAutobalance(Player p)
        {
            bool on = EnforceBalance != null && EnforceBalance.Value;
            bool mv = AutoMove != null && AutoMove.Value;
            int max = BalanceMaxDiff != null ? BalanceMaxDiff.Value : 2;
            TellPlayer(p, "<color=#36FFD0>== Auto-balance (PvP only) ==</color>");
            TellPlayer(p, $"Teams are kept within {max} of each other. If you join the side that already has more players you're moved straight to spectate (no warning) - just reopen the map and join the smaller side.");
            // 1.2.0: quote the REAL pre-move hold (Balance.WarnSeconds), not the retired Balance.GraceSeconds.
            // Same expression MaybeBalance uses for its in-match broadcast, so !autobalance and the broadcast
            // can never disagree (450s -> both say 8 min; the old integer division would have said 7).
            if (mv) { int gmin = Mathf.Max(1, Mathf.RoundToInt((BalanceWarnSeconds != null ? BalanceWarnSeconds.Value : 300) / 60f));
                TellPlayer(p, $"When someone LEAVES and a side ends up more than {max} ahead, the server waits ~{gmin} min (in case the gap fills back in), then switches ONE player to the smaller side - picking whoever keeps both teams' total rank as even as possible." + ((BalanceMoveOnlyGrounded == null || BalanceMoveOnlyGrounded.Value) ? " If they are AIRBORNE you are told first and the switch waits until you land or die, so a sortie is not cut short"
                 + ((BalancePendingTimeout != null && BalancePendingTimeout.Value > 0)
                    ? $" (up to {(BalancePendingTimeout.Value >= 60 ? Mathf.RoundToInt(BalancePendingTimeout.Value / 60f) + " min" : BalancePendingTimeout.Value + " s")}, after which it applies anyway)." : ".") : " Airborne picks are switched mid-flight.")); }
            else    TellPlayer(p, "Auto-move is currently OFF (join-blocking only).");
            TellPlayer(p, "New pilots (first ~15 min) are never moved.");
            // Gated on the live setting, not stated unconditionally: this list has lied to players before
            // by quoting a rule the balancer was not actually applying (the retired GraceSeconds), and an
            // exemption nobody can see in game is exactly the kind of thing that reads as favouritism.
            if (mv && (BalanceNeverMoveTop == null || BalanceNeverMoveTop.Value))
                TellPlayer(p, "Each team's <color=#FFD200>top scorer this match</color> is never auto-balanced.");
            TellPlayer(p, $"<color=#FFC857>Co-op (PvE) is never balanced.</color>  Status: {(on ? "ON" : "OFF")}.");
        }
        // ================= end force-move / auto-balance =================

        // -------- chat reformat --------
        static void LoadRankMap()
        {
            try
            {
                var fi = new FileInfo(RankFilePath);
                if (!fi.Exists || fi.LastWriteTimeUtc.Ticks == _rankFileTicks) return;
                _rankFileTicks = fi.LastWriteTimeUtc.Ticks;
                RankMap.Clear();
                RankWeight.Clear();
                NameFallback.Clear();
                foreach (var line in File.ReadAllLines(RankFilePath))
                {
                    var parts = line.Split('|');                        // sid|ABBR|#hex[|rankIndex][|FullName][|LastKnownName]
                    if (parts.Length >= 3)
                    {
                        string sid = parts[0].Trim();
                        int w = 1;                                       // 4th field = numeric rank 1..11 (for balancing)
                        if (parts.Length >= 4) int.TryParse(parts[3].Trim(), out w);
                        string full = (parts.Length >= 5 && parts[4].Trim().Length > 0) ? parts[4].Trim() : parts[1];
                        RankMap[sid] = (parts[1], parts[2].Trim(), full);
                        RankWeight[sid] = w < 1 ? 1 : w;
                        // 1.1.29 (rank-file contract v6): 6th field = the bot's last-known display
                        // name for this sid (pipe/newline-stripped by the bot; empty = unknown).
                        // Backwards compatible both directions: an old 5-field file just never
                        // fills the dict; an old plugin ignores the extra field.
                        if (parts.Length >= 6)
                        {
                            string nf = parts[5].Trim();
                            if (nf.Length > 0) NameFallback[sid] = nf;
                        }
                    }
                }
            }
            catch (Exception e) { Log?.LogError("LoadRankMap: " + e); }
        }

        // CHAT REROUTE (1.1.28, F5; colours + TTS amended 1.1.29): the game update deleted the
        // synced PlayerName, so rank can no longer ride the native chat name - while Chat.CustomChat
        // is ON we compose the whole chat line server-side ("[TAG] Name: msg" in the EXACT native
        // GetTextColor colour for the chat mode: all-chat = desaturated faction tint, ally chat =
        // the native alliedChat colour) and deliver it per-recipient over RpcTargetServerMessage,
        // replicating the native guards + recipient loop (game ChatManager.UserCode_CmdSendChatMessage).
        // Returning TRUE makes the ChatReformatPatch prefix return false -> the native relay is
        // suppressed (no duplicate).
        // FAIL-OPEN EVERYWHERE: CustomChat off, any reflective bind/invoke failure (logged once), or
        // a failed native guard -> return false -> the untouched native path runs, so chat can never
        // die. Known reroute costs (owner judges live via Chat.CustomChat): the line renders as a
        // server message, client-side mute lists no longer filter it, and TTS does NOT read it
        // (1.1.29 owner call: runTts=false - clients were hearing every line as "server said ...").
        // Commands that MUST stay visible in public chat: the ballot counts these lines and players
        // need to see one another voting. Everything else typed with a leading '!' is hidden.
        static readonly HashSet<string> PublicCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "votemap", "vote", "y", "yes", "n", "no",
            "forfeit", "f", "ff", "surrender",
            "1", "2", "3", "4", "5", "6",
        };

        // Raised for the duration of ONE CmdSendChatMessage whose text is a hidden command. While it is
        // up, TargetReceiveMessage delivers to nobody - but the native handler still RUNS, so the game
        // writes its console line and the bot can still dispatch the command. ThreadStatic + cleared in
        // a finalizer so a throw inside the game cannot leave chat muted.
        [ThreadStatic] internal static bool SuppressNativeChatSend;

        // TRUE when this message should be hidden from public chat: it starts with '!' and its verb is
        // not on the whitelist. Fails toward VISIBLE - a parse problem must never silence real chat.
        internal static bool IsHiddenCommand(string msg)
        {
            try
            {
                string t = (msg ?? "").TrimStart();
                if (t.Length < 2 || t[0] != '!') return false;
                var verb = t.Substring(1).Split(new[] { ' ', '\t' }, 2)[0].Trim();
                if (verb.Length == 0) return false;
                // ANY all-digit verb is a ballot vote and must stay visible. The whitelist below
                // enumerated "1".."6", which silently muted !7-!10 the moment S2's ballot grew to
                // ten options (coop 4 + pvp 6): the votes still counted, but nobody - not even the
                // voter - saw the line, so players re-typed them against the game's chat rate
                // limit. A list in the PLUGIN cannot track a BOT-side ballot-size change without a
                // redeploy, so the rule is structural instead.
                bool allDigits = true;
                for (int i = 0; i < verb.Length; i++)
                    if (verb[i] < '0' || verb[i] > '9') { allDigits = false; break; }
                if (allDigits) return false;
                return !PublicCommands.Contains(verb);
            }
            catch { return false; }
        }

        static MethodInfo _miValidateChatSize, _miCheckRateLimit;
        static bool _chatRerouteDead, _chatRerouteDeadLogged;
        static void DisableChatReroute(string why)
        {
            _chatRerouteDead = true;
            if (_chatRerouteDeadLogged) return;
            _chatRerouteDeadLogged = true;
            Log?.LogWarning("[chat] reroute DISABLED -> pure native chat (" + why + ")");
        }
        static bool BindChatGuards()
        {
            if (_chatRerouteDead) return false;
            if (_miValidateChatSize != null && _miCheckRateLimit != null) return true;
            try
            {
                _miValidateChatSize = AccessTools.Method(typeof(ChatManager), "ValidateChatMessageSize", new[] { typeof(string) });
                _miCheckRateLimit   = AccessTools.Method(typeof(ChatManager), "CheckRateLimit", new[] { typeof(Player), typeof(bool), typeof(bool) });
            }
            catch { }
            if (_miValidateChatSize != null && _miCheckRateLimit != null) return true;
            DisableChatReroute("ValidateChatMessageSize/CheckRateLimit unresolved");
            return false;
        }

        internal bool FormatAndBroadcast(ChatManager cm, Player player, string message, bool allChat)
        {
            Trace("ChatReroute");
            Cm = cm;   // cache the live ChatManager the game just handed us (shared static cache)
            int sent = 0;
            try
            {
                if (CustomChat == null || !CustomChat.Value) return false;   // feature off -> pure native chat
                if (cm == null || player == null) return false;
                // BOT-COMMAND BYPASS (1.1.30, audit gate): a rerouted line never reaches the game's
                // native chat log, which is the ONLY way the bot sees chat. Suppressing '!' messages
                // would silently blind every bot command + the map vote (!votemap/!rank/!y/!n/...).
                // Commands always take the pure-native path; only ordinary chat is rank-composed.
                {
                    string probe = (message ?? "").TrimStart();
                    if (probe.StartsWith("!")) return false;
                }
                if (!BindChatGuards()) return false;                          // bind failure -> native (fail-open)

                // Native guards, same order as the game's own CmdSendChatMessage body. A failed
                // guard returns FALSE -> the native path re-runs the same check and applies its own
                // SetError/penalty (we never duplicate that machinery).
                if (!(_miValidateChatSize.Invoke(cm, new object[] { message }) is bool okSize) || !okSize) return false;
                if (!(_miCheckRateLimit.Invoke(cm, new object[] { player, true, true }) is bool okRate) || !okRate) return false;

                string sid = Sid(player);
                string raw = RawNameOf(player);
                FactionHQ hq = null; try { hq = player.HQ; } catch { }
                // 1.1.36 (Tomo): ally chat only reaches allies, so render the WHOLE line in the
                // ally blue #008FFF; all-chat keeps the faction-coloured name + white message.
                // Chat colour is ABSOLUTE and one line serves everyone - unchanged since 1.1.30.
                // (A per-viewer blue/red variant was added in 1.3.9 and reverted in 1.3.12:
                // chat was working as intended.)
                string col = ChatFactionHex(hq);
                string name = Prefixed(sid, raw);                         // 32-char cap trims the raw tail, never the tag
                string line = allChat
                    ? $"<color={col}>{SafeText(name)}:</color> {SafeText(message)}"
                    : $"<color=#008FFF>(ally) {SafeText(name)}: {SafeText(message)}</color>";

                // 1.1.37: a rerouted line never reaches the game's native chat log, so ordinary
                // chat left ZERO server-side record (moderation/evidence blind spot). Log it here.
                // COSMETIC HARD RULE: this log line must never fail the send - swallow everything.
                // 1.1.41: name/message are attacker-controlled and reach this line RAW off the
                // wire, so a crafted newline could forge whole "[Info : NukeStats] [chat] ..."
                // entries in LogOutput.log - i.e. fake evidence in the evidence log. LogText kills
                // control chars but KEEPS <> so attempted markup stays visible as evidence.
                try { Log?.LogInfo($"[chat] {(allChat ? "all" : "ally")} {LogText(RawNameOf(player))} ({Sid(player)}): {LogText(message)}"); } catch { }

                // 1.2.0: TELL THE BOT. The reroute suppresses the native CmdSendChatMessage console
                // line the bot parses, so without this the activity feed, the web CC and any external
                // monitor never see ordinary chat - only '!' commands, which bypass the reroute. The
                // bot has carried a t:"chat" handler for exactly this since the reroute landed. Out()
                // goes to console.log (what the bot tails); the LogInfo above goes to the BepInEx log,
                // which is a DIFFERENT file. Never fail a delivered message because telemetry threw.
                try
                {
                    Out("{\"t\":\"chat\",\"id\":\"" + Sid(player)
                        + "\",\"n\":\"" + Esc(RawNameOf(player))
                        + "\",\"msg\":\"" + Esc(message)
                        + "\",\"all\":" + (allChat ? "true" : "false") + "}");
                }
                catch { }

                // Recipient loop = the native one: every player when allChat, same-HQ only otherwise.
                foreach (var target in Humans())
                {
                    if (target == null || target.Owner == null) continue;
                    if (!allChat)
                    {
                        FactionHQ thq = null; try { thq = target.HQ; } catch { }
                        if (!ReferenceEquals(thq, hq)) continue;              // team chat reaches ONLY teammates
                    }
                    // 1.4.5: per-recipient shield -
                    // a recipient torn down mid-frame (passed the Owner==null check, connection gone by
                    // the send) must not reach the outer catch, which DisableChatReroutes the WHOLE
                    // server until restart. Log and move on; everyone else still gets the line.
                    try { cm.RpcTargetServerMessage(target.Owner, line, false); sent++; }   // 1.1.29: runTts=FALSE - owner call, no more clients hearing "server said ..."
                    catch (Exception ex)
                    {
                        Log?.LogError("[chat] custom line send failed for one recipient (skipped): " + ex);
                    }
                }
                return true;   // handled -> prefix suppresses the native relay
            }
            catch (Exception e)
            {
                // Reflective invoke / compose / send failure: disable the reroute (logged once) so the
                // NEXT message is pure native. THIS message: if nobody got it yet, fall back to native
                // (false); if some recipients already got the rerouted line, suppress the native relay
                // (true) so nobody sees it twice.
                DisableChatReroute("reroute failed: " + e.Message);
                Log?.LogError("FormatAndBroadcast: " + e);
                return sent > 0;
            }
        }

        // -------- string helpers --------
        static string SafeText(string s)   // for raw-rendered server messages: strip markup + control chars
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(c == '<' || c == '>' || c < 0x20 ? ' ' : c);
            return sb.ToString();
        }
        // For EVIDENCE log lines (rerouted chat): flattens newlines/CR/tabs and every other control
        // char to a space so untrusted text can never forge extra log entries, but deliberately
        // KEEPS '<' and '>' (unlike SafeText) - an attempted "<color=...>" or "</b>" must stay
        // readable in the log, since the markup attempt is itself the evidence. Never throws.
        static string LogText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(c < 0x20 || c == 0x7F ? ' ' : c);
            return sb.ToString();
        }
        static string Num(object o) { try { return Convert.ToString(o, CultureInfo.InvariantCulture) ?? "0"; } catch { return "0"; } }
        static string Esc(string s)        // JSON string escaping
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // ---------------- profanity (racist-slur) gate ----------------
        // The in-game filter doesn't work, so we screen chat here. If ANY single token of
        // a message resolves to a racist slur, the WHOLE message is swapped for the canned
        // line below, BEFORE it broadcasts. We deliberately DO NOT touch ordinary swearing
        // (fuck/cunt/shit/crap and Aussie banter) - only racial/ethnic slurs. The list is
        // curated to be liberal on slur SPELLINGS (leetspeak, spacing, repeats, a few
        // Cyrillic/accented look-alikes are all normalised away) while avoiding collisions
        // with innocent words via two passes:
        //   * STRONG (substring, whole de-spaced message): only distinctive roots that
        //     cannot form inside innocent text - catches "fucknigger" and "n i g g e r".
        //   * FULL  (anchored, per whitespace token): the complete list - anchoring lets
        //     short roots match safely, so "coon"/"spic"/"paki"/"abo" hit but raccoon,
        //     spicy, Pakistan, about, Japan, squawk, minigame, niqab, Nigeria do NOT.
        // Deliberate exclusions (innocent bare tokens / Aussie usage): fag (=cigarette),
        // nip, mick, paddy, dink (dinky-di), cracker, slope (skiing), spook, honky, negro.
        internal const string ProfanityReplacement = "I am an idiot and need help!";

        static readonly string[] FullSlurs =
        {
            // n-word family (liberal: single-g, q-substitution, -uh/-let endings)
            "nigger","nigga","niga","niqqa","niqqer","niqa","nikka","nicca","nigguh","niglet",
            // anti-black
            "jigaboo","jiggaboo","porchmonkey","pickaninny","picaninny","golliwog","gollywog",
            "spearchucker","mooncricket","darkie","darky","coon",
            // anti-asian
            "chink","gook","zipperhead","slopehead","chingchong","jap",
            // anti-hispanic
            "wetback","beaner","spic",
            // anti-arab / south-asian / muslim
            "raghead","towelhead","cameljockey","dothead","muzzie","currymuncher","paki",
            // anti-indigenous (AU-relevant)
            "boong","abo","injun","squaw",
            // anti-jewish
            "kike","kyke",
            // roma
            "gyppo","gippo",
            // organised hate
            "kkk","siegheil","seigheil","heilhitler","gasthejews",
        };

        // Distinctive roots that are safe to match as a substring anywhere (no innocent
        // word/place-name forms them, even across word boundaries once spaces are stripped).
        static readonly string[] StrongSlurs =
        {
            "nigger","nigga","niqqa","niqqer","nigguh","niglet",
            "jigaboo","jiggaboo","porchmonkey","pickaninny","picaninny","golliwog","gollywog",
            "spearchucker","mooncricket","chingchong","cameljockey","currymuncher",
            "siegheil","seigheil","heilhitler","gasthejews",
        };

        // Innocent words that embed a strong root as a substring -> never flag these tokens.
        // (Only the n-word collides with a real English word: "snigger" = laugh slyly.)
        static readonly HashSet<string> SlurAllowlist = new HashSet<string>(StringComparer.Ordinal)
        {
            "snigger","sniggers","sniggered","sniggering","sniggeringly","sniggerer","sniggerers",
        };

        // Each root char -> "c+" so repeats (niiigger) and leet-doubled forms still match.
        static string ExpandSlur(string root)
        {
            var sb = new StringBuilder(root.Length * 2);
            foreach (char c in root) sb.Append(c).Append('+');
            return sb.ToString();
        }

        static Regex _tokenRx, _strongRx;
        static Regex TokenRx => _tokenRx ?? (_tokenRx =
            new Regex("^(?:" + string.Join("|", FullSlurs.Select(ExpandSlur)) + ")$", RegexOptions.CultureInvariant));
        static Regex StrongRx => _strongRx ?? (_strongRx =
            new Regex(string.Join("|", StrongSlurs.Select(ExpandSlur)), RegexOptions.CultureInvariant));

        // Collapse to bare lowercase a-z, mapping common leetspeak and a few Cyrillic/
        // accented look-alikes to their latin base and dropping everything else.
        static string NormalizeForSlur(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                char c = char.ToLowerInvariant(ch);
                switch (c)
                {
                    case '0': c = 'o'; break;
                    case '1': case '|': case '!': c = 'i'; break;
                    case '3': c = 'e'; break;
                    case '4': case '@': c = 'a'; break;
                    case '5': case '$': c = 's'; break;
                    case '6': case '9': c = 'g'; break;   // ni66er / ni99er
                    case '7': c = 't'; break;
                    // Cyrillic homoglyphs
                    case 'а': c = 'a'; break; case 'е': case 'ё': c = 'e'; break;
                    case 'о': c = 'o'; break; case 'с': c = 'c'; break;
                    case 'р': c = 'p'; break; case 'у': c = 'y'; break;
                    case 'х': c = 'x'; break; case 'і': c = 'i'; break;
                    // accented latin
                    case 'à': case 'á': case 'â': case 'ä': case 'ã': case 'å': c = 'a'; break;
                    case 'è': case 'é': case 'ê': case 'ë': c = 'e'; break;
                    case 'ì': case 'í': case 'î': case 'ï': c = 'i'; break;
                    case 'ò': case 'ó': case 'ô': case 'ö': case 'õ': c = 'o'; break;
                    case 'ù': case 'ú': case 'û': case 'ü': c = 'u'; break;
                    case 'ñ': c = 'n'; break; case 'ç': c = 'c'; break;
                }
                if (c >= 'a' && c <= 'z') sb.Append(c);
            }
            return sb.ToString();
        }

        // Strip leading/trailing punctuation from a token so "spic!" / "(coon)" still anchor,
        // while interior leet ("sp!c") survives into NormalizeForSlur.
        static string TrimEdges(string s)
        {
            int i = 0, j = s.Length - 1;
            while (i <= j && !char.IsLetterOrDigit(s[i])) i++;
            while (j >= i && !char.IsLetterOrDigit(s[j])) j--;
            return (i > j) ? "" : s.Substring(i, j - i + 1);
        }

        /// <summary>
        /// Remove every character that could break a single chat message into more than one line of
        /// console output, or forge structure inside one. Strips ALL C0 controls (CR, LF, TAB, NUL and
        /// the rest), DEL, the C1 range, and the Unicode line/paragraph separators U+2028 / U+2029 -
        /// U+2028 matters twice over, because it is the separator the plugin's own 'tell' channel uses
        /// to join multi-line private replies.
        /// Applied to chat BEFORE the native handler writes it to console.log. See ChatReformatPatch.
        /// </summary>
        internal static string StripControlChars(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (c < 0x20 || c == 0x7F) continue;              // C0 controls + DEL
                if (c >= 0x80 && c <= 0x9F) continue;             // C1 controls
                if (c == '\u2028' || c == '\u2029') continue;     // LINE / PARAGRAPH SEPARATOR
                sb.Append(c);
            }
            return sb.ToString();
        }

        internal static bool IsRacist(string raw)
        {
            try
            {
                if (ProfanityFilter != null && !ProfanityFilter.Value) return false;
                if (string.IsNullOrWhiteSpace(raw)) return false;
                var sbWhole = new StringBuilder(raw.Length);
                foreach (var tok in raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                {
                    string n = NormalizeForSlur(TrimEdges(tok));
                    if (n.Length == 0) continue;
                    if (SlurAllowlist.Contains(n)) continue;            // innocent word that embeds a slur ("snigger")
                    if (n.Length >= 3 && TokenRx.IsMatch(n)) return true; // standalone slur token (anchored, full list)
                    sbWhole.Append(n);                                   // de-spaced stream (allowlisted words excluded)
                }
                string whole = sbWhole.ToString();
                return whole.Length >= 5 && StrongRx.IsMatch(whole);    // concatenated / spaced-out distinctive slurs
            }
            catch (Exception e) { Log?.LogError("IsRacist: " + e); return false; }
        }
    }

    // Authoritative winner: the winning faction's HQ declares the end. Read the result
    // by name ("Victory"/"Defeat") so we don't need the internal EndType enum.
    // Authoritative winner: the winning faction's HQ declares the end.
    [HarmonyPatch(typeof(FactionHQ), "DeclareEndGame")]
    internal static class DeclareEndGamePatch
    {
        static bool _fired;
        static void Postfix(FactionHQ __instance, object[] __args)
        {
            NukeStatsPlugin.Trace("DeclareEndGamePatch");
            string end = (__args != null && __args.Length > 0 && __args[0] != null) ? __args[0].ToString() : "";
            if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] DeclareEndGame fired: " + end); }
            NukeStatsPlugin.Instance?.OnDeclareEndGame(__instance, end);
        }
    }

    // PvP team-balance: block a player from joining a side that's already too far ahead.
    // Hooks the server-side faction-set handler. 1.1.28 (F2): the hashed UserCode_* target is
    // resolved at PATCH TIME by prefix scan (ResolveUserCode) - never a hardcoded hash again. A
    // future hash churn either re-resolves cleanly or logs a skip of THIS class alone (F1 loop).
    // Only enforced in PvP (both sides joinable); co-op has a preventJoin AI side -> skipped.
    // 1.1.28 (F5): this postfix is also the 'player appeared on a faction' join signal that queues
    // the ranked join announcement (QueueJoinAnnounce; CustomChat-gated, resolution-aware).
    [HarmonyPatch]
    internal static class BlockJoinPatch
    {
        static bool _fired;
        static MethodBase TargetMethod() => NukeStatsPlugin.ResolveUserCode(typeof(Player), "UserCode_CmdSetFaction_");
        // Returning false here does NOT reliably stop the faction assignment (the join still takes,
        // so the old "please join the other team" message did nothing). Instead we ALLOW the join
        // and queue the player; PumpBounces (next HQTick) moves them to spectate if it left the
        // teams too lopsided, and tells them how to join the smaller side.
        static void Postfix(Player __instance)
        {
            NukeStatsPlugin.Trace("BlockJoinPatch");
            if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] CmdSetFaction hooked (team balance + ranked join announce)"); }
            if (__instance == null) return;
            NukeStatsPlugin.QueueBounceCheck(__instance);
            NukeStatsPlugin.QueueJoinAnnounce(__instance);   // player-driven join (CmdSetFaction)
        }
    }

    // Periodic snapshot driver. Our own MonoBehaviour.Update() does not tick on the
    // dedicated server, so we piggy-back the snapshot on FactionHQ.Update -- a method
    // the server calls every frame for each faction during a live mission. The shared
    // Time.time gate in MaybeSnapshot throttles all callers to one snap per interval.
    [HarmonyPatch(typeof(FactionHQ), "Update")]
    internal static class HQTickPatch
    {
        static bool _fired;
        static void Postfix()
        {
            NukeStatsPlugin.Trace("HQTickPatch");
            if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] FactionHQ.Update tick hooked"); }
            NukeStatsPlugin.PeriodicTick();
        }
    }

    // Spawn hook (Player.SetAircraft). 1.1.29: LabelAircraft/_playerAircraftIds are gone;
    // this hook now only drives the
    // over-stacker probation eject. (The plugin never writes the unit label; 1.1.28: clients
    // render owner names locally via OwnerNameResolved.)
    [HarmonyPatch(typeof(Player), "SetAircraft")]
    internal static class AircraftLabelPatch
    {
        static bool _fired;
        static void Postfix(Player __instance)
        {
            NukeStatsPlugin.Trace("SetAircraftPatch");
            if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] SetAircraft hooked (spawn probation check)"); }
            NukeStatsPlugin.Instance?.OnPlayerSpawned(__instance);   // eject over-stackers who spawn anyway
        }
    }

    // Player-vs-player kills: FactionHQ.ReportKillAction(killer, target, factor). We read
    // killer + target here and emit a "kill" event only for human-vs-human enemy kills.
    [HarmonyPatch(typeof(FactionHQ), "ReportKillAction")]
    internal static class KillPatch
    {
        static bool _fired;
        static void Postfix(object[] __args)
        {
            NukeStatsPlugin.Trace("KillPatch");
            if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] ReportKillAction hooked"); }
            if (__args != null && __args.Length >= 2 && __args[0] is Player killer)
                NukeStatsPlugin.OnKill(killer, __args[1]);
        }
    }

    // 1.3.15: while a hidden '!' command is being handled, the native chat handler still runs (so the
    // console line the bot parses is written) but delivers to NOBODY. Only that window is affected.
    [HarmonyPatch(typeof(ChatManager), "TargetReceiveMessage")]
    internal static class HiddenCommandSuppressPatch
    {
        static bool Prefix()
        {
            return !NukeStatsPlugin.SuppressNativeChatSend;
        }
    }

    // Always hide the "pilot rescued/captured" feed line (Tomo: keep noise suppressed).
    [HarmonyPatch(typeof(MessageManager), "RpcPilotCaptureMessage")]
    internal static class PilotMsgSuppressPatch
    {
        static bool _fired;
        static bool Prefix()
        {
            NukeStatsPlugin.Trace("PilotMsgSuppressPatch");
            if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] RpcPilotCaptureMessage hooked (rescue always hidden)"); }
            return false;   // always suppress
        }
    }

    // 1.1.28 (F5): while Chat.CustomChat is ON the native faction-join RPC is SUPPRESSED and
    // replaced by the plugin's ranked, absolute faction-coloured AnnounceJoinFaction line (queued
    // from the BlockJoinPatch join hook, resolution-aware) - no duplicate join lines. CustomChat
    // OFF = vanilla join lines untouched (pure-vanilla lever).
    // (1.1.28 also DELETED the hashed UserCode_RpcPlayerJoinFactionMessage no-op passthrough and
    //  the ColorFromFaction/GetTextColor passthroughs - hashed/no-op targets are pure update risk.)
    [HarmonyPatch(typeof(MessageManager), "RpcPlayerJoinFactionMessage")]
    internal static class JoinFactionMsgPatch
    {
        static bool _fired;
        static bool Prefix(Player player, FactionHQ hq)
        {
            NukeStatsPlugin.Trace("JoinFactionMsgPatch");
            if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] RpcPlayerJoinFactionMessage hooked (suppressed while CustomChat; vanilla otherwise)"); }
            return NukeStatsPlugin.CustomChat == null || !NukeStatsPlugin.CustomChat.Value;
        }
    }

    // Hide the native "repaired / rearmed / refueled" notification (fires when a player services their
    // aircraft at a base). NOTHING in the plugin or the bot emits this line -- it is a native MessageManager
    // RPC, so before this patch NO toggle could suppress it. Mirror the kill/pilot suppress patches.
    // RpcRepairMessage(PersistentID id) confirmed via ilspycmd as the sole repair RPC (message "was repaired").
    [HarmonyPatch(typeof(MessageManager), "RpcRepairMessage")]
    internal static class RepairMsgSuppressPatch
    {
        static bool _fired;
        static bool Prefix()
        {
            NukeStatsPlugin.Trace("RepairMsgSuppressPatch");
            if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] RpcRepairMessage hooked (repair feed suppression)"); }
            // Show only when Hide is explicitly OFF; fail-open (show) if the config is unresolved.
            return NukeStatsPlugin.HideRepairMessages == null || !NukeStatsPlugin.HideRepairMessages.Value;
        }
    }

    // Teamkill detection: every unit death runs ReportKilled; CheckTeamkill flags a friendly kill by a player.
    [HarmonyPatch(typeof(Unit), "ReportKilled")]
    internal static class TeamkillPatch
    {
        static bool _fired;
        static void Prefix(Unit __instance)
        {
            NukeStatsPlugin.Trace("TeamkillPatch");
            if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] ReportKilled hooked (teamkill enforcement)"); }
            NukeStatsPlugin.CheckTeamkill(__instance);
        }
    }

    // MUNITION LAUNCH TRACKING (ported from 0.9.46). damageCredit keys the DAMAGING unit - the aircraft for its own weapons, the CARRIER
    // MISSILE for a submunition - for gun/missile/bomb/
    // shockwave alike - the munition identity exists ONLY at spawn. Spawner.SpawnMissile is [Server]-only and
    // every live missile/bomb passes through it; record (owner unit -> munition name + blastYield + time).
    // blastYield also detects nuke-scale blasts for the long collateral window. Fail-open: any reflection miss
    // leaves the old damaging-unit-name behaviour. SpawnMissile has TWO overloads - patch both explicitly.
    internal static class SpawnMissileRecord
    {
        static System.Reflection.FieldInfo _fiYield;
        static bool _fired, _yieldMissing;

        internal static void Record(Missile result, Unit owner)
        {
            try
            {
                NukeStatsPlugin.Trace("SpawnMissilePatch");
                if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] Spawner.SpawnMissile hooked (munition launch tracking)"); }
                if (result == null || owner == null) return;
                string name = null;
                try { name = result.definition != null ? result.definition.unitName : null; } catch { }
                if (string.IsNullOrEmpty(name)) return;
                float yield = 0f;
                if (!_yieldMissing)
                {
                    if (_fiYield == null)
                    {
                        _fiYield = HarmonyLib.AccessTools.Field(typeof(Missile), "blastYield");
                        if (_fiYield == null) { _yieldMissing = true; NukeStatsPlugin.Log?.LogWarning("[tk] Missile.blastYield not found - nuclear window detection off (weapon names still work)"); }
                    }
                    if (_fiYield != null)
                        try { yield = Convert.ToSingle(_fiYield.GetValue(result)); } catch { }
                }
                // OWNER-CHAIN WALK. A cluster munition is spawned with its PARENT MISSILE as `owner`
                // (SubmunitionDispenser passes `submunitionDispenser.missile`), so the record is keyed to the
                // bomblet's CARRIER. That key is NOT useless: damageCredit keys the DAMAGING unit, which for a
                // submunition IS the carrier missile - so the carrier key is exactly what a cluster kill gets
                // looked up under, and it must keep being written. What was missing is a record under the
                // AIRCRAFT too, for the paths that credit the aircraft. Walk up past any Missile to get that
                // second key; the walk ADDS, it never replaces. Bounded - a corrupt chain must not spin.
                Unit realOwner = owner;
                bool walked = false;
                for (int hop = 0; hop < 4; hop++)
                {
                    Missile carrier = realOwner as Missile;
                    if (carrier == null) break;
                    Unit up = null;
                    try { carrier.ownerID.TryGetUnit(out up); } catch { }
                    if (up == null || up == realOwner) break;
                    realOwner = up; walked = true;
                }
                // A WALKED owner is a SUBMUNITION. Write BOTH keys - this must ADD, never redirect.
                //   * the CARRIER MISSILE key, exactly as the old build did. This is not optional: Spawner sets
                //     the bomblet's ownerID to the carrier, so every bomblet damage path credits
                //     damageCredit[carrierMissile] - that key IS what a cluster kill is looked up under, and
                //     dropping it stopped cluster kills resolving at all.
                //   * the AIRCRAFT key, in its own map, so a cluster kill credited to the aircraft can also
                //     resolve. Separate map because writing it into _lastLaunch would clobber the carrier
                //     weapon's own record, which is the sole holder of that launch's yield.
                // A missile's PersistentID can never equal an aircraft's, so the two writes cannot collide.
                if (walked)
                {
                    NukeStatsPlugin.NoteLaunch(owner.persistentID.Id, name, yield);        // unchanged from HEAD
                    NukeStatsPlugin.NoteSubmunition(realOwner.persistentID.Id, name, yield);   // additive
                }
                else NukeStatsPlugin.NoteLaunch(realOwner.persistentID.Id, name, yield);
            }
            catch { }
        }
    }

    // GUN HIT TRACKING (client-claimed path). A client claims a hit with CmdClaimHit(hitID,
    // relPos, velocity, weaponStationIndex) and the server runs HitOnPhysicsFrame on the SHOOTER.
    // HONESTY NOTE: this Prefix runs on the method's synchronous kickoff, which is BEFORE the
    // awaited HitValidator check inside it - so the record is written on the CLAIM, not on the
    // validated hit. Accepted deliberately: the record is a NAMING AID only (5s window, keyed to
    // this exact victim, consulted only after every launch record has failed), it feeds no damage
    // or punishment maths, and a shooter whose claims are being rejected is not producing kills
    // for it to mislabel. Do not treat this map as proof a hit landed. (audit 13)
    // Fail-open and swallow everything: a naming aid must never interfere with damage.
    // LANDED-EXIT CONFIRMATION. The Abandoned/recovery path never reaches ReportKilled, but its 2s
    // hull-destroy can race the 1 Hz LifeTick - stamping on the game's own declaration means the
    // outcome never depends on tick phase. (1.3.34)
    [HarmonyPatch(typeof(Aircraft), "ReturnToInventory")]
    internal static class ReturnToInventoryPatch
    {
        static void Prefix(Aircraft __instance)
        {
            try { NukeStatsPlugin.NoteAbandonedReturn(__instance); } catch { }
        }
    }

    // SERVER-SIMULATED GUN HITS. Unit.RegisterHit has two branches (Unit.cs:1829-1856): the SERVER
    // branch goes straight to DamageEffects.ArmorPenetrate and never touches weaponStations, so
    // HitOnPhysicsFrame - and therefore GunHitPatch below - never sees it. That is every gun hit the
    // server itself simulates, which on the AI+ server is most of the gunnery, and without this those
    // kills fall back to naming the aeroplane. Writes the same map under the same (shooter,victim)
    // key, so the two hooks are interchangeable: whichever fires, the record is identical, and a
    // double write is a harmless overwrite rather than a conflict.
    [HarmonyPatch(typeof(Unit), "RegisterHit")]
    internal static class RegisterHitPatch
    {
        static bool _fired;

        static void Prefix(Unit __instance, Unit hitUnit, WeaponInfo weaponInfo)
        {
            try
            {
                if (__instance == null || hitUnit == null || weaponInfo == null) return;
                string name = !string.IsNullOrEmpty(weaponInfo.shortName) ? weaponInfo.shortName : weaponInfo.weaponName;
                if (string.IsNullOrEmpty(name)) return;
                if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] Unit.RegisterHit hooked (server-simulated gun hits)"); }
                NukeStatsPlugin.NoteGunHit(__instance.persistentID.Id, hitUnit.persistentID.Id, name);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Unit), "HitOnPhysicsFrame")]
    internal static class GunHitPatch
    {
        static bool _fired;

        static void Prefix(Unit __instance, Unit hitUnit, byte weaponStationIndex)
        {
            try
            {
                if (__instance == null) return;
                var stations = __instance.weaponStations;
                if (stations == null || weaponStationIndex >= stations.Count) return;   // the game's own guard
                var wi = stations[weaponStationIndex].WeaponInfo;
                if (wi == null) return;
                string name = !string.IsNullOrEmpty(wi.shortName) ? wi.shortName : wi.weaponName;
                if (string.IsNullOrEmpty(name)) return;
                if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] Unit.HitOnPhysicsFrame hooked (gun hit tracking)"); }
                if (hitUnit == null) return;
                NukeStatsPlugin.NoteGunHit(__instance.persistentID.Id, hitUnit.persistentID.Id, name);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Spawner), "SpawnMissile",
        typeof(MissileDefinition), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Unit), typeof(Unit))]
    internal static class SpawnMissileDefPatch
    {
        static void Postfix(Missile __result, Unit owner) => SpawnMissileRecord.Record(__result, owner);
    }

    [HarmonyPatch(typeof(Spawner), "SpawnMissile",
        typeof(GameObject), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Unit), typeof(Unit))]
    internal static class SpawnMissileGoPatch
    {
        static void Postfix(Missile __result, Unit owner) => SpawnMissileRecord.Record(__result, owner);
    }

    // ANTI-EXPLOIT: suppress radar/spotting + radar-jamming score entirely.
    // FactionHQ.RewardPlayer is the sole score funnel; its 5th param RewardType distinguishes
    // the reason. RewardType (verified via ilspycmd on Assembly-CSharp.dll):
    //   None=0, Kill=1, Recon=2, Jamming=3, Supply=4, Refuel=5, Repair=6,
    //   RescuePilots=7, CapturePilots=8, CaptureLocation=9
    // Recon (radar/sensor DETECTION) is the score-explosion vector: it fires from
    // RadarLocator_OnRadarWarning / Sensor.DetectTarget on every fresh detection and
    // accumulates fast with many AI aircraft. Jamming is the analogous passive radar reward.
    // Returning false from this Prefix skips the original method body entirely, so NO
    // AddScore / AddAllocation / sortieScore / credit popup happens for these reasons.
    // Kills (1), captures (9), supply/refuel/repair/rescue (4-8) are untouched.
    // NOTE: self-destruct-weapon kills route through RewardType.Kill and are intentionally
    // NOT affected here (separate exploit, monitored only).
    [HarmonyPatch(typeof(FactionHQ), "RewardPlayer")]
    internal static class SuppressSpottingScorePatch
    {
        // consumed by RewardPlayerPatch.Postfix so suppressed rewards don't emit a score event
        [ThreadStatic] internal static bool Suppressed;
        static bool _fired;
        // bind missionType by name (Harmony matches the original's 5th parameter)
        static bool Prefix(Player player, float rewardScore, object missionType)
        {
            NukeStatsPlugin.Trace("RewardPlayerPrefix");
            int mt;
            try { mt = System.Convert.ToInt32(missionType); } catch { return true; }
            if (mt == 2 /*Recon*/ || mt == 3 /*Jamming*/)
            {
                // 2026-07-28: recon/spotting pays VANILLA again. Only a player whose earnings go
                // abnormal is blocked, and only for the rest of that match - see ReconBlocked.
                if (!NukeStatsPlugin.ReconBlocked(player, rewardScore))
                {
                    if (!_fired)
                    {
                        _fired = true;
                        NukeStatsPlugin.Log?.LogInfo("[diag] recon/jamming score is VANILLA (per-player breaker armed; Recon.SuppressAll mutes everyone)");
                    }
                    return true;   // pay it, exactly as the base game would
                }
                Suppressed = true;
                return false;      // blocked player -> no score, no funds, no popup
            }
            return true;
        }
    }

    // Score gains: the central path appears to be FactionHQ.RewardPlayer(player, ...).
    [HarmonyPatch(typeof(FactionHQ), "RewardPlayer")]
    internal static class RewardPlayerPatch
    {
        static bool _fired;
        static void Postfix(object[] __args)
        {
            NukeStatsPlugin.Trace("RewardPlayerPostfix");
            // a Prefix returning false still runs Postfixes; skip telemetry for suppressed spotting/jamming
            if (SuppressSpottingScorePatch.Suppressed) { SuppressSpottingScorePatch.Suppressed = false; return; }
            if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] RewardPlayer fired"); }
            if (__args != null && __args.Length > 0 && __args[0] is Player p) NukeStatsPlugin.EmitOne(p, "score");
        }
    }

    // NOTE: there is deliberately NO Player.AddScore patch. FactionHQ.RewardPlayer is the sole
    // funnel for in-mission score (kills, recon, supply, refuel, captures, repair, rescue all
    // call it, and it calls AddScore), so RewardPlayerPatch already covers every gain. Patching
    // AddScore as well doubled every score event in console.log -- pure noise/CPU, removed in 0.4.0.

    // EJECTION -> bail ledger. The ONLY place the plugin can learn that a pilot left an airframe under
    // their own power, at the moment it happens. Everything downstream (life accounting, the eject
    // announce, the death stamp) used to infer this ~30s later from the absence of a resolved killer,
    // which mislabels every killer-less death. See the _bailedFrom ledger for the full reasoning.
    // Fail-open: a throw here must never block a player's ejection.
    // TWO entry points, because a bail reaches the server by a different route than a server-initiated
    // eject, and the ledger is worthless unless BOTH are covered:
    //
    //   1. Aircraft.StartEjectionSequence - only executes the sequence when IsServer. That is the path
    //      the PLUGIN itself uses (AdminEject, the ailimit fallback). On the owning CLIENT it instead
    //      takes the HasAuthority branch and sends CmdStartEjectionSequence, so for a real pilot bail
    //      this method never runs on a dedicated server at all.
    //   2. UserCode_CmdStartEjectionSequence_<hash> - the weaver-generated ServerRpc body the server
    //      actually runs for a player-initiated bail. It calls EjectionSequence() directly and never
    //      re-enters StartEjectionSequence.
    //
    // Patching only (1) left the ledger permanently EMPTY for every real bail, which - combined with
    // the now-unconditional death stamp - made EndLife("eject")
    // unreachable for everyone. The hashed name is resolved by the same prefix scan the chat
    // patch uses, so a game update that rehashes it degrades to "not patched" rather than crashing.
    [HarmonyPatch(typeof(Aircraft), "StartEjectionSequence")]
    internal static class EjectLedgerPatch
    {
        static void Postfix(Aircraft __instance) => NukeStatsPlugin.NoteBailFrom(__instance);
    }

    [HarmonyPatch]
    internal static class EjectLedgerCmdPatch
    {
        static bool Prepare()
        {
            var m = NukeStatsPlugin.ResolveUserCode(typeof(Aircraft), "UserCode_CmdStartEjectionSequence_");
            if (m == null)
            {
                // Say so LOUDLY. Prepare() returning false makes Harmony skip this class silently, and
                // the boot log would still read as a clean patch set - while the bail ledger stays
                // empty for every real bail.
                // If a game update rehashes this method, that must be visible, not inferred later.
                NukeStatsPlugin.Log?.LogError(
                    "[bail] UserCode_CmdStartEjectionSequence_* NOT FOUND - the game update likely rehashed it. "
                    + "Player bails will not be recorded, so eject lines and eject life-ends are DISABLED. "
                    + "Re-resolve this method name.");
            }
            return m != null;
        }
        static MethodBase TargetMethod() => NukeStatsPlugin.ResolveUserCode(typeof(Aircraft), "UserCode_CmdStartEjectionSequence_");
        static void Postfix(Aircraft __instance) => NukeStatsPlugin.NoteBailFrom(__instance);
    }

    // Chat send: profanity gate + admin/public !commands + the 1.1.28 rank-in-chat reroute.
    // The hashed UserCode target is resolved at patch time by prefix scan (F2). While
    // Chat.CustomChat is ON, FormatAndBroadcast composes+delivers the ranked line itself and
    // returns true -> this prefix returns false -> the native relay is suppressed. CustomChat OFF
    // or ANY reroute failure -> FormatAndBroadcast returns false -> pure native chat (fail-open).
    [HarmonyPatch]
    internal static class ChatReformatPatch
    {
        static MethodBase TargetMethod() => NukeStatsPlugin.ResolveUserCode(typeof(ChatManager), "UserCode_CmdSendChatMessage_");
        static bool Prefix(ChatManager __instance, ref string __0, bool __1, INetworkPlayer __2)
        {
            NukeStatsPlugin.Trace("ChatReformatPatch");
            try
            {
                // ===== SECURITY, 1.3.20 - DO NOT REMOVE OR MOVE BELOW THIS POINT =====
                // A chat message must never be able to become TWO console lines.
                //
                // The bot's telemetry gate blanks any console line that looks like chat, so a player
                // cannot type a "[NOSTATS] {...}" frame and have it parsed. But that gate assumes one
                // message = one line. Nothing stripped CR/LF from the message, and the bot's console
                // readers split strictly on '\n' - so a message containing a newline produced a SECOND
                // line which was NOT chat-shaped, sailed past the gate, and was fed to every frame
                // parser as though this plugin had emitted it.
                //
                // That gave any modified client arbitrary telemetry: an "award" frame writing points
                // into ranks.json permanently, a "win" frame forging the match result and W/L tally, a
                // "tk" frame fabricating a ban record against any SteamID, an "end" frame wiping
                // mid-match state. Since 1.3.15 hides '!' lines from public chat, it left no trace in
                // game. Strip control characters HERE, before the native handler - which is what writes
                // console.log - ever sees the string.
                __0 = NukeStatsPlugin.StripControlChars(__0);

                if (NukeStatsPlugin.IsRacist(__0))
                    __0 = NukeStatsPlugin.ProfanityReplacement;

                string message = __0; bool allChat = __1; INetworkPlayer sender = __2;
                Player player = null;
                bool got = sender != null && sender.TryGetPlayer<Player>(out player) && player != null;
                if (!got || string.IsNullOrWhiteSpace(message)) return true;
                if (NukeStatsPlugin.Instance.TryHandleChatCommand(__instance, player, message)) return false;
                // 1.3.15: a '!' command that is not a vote is hidden from public chat. We deliberately
                // let the native handler RUN - it writes the console line the bot parses commands from -
                // and instead drop the per-recipient delivery while this flag is up.
                if (NukeStatsPlugin.IsHiddenCommand(message)) NukeStatsPlugin.SuppressNativeChatSend = true;
                // Rank-in-chat reroute: true = plugin delivered the ranked line (suppress native);
                // false = native chat runs untouched.
                return !NukeStatsPlugin.Instance.FormatAndBroadcast(__instance, player, message, allChat);
            }
            catch (Exception e) { try { NukeStatsPlugin.Log?.LogError("chat Prefix threw: " + e); } catch { } return true; }
        }

        // Runs even if the game throws. A stuck flag would mute chat for everyone, so this is the one
        // thing here that must never be skipped.
        static void Finalizer()
        {
            NukeStatsPlugin.SuppressNativeChatSend = false;
        }
    }

    // RANK FLOOR FIX. The game seeds a player's mission starting rank only when
    // !saveData.Rejoined; a reconnecting player (Rejoined=true) keeps their SAVED rank, which
    // is 0 if their old connection was saved before they ever ranked (e.g. dropped in faction
    // select). That stranded rejoiners at rank 0 on missions whose playerStartingRank is 2/3.
    // Fix: after ServerMissionStartPlayer runs, ensure the player is at LEAST the mission's
    // starting rank. No-op for everyone already at/above it (so legit higher ranks are kept).
    [HarmonyPatch(typeof(NetworkManagerNuclearOption), "ServerMissionStartPlayer")]
    internal static class StartingRankFloorPatch
    {
        static bool _fired;
        static object _lastMission;
        static void Postfix(Mission __0, Player __1)
        {
            NukeStatsPlugin.Trace("RankFloorPatch");
            if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] ServerMissionStartPlayer hooked (rank floor)"); }
            try
            {
                if (!ReferenceEquals(_lastMission, __0)) { _lastMission = __0; NukeStatsPlugin.AdvanceGame(); NukeStatsPlugin.ClearMatchTeamkills(); NukeStatsPlugin.ResetReconMeters(); NukeStatsPlugin.ClearForfeitVotes(); NukeStatsPlugin.ResetCatchup(); NukeStatsPlugin.ResetRankFunds(); }  // new game -> advance balance move-exemption + reset teamkill + forfeit + catch-up + rank funds (1.1.29: PvP-suppression id set removed)
                if (__0 == null || __1 == null || __0.missionSettings == null) return;
                int baseWant = __0.missionSettings.playerStartingRank;
                // PvP matches (Escalation/Terminal): floor EVERY player to PvpStartingRank, on top of the
                // mission's own value (covers the built-in PvP maps we can't edit). Co-op is unaffected.
                int pvp = NukeStatsPlugin.PvpStartingRank != null ? NukeStatsPlugin.PvpStartingRank.Value : 0;
                if (pvp > baseWant && NukeStatsPlugin.IsPvpMission(__0)) baseWant = pvp;
                int want = NukeStatsPlugin.CatchupFloor(baseWant);   // latecomer spawns at risen catch-up floor
                int was = __1.PlayerRank;
                if (was < want)
                {
                    __1.SetRank(want, setScoreOffset: true);
                    NukeStatsPlugin.Log?.LogInfo($"[rankfloor] {NukeStatsPlugin.RawNameOf(__1)} {was} -> {want} (mission/PvP starting-rank floor)");
                }
                // Join/start floor (baseWant) never pays (match start: CatchupBonus==0 => want==baseWant).
                // Mid-match late-join catch-up (want > baseWant) MUST pay (new-old)*RankFundsPerRank —
                // e.g. base 2 to CatchupFloor 5 = 3 rank-ups * 30 = 90. Pay even when already at want
                // (reconnect after unpaid late-join on older builds): CatchupTick skips PlayerRank>=floor,
                // and 1.1.1 only paid inside the SetRank branch so reconnect-at-floor still got 0.
                // GrantRankFundsForLift is monotonic via _rankFunded (no double-pay if CatchupTick already paid).
                if (want > baseWant)
                {
                    string mode = NukeStatsPlugin.FundsMode();
                    if (mode == "catchup_raised" || mode == "catchup_all")
                    {
                        int payFrom = (was > baseWant && was < want) ? was : baseWant;
                        NukeStatsPlugin.GrantRankFundsForLift(__1, payFrom, want, "late-join catch-up");
                    }
                }
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("StartingRankFloor: " + e); }
        }
    }

    // (1.1.28: NameInjectPatch is DELETED, not migrated - the game update removed CmdSetPlayerName
    //  outright; no name crosses the wire any more, so there is no injection point and none is
    //  needed. Rank now lives in plugin-composed strings - see FormatAndBroadcast / AnnounceJoinFaction.)

    // COMMAND POLICY + ORDER TELEMETRY (was: flood guard A). This is the only hook on
    // UnitCommand.CmdSetDestination. Since 1.2.4 it does exactly two things, NEITHER of which is a rate
    // limit: it enforces the Command.Policy rule about WHICH units a player may order (AllowCommandTarget),
    // and it counts the order for telemetry (NoteOrderAttempt -> the net frame's "ord" and "streak" fields).
    // WHY THERE IS NO LONGER A PER-SENDER CAP HERE: the game registers CmdSetDestination with
    // RpcRateLimitConfig.Enabled(1f, 5, 20, 1) and Mirage keys that bucket on RpcId(declaring TYPE, index)
    // inside a PER-PLAYER dictionary -- i.e. vanilla ALREADY caps each player at ~5 accepted/s (burst 20)
    // across ALL their units, in HandleRpc, before this prefix runs. (1.1.x source claimed the game's
    // limiter was per-UNIT; that was WRONG, and is probably why the live cap was once raised to 5 to
    // "match" it.) Layer A was a second, tighter cap on top of that: redundant, measured clicks rather
    // than RPCs on the wire, and it false-kicked an honest player, so it was removed in 1.2.4 on the
    // owner's instruction. What vanilla does NOT cover is an RPC to a DEAD netId, which exits Mirage's
    // HandleRpc at the identity lookup before the rate-limit check ever runs -- guard B owns that path
    // and is untouched.
    // 1.1.28 (F2): the hashed UserCode target is resolved by prefix scan at patch time.
    // UserCode_CmdSetDestination_*(GlobalPosition waypoint, INetworkPlayer sender) => waypoint is Harmony
    // __0 and sender is __1. __0 is now unused but kept in the signature (Harmony tolerates it) rather
    // than churning a live patch signature.
    [HarmonyPatch]
    internal static class FleetOrderFloodPatch
    {
        static bool _fired;
        static MethodBase TargetMethod() => NukeStatsPlugin.ResolveUserCode(typeof(UnitCommand), "UserCode_CmdSetDestination_");
        static bool Prefix(UnitCommand __instance, GlobalPosition __0, INetworkPlayer __1)
        {
            NukeStatsPlugin.Trace("FleetOrderFloodPatch");
            if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] CmdSetDestination hooked (command policy + order telemetry)"); }
            try
            {
                if (__1 == null || !__1.TryGetPlayer<Player>(out Player player) || player == null) return true;
                if (!NukeStatsPlugin.AllowCommandTarget(__instance, player))                  // gameplay rule: target not allowed
                { NukeStatsPlugin.NoteOrderAttempt(player); return false; }                   // still counts as an attempt (per-unit rule)
                // 1.2.4: no rate limit here any more (layer A removed — vanilla caps this at ~5/s per player).
                // Count the order for telemetry and let it through. Now once per unit-RPC, not once per click.
                NukeStatsPlugin.NoteOrderAttempt(player);
                return true;
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("FleetOrderFlood: " + e); return true; }
        }
    }

    // FLOOD GUARD B: silently drop a ServerRpc whose target netId has no live object. The game already
    // drops these (return false) but first LOGS + pushes a client error + builds a network reader; under
    // a flood (a client re-firing at a just-destroyed unit) that storm exhausts the ByteBuffer pool and
    // overflows send buffers. We short-circuit with the SAME result, minus the amplifier. RPCs to a dead
    // netId NEVER reach the per-unit handler (they exit HandleRpc before the rate-limit check and before
    // dispatch), so the game's own RpcRateLimitConfig cannot see them -- and since 1.2.4 there is no plugin
    // rate limiter on that path either. This is the only
    // place to catch that path. Applied MANUALLY from Awake (RpcHandler is internal / HandleRpc private).
    // HandleRpc(player, netId, ...) => sender is Harmony __0, netId is __1; returns bool.
    //
    // 1.2.0 -- guard B no longer treats every unknown netId the same. Mirage's own RpcHandler keeps a 5s
    // RecentlyDestroyed list (DESTROY_GRACE_PERIOD) precisely to separate "the unit died while my order was
    // in flight" (innocent, warn-level in vanilla) from "this id was never here" (error-level, SetError(1)).
    // Our prefix used to short-circuit BEFORE that distinction, so neither the plugin nor the game penalised
    // the order-a-dead-unit exploit at all -- and on a server with DedicatedServerConfig.DisableErrorKick=true
    // the game's SetError is a no-op anyway. We now read that same list: on the list => silent drop as before;
    // not on it => a strike toward NoteStaleNetIdRpc's kick. We also run Mirage's own RecentlyDestroyed.CleanUp
    // for it, because our short-circuit is what stops the game's HandleRpc from ever calling it (the list would
    // otherwise grow for the life of the process). Every step is fail-open: if the list cannot be bound we
    // behave exactly as 1.1.x did (silent drop, no strikes).
    internal static class DeadNetIdDropPatch
    {
        delegate bool TryGetIdDel(uint netId, out NetworkIdentity identity);
        delegate bool WasRecentDel(uint netId, out double destroyTime);
        static object _boundHandler;
        static TryGetIdDel _tryGetId;
        static WasRecentDel _wasRecent;
        static Action<double> _cleanUp;
        static bool _fired, _recentFired, _recentWarned;
        const double DESTROY_GRACE_PERIOD = 5.0;   // mirrors Mirage.RemoteCalls.RpcHandler.DESTROY_GRACE_PERIOD

        static bool Prefix(object __instance, object __0, uint __1, ref bool __result)
        {
            NukeStatsPlugin.Trace("HandleRpcPatch");
            // GUARD E source-attribution: record which connection's ServerRpc is being dispatched, so a send-buffer
            // overflow that fires while broadcasting it is blamed on THIS source. Postfix clears it after dispatch, so
            // an overflow OUTSIDE an RPC (e.g. an AI-tick broadcast) blames no one -> absorb-only, never a false kick.
            NukeStatsPlugin.SetRpcSource(__0);
            // ROOT-CAUSE flood guard D: general per-connection inbound RPC rate limit (ALL rpc types). Runs FIRST so it
            // also catches RPCs to LIVE netIds (guard B below only handles already-dead ones). Drops the excess with the
            // game's own safe result; DropInboundRpc fails open internally (never throws).
            if (NukeStatsPlugin.DropInboundRpc(__0)) { __result = false; return false; }
            try
            {
                var cfg = NukeStatsPlugin.FloodDropDeadNet;
                if (cfg == null || !cfg.Value || __instance == null) return true;
                if (!ReferenceEquals(__instance, _boundHandler))     // (re)bind once per RpcHandler instance
                {
                    _boundHandler = __instance; _tryGetId = null; _wasRecent = null; _cleanUp = null;
                    var loc = AccessTools.Field(__instance.GetType(), "_objectLocator")?.GetValue(__instance);
                    if (loc != null)
                    {
                        var mi = AccessTools.Method(typeof(IObjectLocator), "TryGetIdentity");
                        if (mi != null) _tryGetId = (TryGetIdDel)Delegate.CreateDelegate(typeof(TryGetIdDel), loc, mi);
                    }
                    // Mirage's own just-destroyed list (public field on the internal RpcHandler). Reflective +
                    // fully optional: without it we simply keep 1.1.x behaviour (drop everything silently).
                    try
                    {
                        var rd = NukeStatsPlugin.ReflectGet(__instance, "RecentlyDestroyed");
                        if (rd != null)
                        {
                            var wm = AccessTools.Method(rd.GetType(), "WasRecentlyDestroyed");
                            if (wm != null) _wasRecent = (WasRecentDel)Delegate.CreateDelegate(typeof(WasRecentDel), rd, wm);
                            var cm = AccessTools.Method(rd.GetType(), "CleanUp");
                            if (cm != null) _cleanUp = (Action<double>)Delegate.CreateDelegate(typeof(Action<double>), rd, cm);
                        }
                    }
                    catch { _wasRecent = null; _cleanUp = null; }
                    if (_wasRecent == null && !_recentWarned)
                    {
                        _recentWarned = true;
                        NukeStatsPlugin.Log?.LogWarning("[diag] Mirage RecentlyDestroyed not reachable -> dead-netId RPCs are "
                            + "dropped silently as before, but the stale-netId exploit strike CANNOT be applied (fail-open)");
                    }
                }
                var del = _tryGetId;
                if (del == null) return true;                        // couldn't bind -> let the game handle it
                if (!del(__1, out _))                                 // no live object for this netId
                {
                    if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] HandleRpc dead-netId drop ACTIVE (flood guard B)"); }
                    NukeStatsPlugin.NoteDeadNetIdDrop();
                    // We short-circuit the game's HandleRpc, so nothing else ever prunes this list -> do it here.
                    // CleanUp self-throttles to once per 10s internally, so calling it per drop is cheap.
                    try { _cleanUp?.Invoke(DESTROY_GRACE_PERIOD); } catch { }
                    try
                    {
                        var recent = _wasRecent;
                        if (recent != null)
                        {
                            bool justDied = recent(__1, out double destroyedAt)
                                && destroyedAt + DESTROY_GRACE_PERIOD > Time.unscaledTimeAsDouble;
                            if (!justDied)
                            {
                                if (!_recentFired) { _recentFired = true; NukeStatsPlugin.Log?.LogInfo("[diag] stale-netId exploit strikes ACTIVE (flood guard B part 2)"); }
                                NukeStatsPlugin.NoteStaleNetIdRpc(__0, __1);   // never throws; may queue a kick
                            }
                        }
                    }
                    catch (Exception e) { NukeStatsPlugin.Log?.LogError("StaleNetIdCheck: " + e); }   // cosmetic path: must never change the drop below
                    __result = false;                                // match the game's own drop result
                    return false;                                    // skip body: no log, no SetError, no reader/pool churn
                }
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("DeadNetIdDrop: " + e); }
            return true;
        }

        // guard E: clear the RPC source after dispatch, so a send-buffer overflow that fires OUTSIDE an RPC context
        // (e.g. an AI-tick broadcast) is attributed to no one -> absorbed but never blamed/kicked. Harmony runs the
        // postfix even when Prefix returned false (a dropped RPC broadcast nothing), which is exactly what we want.
        static void Postfix() { NukeStatsPlugin.Trace("HandleRpcPostfix"); NukeStatsPlugin.ClearRpcSource(); }
    }

    // FLOOD GUARD E (the real mass-DC fix): veto Mirage's buffer-full disconnect. When a client's reliable send buffer
    // overflows, Mirage.NetworkPlayer.Send catches BufferFullException and calls Disconnect((DisconnectReason)5) -- and a
    // flood overflows EVERY client's buffer at once, so the game itself disconnects the whole lobby (not our guards).
    // DisconnectReason 5 is used ONLY for this (verified by decompiling Mirage.dll: exactly two call sites, both the
    // buffer-full catch). We prefix Disconnect(reason) and ALWAYS VETO reason 5 -> the overflow is ABSORBED (the
    // un-queueable packet is dropped for that one connection), the player STAYS. No grace valve: a genuinely-dead client
    // is still dropped by Mirage's own Timeout (reason 1, un-vetoed). COMMAND-AGNOSTIC. A SUSTAINED flood causes desync
    // (dropped reliable packets) until the source stops or is kicked, but nobody is mass-kicked. Applied MANUALLY from
    // Awake (reflective lookup, no hard Mirage type dependency). Fail-open: any error -> allow the disconnect (normal).
    internal static class OverflowDisconnectVetoPatch
    {
        static bool _fired;

        // return false = SKIP the original Disconnect (veto). true = let it run. __instance = the NetworkPlayer whose
        // send buffer overflowed (the victim); __0 = DisconnectReason (boxed as object). ALWAYS vetoes reason 5; a
        // genuinely-dead client is still dropped by Mirage's own Timeout (reason 1), which we don't touch -> no zombies,
        // no grace valve needed (the old grace valve re-enabled the mass-DC after N seconds -- removed).
        static bool Prefix(object __instance, object __0)
        {
            NukeStatsPlugin.Trace("OverflowVetoPatch");
            try
            {
                var cfg = NukeStatsPlugin.OverflowAbsorb;
                if (cfg == null || !cfg.Value) return true;                          // feature off -> normal game behavior
                int reason;
                try { reason = System.Convert.ToInt32(__0); } catch { return true; }
                if (reason != 5) return true;                                        // ONLY the send-buffer-full disconnect; normal leaves/kicks/TIMEOUTS pass through untouched
                if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] send-buffer overflow ABSORB ACTIVE (flood guard E): buffer-full disconnects vetoed; dead clients still drop via Mirage Timeout"); }
                NukeStatsPlugin.NoteOverflowAbsorbed(__instance);                    // blame the current RPC source (main-thread-gated); kick it if it floods many distinct victims
                return false;                                                         // ABSORB: skip the disconnect, keep the player connected
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("OverflowVeto: " + e); return true; }   // fail to default game behavior (never break disconnects)
        }
    }

    // FLOOD GUARD C: raise the per-connection reliable-send-buffer cap on the Mirage.SocketLayer.Config that
    // NetworkManagerNuclearOption.ConfigureNetwork just built + assigned to Server.PeerConfig (a reference type,
    // so the mutation sticks for the Peer/AckSystem built right after). The game caps it at 3000; a busy server's
    // transient fleet-order/RPC burst overflows that -> BufferFullException -> the whole lobby drops. We raise it
    // (default 12000 = 4x) so the burst drains instead of overflowing, and read the field back to PROVE the new
    // value. Field-or-property + reflection-only (no hard SocketLayer ref). Never LOWERS it. Fail-open everywhere.
    // Applied MANUALLY from Awake (private target method; reflective field set).
    internal static class MirageBufferRaisePatch
    {
        const string Member = "MaxReliablePacketsInSendBufferPerConnection";
        static bool _fired;

        // 1.1.30: delegate to the shared FIELD-first cached getter (the game update made
        // Server/the buffer member field-only; property-first probing warn-spammed the log).
        static object GetMember(object o, string name) => NukeStatsPlugin.ReflectGet(o, name);

        static void Postfix(object __instance)   // __instance = NetworkManagerNuclearOption
        {
            NukeStatsPlugin.Trace("MirageBufferRaisePatch");
            try
            {
                var flag = NukeStatsPlugin.MirageRaiseSendBuffer;
                if (flag == null || !flag.Value || __instance == null) return;

                var server = GetMember(__instance, "Server");          // Mirage.NetworkServer (libs Mirage.dll)
                if (server == null) { NukeStatsPlugin.Log?.LogWarning("[flood] Layer C: Server null, skipped"); return; }
                var peerCfg = GetMember(server, "PeerConfig");          // Mirage.SocketLayer.Config (reference type)
                if (peerCfg == null) { NukeStatsPlugin.Log?.LogWarning("[flood] Layer C: PeerConfig null, skipped"); return; }

                var t = peerCfg.GetType();
                var fld = AccessTools.Field(t, Member);
                var prop = fld == null ? AccessTools.Property(t, Member) : null;
                if (fld == null && prop == null) { NukeStatsPlugin.Log?.LogWarning("[flood] Layer C: " + Member + " not found, skipped"); return; }

                int target = NukeStatsPlugin.MirageSendBufferLimit != null ? NukeStatsPlugin.MirageSendBufferLimit.Value : 12000;
                if (target < 3000) target = 3000;   // never go BELOW the game default

                int before = 0;
                try { before = System.Convert.ToInt32(fld != null ? fld.GetValue(peerCfg) : prop.GetValue(peerCfg)); } catch { }
                if (target <= before)
                {
                    if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo($"[diag] Layer C: {Member} already {before} >= target {target}, left as-is"); }
                    return;
                }
                if (fld != null) fld.SetValue(peerCfg, target); else prop.SetValue(peerCfg, target);
                int after = 0;
                try { after = System.Convert.ToInt32(fld != null ? fld.GetValue(peerCfg) : prop.GetValue(peerCfg)); } catch { }

                if (!_fired || after != target)
                {
                    _fired = true;
                    NukeStatsPlugin.Log?.LogInfo($"[diag] Layer C ACTIVE: {Member} {before} -> {after} (target {target}, {(double)target / 3000.0:0.#}x default)");
                }
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("MirageBufferRaise: " + e); }
        }
    }

    // NET-HEALTH: capture the DisconnectReason for each forced drop. Mirage's PUBLIC Disconnected event
    // gives us the player but NOT the reason; the reason only exists on the PRIVATE
    // NetworkServer.Peer_OnDisconnected(IConnection, DisconnectReason) callback. We postfix it (read-only)
    // to tally per-SteamID forced-DC count + last reason for the {"t":"net"} telemetry line. Applied MANUALLY
    // from Awake (private target); fail-open -- if the method can't be resolved at load, the patch is simply
    // never installed (net telemetry still emits, lastDc just stays empty). Never throws into the netcode.
    internal static class DcReasonPatch
    {
        static bool _fired;
        static void Postfix(object __0, object __1)   // __0 = IConnection, __1 = DisconnectReason (kept loose to avoid a hard ref)
        {
            NukeStatsPlugin.Trace("DcReasonPatch");
            try
            {
                if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] Peer_OnDisconnected postfix ACTIVE (net-health DisconnectReason capture)"); }
                if (__0 == null) return;
                string reason = __1 != null ? __1.ToString() : "";
                string sid = NukeStatsPlugin.SidForConnection(__0);
                if (string.IsNullOrEmpty(sid)) return;     // couldn't map this IConnection to a current player
                NukeStatsPlugin.NoteForcedDc(sid, reason);
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("DcReason: " + e); }
        }
    }

    // GUARD F part 1 (1.2.1): lift the error-kick rejoin lockout. Root cause of the 2026-07-28 owner
    // lockout: the game's PlayerErrorFlags/ErrorKick machinery (fed since the 07-27 update by the new
    // InvalidTransformSnapshot / under-terrain client checks - e.g. a client still streaming snapshots
    // for its just-destroyed aircraft) calls TimeoutManager.OnKickFromError, which creates a ~300s
    // timeout entry. While that entry lives, SteamNetAcceptCallback SILENTLY refuses every reconnect
    // (and HasTimeout adds +10s per attempt), the client renders each refusal as "Local client
    // stopped", and repeated error-kicks escalate to the game's persistent "Error Auto Ban". This
    // postfix runs AFTER the game applied the timeout and BEFORE ErrorRateLimitReached issues its
    // (plain, kick-list-free) player.Disconnect() - the exact right moment to clear the lockout and
    // roll the ban ladder back. Ban-path results (__result true: instant-ban flags / repeated kicks)
    // are reported loudly but NEVER vetoed. Loose object params = no hard Steamworks/game-enum refs;
    // applied MANUALLY from Awake; fail-open everywhere (any error -> native behavior, evidence only).
    internal static class ErrorKickLiftPatch
    {
        static bool _fired;

        // OnKickFromError(CSteamID steamId, NuclearOptionPlayerErrorFlags.Names errorFlag) : bool (true = ban path)
        static void Postfix(object __instance, object __0, object __1, bool __result)
        {
            NukeStatsPlugin.Trace("ErrorKickLiftPatch");
            try
            {
                if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] guard F ACTIVE: error-kick evidence + rejoin-lockout lift"); }
                NukeStatsPlugin.NoteErrorKick(__instance, __0, __1, __result);
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("ErrorKickLift: " + e); }
        }
    }

    // GUARD F part 2 (1.2.1, READ-ONLY): make the silent join refusal visible. While a TimeoutManager
    // lockout is active the server refuses the connection at the Steam accept callback with NO message
    // to anyone - the player just sees "Local client stopped" and nothing lands in any log the bot
    // tails. Postfix on TimeoutManager.HasTimeout: whenever it returns true (join refused), report it
    // ([errkick] line + {"t":"joinblock"} frame, rate-limited per sid). Changes NOTHING about the
    // decision. Applied MANUALLY from Awake; fail-open.
    internal static class JoinTimeoutBlockPatch
    {
        static bool _fired;

        // HasTimeout(CSteamID steamId) : bool (true = join refused)
        static void Postfix(object __instance, object __0, bool __result)
        {
            NukeStatsPlugin.Trace("JoinTimeoutBlockPatch");
            try
            {
                if (!__result) return;                                    // join allowed -> nothing to report
                if (!_fired) { _fired = true; NukeStatsPlugin.Log?.LogInfo("[diag] guard F join-refusal evidence ACTIVE"); }
                NukeStatsPlugin.NoteJoinBlocked(__instance, __0);
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("JoinTimeoutBlock: " + e); }
        }
    }

}
