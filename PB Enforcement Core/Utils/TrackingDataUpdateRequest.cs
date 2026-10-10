using Sandbox.Game.Entities.Blocks;

namespace Advanced_PB_Limiter.Utils
{
    public class TrackingDataUpdateRequest
    {
        public MyProgrammableBlock? ProgBlock { get; set; }
        public double RunTime { get; set; }
    }
}