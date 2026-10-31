using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ServerTester.Engine
{
    // Everything the tester keeps lives in a "data" folder next to the program, so the whole thing stays portable:
    //   data/steamcmd   Valve's SteamCMD, downloaded on first use
    //   data/server     the private test server (Steam app 3930080) with BepInEx and the test build of NukeStats
    //   data/results    one folder per test run
    // The test server is the tester's own copy. It never touches a server you already run.
    public static class ServerSetup
    {
        public const int AppId = 3930080;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static string AppDir => Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory);
        public static string DataDir => Path.Combine(AppDir, "data");
        public static string SteamCmdDir => Path.Combine(DataDir, "steamcmd");
        public static string ServerDir => Path.Combine(DataDir, "server");
        public static string ResultsDir => Path.Combine(DataDir, "results");
        public static string ServerExe => Path.Combine(ServerDir, Native.IsWindows ? "NuclearOptionServer.exe" : "NuclearOptionServer.x86_64");
        public static string ConfigPath => Path.Combine(ServerDir, "DedicatedServerConfig.json");
        public static string LogPath => Path.Combine(ServerDir, "logs", "console.log");
        public static string PluginCfgPath => Path.Combine(ServerDir, "BepInEx", "config", "anz.nukestats.cfg");
        public static string LauncherLogPath => Path.Combine(ServerDir, "logs", "launcher-output.log");   // what the start script and the server print

        // where to look when the test server stopped on its own
        public static string LogNote() => File.Exists(LogPath) ? "(see server-console.log in the results folder)"
            : "(before writing its own log: see server-output.log in the results folder)";

        public static bool ServerInstalled => File.Exists(ServerExe);
        public static bool ModsInstalled => File.Exists(Path.Combine(ServerDir, "BepInEx", "plugins", "NukeStats.dll"))
                                            && File.Exists(Path.Combine(ServerDir, "missions", "Stress Test", "Stress Test.json"))
                                            && File.Exists(Path.Combine(ServerDir, "missions", "Realistic Benchmark", "Realistic Benchmark.json"))
                                            && File.Exists(Path.Combine(ServerDir, Native.IsWindows ? "winhttp.dll" : "run_bepinex.sh"));

        public static string ServerBuild()
        {
            try
            {
                string acf = Path.Combine(ServerDir, "steamapps", "appmanifest_" + AppId + ".acf");
                var m = Regex.Match(File.ReadAllText(acf), "\"buildid\"\\s+\"(\\d+)\"");
                return m.Success ? m.Groups[1].Value : "";
            }
            catch { return ""; }
        }

        // ---------------------------------------------------------------- SteamCMD and the server
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        static async Task<string> EnsureSteamCmd(Action<string> log, CancellationToken cancel)
        {
            string exe = Path.Combine(SteamCmdDir, Native.IsWindows ? "steamcmd.exe" : "steamcmd.sh");
            if (File.Exists(exe)) return exe;
            Directory.CreateDirectory(SteamCmdDir);
            string url = Native.IsWindows
                ? "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip"
                : "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";
            log("Downloading SteamCMD from Valve (" + url + ")");
            byte[] data = await Http.GetByteArrayAsync(url, cancel);
            log("SteamCMD " + (data.Length / 1024) + " KB, sha256 " + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant().Substring(0, 16));
            if (Native.IsWindows)
                using (var z = new ZipArchive(new MemoryStream(data))) z.ExtractToDirectory(SteamCmdDir, true);
            else
            {
                using (var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress))
                    TarFile.ExtractToDirectory(gz, SteamCmdDir, true);
                Chmod(exe);
                foreach (var f in Directory.GetFiles(SteamCmdDir, "*", SearchOption.AllDirectories)) if (!f.EndsWith(".so")) Chmod(f);
            }
            return exe;
        }

        // SteamCMD prints "Update state (0x61) downloading, progress: 42.17 (...)"
        static readonly Regex Progress = new Regex(@"progress:\s*([0-9.]+)", RegexOptions.Compiled);

        public static async Task<bool> InstallOrUpdateServer(Action<string> log, Action<double> progress, CancellationToken cancel)
        {
            if (Native.IsLinux && !new[] { "/lib/ld-linux.so.2", "/lib32/ld-linux.so.2", "/usr/lib32/ld-linux.so.2", "/usr/lib/i386-linux-gnu/ld-linux.so.2" }.Any(File.Exists))
            {
                log("SteamCMD needs the 32-bit system libraries, which this system doesn't have. Install them, then press INSTALL again:");
                log("  Debian/Ubuntu: sudo apt install lib32gcc-s1    Fedora: sudo dnf install glibc.i686 libstdc++.i686    Arch: sudo pacman -S lib32-gcc-libs");
                return false;
            }
            string steamcmd = await EnsureSteamCmd(log, cancel);
            Directory.CreateDirectory(ServerDir);
            log("Installing the Nuclear Option dedicated server (Steam app " + AppId + ", about 1 GB) into " + ServerDir);
            // the first time, SteamCMD often updates itself and quits without installing anything: run it a second time
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                await RunSteamCmd(steamcmd, log, progress, cancel);
                if (cancel.IsCancellationRequested) return false;
                if (ServerInstalled) break;
                if (attempt == 1) log("SteamCMD finished without the server (it updates itself the first time): running it again.");
            }
            if (!ServerInstalled) { log("SteamCMD finished without the server; the lines above say why."); return false; }
            if (!Native.IsWindows) Chmod(ServerExe);
            log("Server installed, build " + ServerBuild());
            return true;
        }

        static async Task RunSteamCmd(string steamcmd, Action<string> log, Action<double> progress, CancellationToken cancel)
        {
            var psi = new ProcessStartInfo(steamcmd)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                WorkingDirectory = SteamCmdDir,
            };
            // SteamCMD keeps its files under the home folder on Linux: give it its own, so nothing lands outside data/
            if (!Native.IsWindows) psi.Environment["HOME"] = SteamCmdDir;
            foreach (var a in new[] { "+force_install_dir", ServerDir, "+login", "anonymous", "+app_update", AppId.ToString(Inv), "validate", "+quit" })
                psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi))
            {
                void Line(string l)
                {
                    if (string.IsNullOrWhiteSpace(l)) return;
                    var m = Progress.Match(l);
                    if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, Inv, out double v)) progress?.Invoke(v / 100.0);
                    else log("  " + l.Trim());
                }
                p.OutputDataReceived += (_, e) => Line(e.Data);
                p.ErrorDataReceived += (_, e) => Line(e.Data);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                using (cancel.Register(() => { try { p.Kill(true); } catch { } }))
                    await p.WaitForExitAsync();
            }
        }

        // ---------------------------------------------------------------- BepInEx and the test build of NukeStats
        // payload.zip is built into the program: BepInEx 5 (Unity Mono), the doorstop loader for each system, the
        // Server Tester build of NukeStats and the two test missions.
        public static bool InstallMods(Action<string> log)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ServerTester.payload.zip"))
            {
                if (s == null) { log("This build of the tester has no BepInEx/NukeStats payload."); return false; }
                using (var z = new ZipArchive(s))
                {
                    string os = Native.IsWindows ? "windows/" : "linux/";
                    foreach (var e in z.Entries)
                    {
                        if (e.FullName.EndsWith("/")) continue;
                        string rel = null;
                        if (e.FullName.StartsWith("common/", StringComparison.Ordinal)) rel = e.FullName.Substring(7);
                        else if (e.FullName.StartsWith(os, StringComparison.Ordinal)) rel = e.FullName.Substring(os.Length);
                        if (rel == null) continue;
                        string dst = Path.Combine(ServerDir, rel.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        e.ExtractToFile(dst, true);
                        if (!Native.IsWindows && (rel.EndsWith(".sh") || rel.EndsWith(".so"))) Chmod(dst);
                    }
                }
            }
            log("BepInEx, the test build of NukeStats and the test missions are in place");
            return true;
        }

        // ---------------------------------------------------------------- the test server's settings
        // Hidden, passworded, on its own ports, the chosen mission from the payload (Stress Test: Escalation's map with
        // aircraft only, Compasses and Vagrants with guns and heat-seeking missiles; Realistic Benchmark: the full Escalation battle), and an
        // empty server never stops its mission.
        public static void WriteConfigs(TestOptions o)
        {
            Directory.CreateDirectory(Path.Combine(ServerDir, "missions"));
            Directory.CreateDirectory(Path.Combine(ServerDir, "logs"));
            var cfg = new Dictionary<string, object>
            {
                ["MissionDirectory"] = Path.Combine(ServerDir, "missions"),
                ["ModdedServer"] = "true",
                ["Hidden"] = true,
                ["ServerName"] = "Server Tester (private test, do not join)",
                ["Password"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
                ["MaxPlayers"] = 2,
                ["RotationType"] = 0,
                ["MissionRotation"] = new[] { new Dictionary<string, object> { ["Key"] = new Dictionary<string, string> { ["Group"] = "User", ["Name"] = o.Mission == TestPlan.RealisticBenchmark ? TestPlan.RealisticBenchmark : TestPlan.StressTest }, ["MaxTime"] = 86400.0 } },
                ["DisableErrorKick"] = true,
                ["PostMissionDelay"] = 10.0,
                ["NoPlayerStopTime"] = 100000000.0,
                ["Port"] = new Dictionary<string, object> { ["IsOverride"] = true, ["Value"] = o.GamePort },
                ["QueryPort"] = new Dictionary<string, object> { ["IsOverride"] = true, ["Value"] = o.QueryPort },
                ["BanListPaths"] = new string[0],
                ["ErrorKickImmuneListPaths"] = new string[0],
            };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));

            // NukeStats: timing on, nobody needed, a fixed AI load; its gameplay features that could end the mission off
            Directory.CreateDirectory(Path.GetDirectoryName(PluginCfgPath));
            string skip = o.VisualSkips ? "true" : "false";
            var sb = new StringBuilder();
            sb.AppendLine("[Perf]");
            sb.AppendLine("Timing = true");
            sb.AppendLine("Phases = true");
            sb.AppendLine("PretendPlayers = true");
            sb.AppendLine("TestAiAircraft = " + o.FirstAi.ToString(Inv));
            sb.AppendLine("JobWorkers = 0");   // the game's own, as out of the box, until a step sets them
            sb.AppendLine("TargetFps = " + TestPlan.Compare(o).ToString(Inv));   // the warm-up at the steps' own target, as the plugin's
            sb.AppendLine("FramePacer = 0");
            sb.AppendLine("HitchMs = 0");
            foreach (var k in new[] { "SkipPilotAnimation", "SkipVisualConstraints", "SkipVisualEffects", "SkipServerParticles" }) sb.AppendLine(k + " = " + skip);
            sb.AppendLine();
            sb.AppendLine("[Annihilate]");
            sb.AppendLine("Enabled = false");
            sb.AppendLine();
            sb.AppendLine("[Balance]");
            sb.AppendLine("Enforce = false");
            File.WriteAllText(PluginCfgPath, sb.ToString());
        }

        static void Chmod(string path)
        {
            try { File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute); } catch { }
        }
    }
}
