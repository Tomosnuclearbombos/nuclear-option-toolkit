namespace ServerTester
{
    // What the little ? next to an option says, in plain English.
    internal static class Help
    {
        public const string Mission =
            "The Stress Test is aircraft only and steady from run to run, the one to find your settings with. The Realistic "
            + "Benchmark is the full Escalation battle with ground units and ships, closer to a real match but less steady, the one "
            + "to check them with afterwards.";

        public const string Ai =
            "This is how many AI aircraft the test keeps up, a quarter BDF and three quarters PALA. More AI makes the physics worker "
            + "differences show, although, it asks a lot more of the CPU. 80 suits most PCs.";

        public const string Baseline =
            "This measures the server first, on every core with one worker fewer than its cores. The core count and physics worker "
            + "tests are set against it, and every test runs on its settings apart from the one it tests. If your server won't run "
            + "like that, pick the cores and workers it will have.";

        public const string Cores =
            "This runs the server on 1, 2, 3 and more of your fastest cores and on every core, with the baseline workers, and "
            + "suggests the fewest that are as quick as the best.";

        public const string Workers =
            "This runs 1 to 8 physics workers on the baseline cores and compares them with the baseline workers. It suggests the "
            + "fewest that are as quick as the best, never fewer than 2, the game's own least.";

        public const string Order =
            "Alternate runs the baseline between every step, so a PC warming up or a mission getting busier is taken out of the "
            + "comparison. One after another takes about half as long, however, that drift stays in the results.";

        public const string Target =
            "This measures every frame target and suggests the highest that held with the spare time set in the advanced options. "
            + "When none left that spare time, it suggests the highest that held, and when none held, the one with the highest frame "
            + "rate. Targets can be 60 to 240 fps.";

        public const string TargetOrder =
            "One after another runs the targets in order, the usual way. Alternating runs a baseline target between every step, the "
            + "90 or your own in the box beside it, to show whether the PC drifts.";

        public const string StepSeconds =
            "This is how long each step measures for, after its setting changes. 30 seconds shows the differences, however, a "
            + "longer step gives steadier numbers.";

        public const string UseCores =
            "The core count tests count up through the cores ticked here, fastest first. Untick any your server won't get, the "
            + "efficient ones on an Intel CPU for example. The baseline uses the cores under Baseline cores, every core unless you "
            + "change it.";

        public const string AutoBaseline =
            "This measures the server first, on every core with one worker fewer than its cores. Round 1 sets every core and worker "
            + "pair against it. If your server won't run like that, pick the cores and workers it will have.";

        public const string Automated =
            "This runs the tests in rounds: the baseline, every core count with every physics worker count, the best 3 of those "
            + "again for 2 minutes each, the frame target on the pair picked, then the AI load on both picks. Everything runs at 90 "
            + "fps until the frame target round picks one. It picks each round's best itself, or you pick when a round is done.";

        public const string Custom =
            "This runs only the tests you tick, each on the baseline cores and workers apart from what it tests.";

        public const string AutoPairs =
            "This runs every core count with every physics worker count and sets each pair against the baseline, then measures "
            + "the best 3 again for 2 minutes each, with a baseline between them. It picks the best of those 3, or lets you pick.";

        public const string PickWhen =
            "Pick the best automatically goes straight on. Stop and let me pick waits with the round on the graph until you pick "
            + "and press CONTINUE, with the test server still running.";

        public const string AutoTarget =
            "This measures every frame target on the cores and workers picked, and picks the highest that held with the spare time "
            + "set in the advanced options, or lets you pick. A target the server can't hold doesn't stop the round, given a higher "
            + "one can still come out better.";

        public const string AutoAi =
            "This adds AI aircraft a step at a time on both picks, every load up to the most you set, and says how far the frame "
            + "target held.";

        public const string CompareFps =
            "This is the frame target the baseline and the core and worker steps run at, so every step asks for the same work. 90 "
            + "is safe on a lower end CPU and sets the worse settings clearly apart from the better ones.";

        public const string Spare =
            "This is how much of each second the suggested frame target has to leave spare, given a busy match with players costs "
            + "more than this test. 25% leaves room for that.";

        public const string Power =
            "This switches the PC to high performance power for the test and puts yours back after, given power saving holds the "
            + "CPU clock down and that shows as a slower server.";

        public const string Skips =
            "This turns on the NukeStats switches that skip work only seen on screen: pilot models, smoke and sparks. Leave it on "
            + "unless you want to see what they save.";

        public const string AiLoad =
            "This adds AI aircraft a step at a time, every load up to the most you set, and says how far the frame rate held. It runs once, at the "
            + "end, given AI that is up can't be taken away, so with other tests ticked it starts no lower than their AI aircraft.";

        public const string Repeats =
            "More runs make the core and worker results surer, given a difference only counts when it is bigger than how much the "
            + "same setting moved between runs. Each run takes about as long as the first.";

        public const string HeldBy =
            "The 5% and 1% lows are the slowest frames, so holding a target on them means no stutter as well. The lows sit a little "
            + "under the target even when it holds easily, so they need 90% and 80% of it rather than 98%.";

        public const string Graph =
            "This shows one test: the baseline on top, its settings down the side and the measure you pick along the bottom. The "
            + "dot is the average of a setting's steps and the line its lowest to highest 15 second measurement, so when two lines "
            + "overlap a lot, the difference is within the test's own wobble. Tick more tests above to compare them: baselines that "
            + "ran the same are averaged into one row, otherwise you pick the baseline.";
    }
}
