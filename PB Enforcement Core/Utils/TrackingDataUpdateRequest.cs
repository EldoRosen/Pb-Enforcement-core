using Sandbox.Game.Entities.Blocks;

namespace Advanced_PB_Limiter.Utils
{
    public class TrackingDataUpdateRequest
    {
        public MyProgrammableBlock? ProgBlock { get; set; }
        public double RunTime { get; set; }
        /// <summary>Owner when the run happened (read on the game thread).</summary>
        public long OwnerId { get; set; }
        /// <summary>A garbage collection happened during the run: the sample counts for averages and load, but never on its own for a single-run or instant-kill punishment.</summary>
        public bool GcAffected { get; set; }
    }
}
