using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Advanced_PB_Limiter.Settings;
using ExtensionMethods;
using NLog;
using Sandbox.Game.Entities.Blocks;

namespace Advanced_PB_Limiter.Utils
{
    public sealed class TrackedPBBlock
    {
        private static Logger Log = LogManager.GetCurrentClassLogger();
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        public MyProgrammableBlock? ProgrammableBlock { get; }
        private ConcurrentQueue<double> RunTimesMS { get; set; } = new ();
        public string GridName { get; }
        private long _pbStartTime;
        public ConcurrentQueue<long> Offences { get; private set; } = new();
        public int Recompiles { get; private set; }
        public double LastRunTimeMS { get; private set; }
        private double RunTimesSum { get; set; } = 0;
        public double PeekRunTimeMS { get; private set; } = 0;
        public long memoryUsage { get; private set; }
        /// <summary>How many memory scans in a row found this block over the memory limit (one high reading is not enough to punish).</summary>
        public int ConsecutiveMemoryScansOver { get; private set; }

        private long _lastOffenceTick;
        public long LastOffenceTick
        {
            get => Interlocked.Read(ref _lastOffenceTick);
            set => Interlocked.Exchange(ref _lastOffenceTick, value);
        }

        private long _lastUpdateTick;
        /// <summary>When this block last ran (any run, including runs inside the startup allowance).</summary>
        public long LastUpdateTick
        {
            get => Interlocked.Read(ref _lastUpdateTick);
            set => Interlocked.Exchange(ref _lastUpdateTick, value);
        }

        /// <summary>The block was removed from the world (its grid deleted, the block ground down, the grid left the server).</summary>
        public bool IsGone => ProgrammableBlock is null || ProgrammableBlock.Closed || ProgrammableBlock.MarkedForClose;

        /// <summary>
        /// Notes that the block ran. Called for every run, also while the block is in its startup allowance: before 3.1 only
        /// counted runs did this, so a block in its allowance looked inactive, was removed by the cleanup, came back with a
        /// fresh allowance, and was never measured.
        /// </summary>
        public void Touch() => LastUpdateTick = Stopwatch.GetTimestamp();

        public void updateMemoryUsage(long bytes, long threshold)
        {
            memoryUsage = bytes;
            ConsecutiveMemoryScansOver = threshold > 0 && bytes > threshold ? ConsecutiveMemoryScansOver + 1 : 0;
        }

        public void ResetMemoryUsage()
        {
            memoryUsage = 0;
            ConsecutiveMemoryScansOver = 0;
        }

        public TrackedPBBlock(string gridName, MyProgrammableBlock pbBlock)
        {
            GridName = gridName;
            _pbStartTime = Stopwatch.GetTimestamp();
            ProgrammableBlock = pbBlock;
            LastUpdateTick = Stopwatch.GetTimestamp();
        }

        // ---- instant kill: runs over the instant-kill limit inside the last few seconds
        private readonly Queue<long> _extremeSamples = new();

        /// <summary>Remembers one run over the instant-kill limit and returns how many there were within the last windowSeconds.</summary>
        public int RegisterExtremeSample(int windowSeconds)
        {
            long now = Stopwatch.GetTimestamp();
            long oldest = now - (long)(windowSeconds * (double)Stopwatch.Frequency);
            lock (_extremeSamples)
            {
                _extremeSamples.Enqueue(now);
                while (_extremeSamples.Count > 0 && _extremeSamples.Peek() < oldest) _extremeSamples.Dequeue();
                return _extremeSamples.Count;
            }
        }

        private void ClearExtremeSamples()
        {
            lock (_extremeSamples) _extremeSamples.Clear();
        }

        // ---- load: how many milliseconds of server time this block uses per second
        private const double LoadWindowSeconds = 10;
        private readonly object _loadLock = new();
        private long _loadWindowStart;
        private double _loadWindowMs;
        private double _lastWindowLoad;

        private void RecordLoad(double runTimeMs)
        {
            long now = Stopwatch.GetTimestamp();
            lock (_loadLock)
            {
                if (_loadWindowStart == 0) _loadWindowStart = now;
                _loadWindowMs += runTimeMs;

                double elapsed = (now - _loadWindowStart) / (double)Stopwatch.Frequency;
                if (elapsed >= LoadWindowSeconds)
                {
                    _lastWindowLoad = _loadWindowMs / elapsed;
                    _loadWindowStart = now;
                    _loadWindowMs = 0;
                }
            }
        }

        /// <summary>Milliseconds of server time this block uses per second (the higher of the last full 10 second window and the running one).</summary>
        public double LoadMSPerSecond
        {
            get
            {
                lock (_loadLock)
                {
                    if (_loadWindowStart == 0) return 0;
                    double elapsed = (Stopwatch.GetTimestamp() - _loadWindowStart) / (double)Stopwatch.Frequency;

                    // A block that stopped running (switched off, removed) must not keep its last window's load forever.
                    if (elapsed > LoadWindowSeconds * 2) return 0;

                    double partial = elapsed >= 3 ? _loadWindowMs / elapsed : 0;
                    return System.Math.Max(_lastWindowLoad, partial);
                }
            }
        }

        // Guards the run-time queue and its running sum: a punishment (another thread) can reset them while a new sample arrives.
        private readonly object _statsLock = new();

        public void AddRuntimeData(double lastRunTimeMS, ulong steamId)
        {
            Touch();
            if (IsUnderGracePeriod(steamId)) return;
            RecordLoad(lastRunTimeMS);
            lock (_statsLock)
            {
                if (lastRunTimeMS > PeekRunTimeMS)
                    PeekRunTimeMS = lastRunTimeMS;
                LastRunTimeMS = lastRunTimeMS;
                while (RunTimesMS.Count >= System.Math.Max(1, Config.MaxRunsToTrack) && RunTimesMS.TryDequeue(out double oldRunTimeMS))
                    RunTimesSum -= oldRunTimeMS;

                RunTimesMS.Enqueue(lastRunTimeMS);
                RunTimesSum += lastRunTimeMS;
            }
        }

        /// <summary>
        /// Forgets the run times and offences that led to a punishment (recompile count and the startup allowance stay as they are),
        /// so the same old numbers cannot punish the block again, and a block switched back on starts with a clean record.
        /// </summary>
        public void ResetAfterPunishment()
        {
            lock (_statsLock)
            {
                RunTimesMS = new ConcurrentQueue<double>();
                RunTimesSum = 0;
                LastRunTimeMS = 0;
            }
            lock (_loadLock)
            {
                _loadWindowStart = 0;
                _loadWindowMs = 0;
                _lastWindowLoad = 0;
            }
            Offences = new ConcurrentQueue<long>();
            ClearExtremeSamples();
            ConsecutiveMemoryScansOver = 0;
        }

        public double[] GetRunTimesMS
        {
            get
            {
                lock (_statsLock) return RunTimesMS.ToArray();
            }
        }

        /// <summary>The average that limits are checked against: 0 until MaxRunsToTrack runs have been collected.</summary>
        public double RunTimeMSAvg
        {
            get
            {
                lock (_statsLock)
                {
                    // If the queue doesnt have enough samples to calculate the average, return 0
                    int samples = RunTimesMS.Count;
                    if (samples == 0 || samples < System.Math.Max(1, Config.MaxRunsToTrack))
                        return 0;

                    return System.Math.Max(0, RunTimesSum / samples);
                }
            }
        }

        /// <summary>
        /// For display only (the live view): the average of the runs collected so far. <see cref="RunTimeMSAvg"/> stays 0 until
        /// MaxRunsToTrack runs have been collected, which kept slow scripts at 0 in the live view for minutes. Limits still use RunTimeMSAvg.
        /// </summary>
        public double RunTimeMSAvgSoFar
        {
            get
            {
                lock (_statsLock)
                {
                    int samples = RunTimesMS.Count;
                    return samples == 0 ? 0 : System.Math.Max(0, RunTimesSum / samples);
                }
            }
        }

        public double RunTimeMSMaxPeek
        {
            get
            {
                double[] runTimes = GetRunTimesMS;
                double max = 0;
                for (int index = 0; index < runTimes.Length; index++)
                {
                    if (runTimes[index] > max)
                        max = runTimes[index];
                }

                return max;
            }
        }

        public bool IsUnderGracePeriod(ulong SteamId)
        {
            double age = Interlocked.Read(ref _pbStartTime).TicksTillNow_TimeSpan().TotalSeconds;
            if (Config.PrivilegedPlayers.TryGetValue(SteamId, out PrivilegedPlayer privilegedPlayer))
                return age < privilegedPlayer.StartupAllowance;

            return age <= Config.StartupAllowance;
        }

        public void ClearRunTimes()
        {
            lock (_statsLock)
            {
                RunTimesMS = new ConcurrentQueue<double>();
                LastRunTimeMS = 0;
                RunTimesSum = 0;
                PeekRunTimeMS = 0;
            }
            lock (_loadLock)
            {
                _loadWindowStart = 0;
                _loadWindowMs = 0;
                _lastWindowLoad = 0;
            }
            Offences = new ConcurrentQueue<long>();
            ClearExtremeSamples();
            Recompiles++;
            Interlocked.Exchange(ref _pbStartTime, Stopwatch.GetTimestamp());
            Touch();
        }
    }
}
