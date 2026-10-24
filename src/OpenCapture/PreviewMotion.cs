using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace OpenCapture
{
    internal static class PreviewMotion
    {
        // A 45 ms response trails by only a few pixels at ordinary mouse speed.
        // Exponential convergence retargets from the current position without bounce.
        internal static PointF Advance(PointF current, PointF target, double seconds)
        {
            double amount = 1.0 - Math.Exp(-Math.Max(0.0, seconds) / 0.045);
            double x = current.X + (target.X - current.X) * amount;
            double y = current.Y + (target.Y - current.Y) * amount;
            double dx = target.X - x;
            double dy = target.Y - y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance > 72.0)
            {
                x = target.X - dx * 72.0 / distance;
                y = target.Y - dy * 72.0 / distance;
            }
            if (Math.Abs(target.X - x) < 0.35 && Math.Abs(target.Y - y) < 0.35)
            {
                return target;
            }
            return new PointF((float)x, (float)y);
        }

        internal static bool AnimationsEnabled
        {
            get
            {
                bool enabled;
                return !SystemParametersInfo(0x1042, 0, out enabled, 0) || enabled;
            }
        }

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(
            uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
    }
}
