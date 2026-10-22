namespace Advanced_PB_Limiter.Utils
{
    /// <summary>State of one programmable block's run while it is in progress (game thread only).</summary>
    public class ProfileData
    {
        public long EntityId;
        public long TimingStart;
        /// <summary>Stopwatch ticks spent in other programmable blocks this block ran during its own run (TryRun). Not charged to this block.</summary>
        public long ChildTicks;
        /// <summary>Sum of the GC collection counts when the run started; a different sum at the end means a collection happened during the run.</summary>
        public int GcCountAtStart;
        public bool Skipped;
        public bool Started;
    }
}
