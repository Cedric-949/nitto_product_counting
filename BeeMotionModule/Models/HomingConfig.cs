using System;

namespace BeeMotionModule.Models
{
    /// <summary>
    /// Configuration for Homing (Search Machine Zero)
    /// </summary>
    public class HomingConfig
    {
        public short AxisIndex { get; set; } = 0;
        public short HomeMethod { get; set; } = 28; // Home switch, tìm theo chiều âm và không dùng Z-index
        public double HighVelocity { get; set; } = 10000; // Search speed pulse/s
        public double LowVelocity { get; set; } = 1000;   // Creep/Zero search speed pulse/s
        public double Acceleration { get; set; } = 100000;
        public int OffsetPulses { get; set; } = 0;        // Offset from home position
        public uint TimeoutMs { get; set; } = 30000;      // 30s timeout
    }
}

