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
        private long PBStartTime { get; set; }
        public ConcurrentQueue<long> Offences { get; private set; } = new();
        public int Recompiles { get; private set; }
        public double LastRunTimeMS { get; private set; }
        private double RunTimesSum { get; set; } = 0;
        public double PeekRunTimeMS { get; private set; } = 0;
        public long memoryUsage { get; private set; }
        
        private long _lastOffenceTick;
        public long LastOffenceTick
        {
            get => _lastOffenceTick;
            set => Interlocked.Exchange(ref _lastOffenceTick, value);
        }
        
        private long _lastUpdateTick;
        public long LastUpdateTick
        {
            get => Interlocked.Read(ref _lastUpdateTick);
            set => Interlocked.Exchange(ref _lastUpdateTick, value);
        }

        public void updateMemoryUsage(long bytes)
        {
            memoryUsage = bytes;
        }
        
        public void ResetMemoryUsage()
        {
            memoryUsage = 0;
        }

        public TrackedPBBlock(string gridName, MyProgrammableBlock pbBlock)
        {
            GridName = gridName;
            PBStartTime = Stopwatch.GetTimestamp();
            ProgrammableBlock = pbBlock;
            LastUpdateTick = Stopwatch.GetTimestamp();
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
                    double partial = elapsed >= 3 ? _loadWindowMs / elapsed : 0;
                    return System.Math.Max(_lastWindowLoad, partial);
                }
            }
        }

        // Guards the run-time queue and its running sum: a punishment (another thread) can reset them while a new sample arrives.
        private readonly object _statsLock = new();

        public void AddRuntimeData(double lastRunTimeMS, ulong steamId)
        {
            if (IsUnderGracePeriod(steamId)) return;
            RecordLoad(lastRunTimeMS);
            lock (_statsLock)
            {
                if (lastRunTimeMS > PeekRunTimeMS)
                    PeekRunTimeMS = lastRunTimeMS;
                LastRunTimeMS = lastRunTimeMS;
                if (RunTimesMS.Count >= Config.MaxRunsToTrack)
                    if (RunTimesMS.TryDequeue(out double oldRunTimeMS))
                        RunTimesSum -= oldRunTimeMS;

                RunTimesMS.Enqueue(lastRunTimeMS);
                RunTimesSum += lastRunTimeMS;
            }
            LastUpdateTick = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Forgets the run times that led to a punishment (offences, recompile count and the startup allowance stay as they are),
        /// so the same old numbers cannot punish the block again while it is switched off.
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
        }
        
        public double[] GetRunTimesMS
        {
            get
            {
                double[] runTimes = new double[RunTimesMS.Count];
                int index = 0;

                using IEnumerator<double> enumerator = RunTimesMS.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    runTimes[index++] = enumerator.Current;
                }
                return runTimes;
            }
        }
        
        public double RunTimeMSAvg
        {
            get
            {
                // If the queue doesnt have enough samples to calculate the average, return 0
                if (RunTimesMS.Count < Config.MaxRunsToTrack)
                    return 0;
                
                return RunTimesSum / RunTimesMS.Count;
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
                int samples = RunTimesMS.Count;
                return samples == 0 ? 0 : System.Math.Max(0, RunTimesSum / samples);
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
            if (Config.PrivilegedPlayers.TryGetValue(SteamId, out PrivilegedPlayer privilegedPlayer))
                return PBStartTime.TicksTillNow_TimeSpan().TotalSeconds < privilegedPlayer.StartupAllowance;
            
            double result = PBStartTime.TicksTillNow_TimeSpan().TotalSeconds;
            return result <= Config.StartupAllowance;
        }
        
        public void ClearRunTimes()
        {
            lock (_statsLock)
            {
                RunTimesMS = new ConcurrentQueue<double>();
                LastRunTimeMS = 0;
                RunTimesSum = 0;
            }
            lock (_loadLock)
            {
                _loadWindowStart = 0;
                _loadWindowMs = 0;
                _lastWindowLoad = 0;
            }
            Offences = new ConcurrentQueue<long>();
            Recompiles++;
            PBStartTime = Stopwatch.GetTimestamp();
            _lastUpdateTick = Stopwatch.GetTimestamp();
            PeekRunTimeMS = 0;
        }
    }
}