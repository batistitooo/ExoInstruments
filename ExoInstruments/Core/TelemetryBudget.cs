using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// What it costs to get a frame off the spacecraft: how many bits it is, how long the link
    /// takes to move them, and how much charge the exposure and the transmission consume.
    ///
    /// WHY THIS EXISTS AS PHYSICS RATHER THAN AS A CHECKBOX. The requirement is that a telescope
    /// in orbit can be operated from the ground only if it has power and a working antenna, and
    /// otherwise only from the spacecraft itself. That could have been a boolean. It is not one
    /// here, because the interesting version of the constraint is quantitative: a WFC3/UVIS
    /// full frame is 16.8 million pixels at 16 bits, which is 269 megabits, and the antenna
    /// carried decides whether that is a few seconds or most of an orbit. A small relay antenna
    /// on a large-format detector is a real design mistake a player can make, and it should cost
    /// them time rather than be silently allowed.
    ///
    /// WHAT IS AND IS NOT SOURCED. The frame size is arithmetic on the detector's own published
    /// format and ADC depth, so it is exact. The link rate is KSP's, taken from the antenna
    /// parts the player actually flew, because that is the hardware in the game; the real HST
    /// downlinks through TDRSS on a schedule set by a network shared with other spacecraft (HST
    /// Primer: Data Storage and Transmission), which has no counterpart in KSP and is not
    /// modelled. Electric charge is a game resource with no conversion to watts anywhere in KSP,
    /// so the power numbers are game balance and are labelled as such where they are declared
    /// (SpacePlatformSpec).
    ///
    /// Pure C# with no Unity or KSP dependency, like the rest of Core.
    /// </summary>
    public static class TelemetryBudget
    {
        /// <summary>Bits in one full-frame readout of the given detector format at the given ADC depth.</summary>
        public static double FrameBits(long pixels, int bitsPerPixel)
        {
            if (pixels <= 0 || bitsPerPixel <= 0) return 0.0;
            return (double)pixels * bitsPerPixel;
        }

        /// <summary>
        /// Seconds to move <paramref name="bits"/> down a link running at
        /// <paramref name="bitsPerSecond"/>, degraded by the link's signal strength.
        ///
        /// Signal strength enters LINEARLY as a throughput multiplier rather than through a
        /// Shannon-capacity relation, because that is what it is in KSP: CommNet's signal
        /// strength already scales the effective data rate of a connection, and treating it as a
        /// bandwidth-limited channel here would be modelling a physical layer the game does not
        /// have. PositiveInfinity when there is no link at all, which is the honest answer to
        /// "how long until this arrives" rather than a large finite number.
        /// </summary>
        public static double DownlinkSeconds(double bits, double bitsPerSecond, double signalStrength)
        {
            if (!(bits > 0.0)) return 0.0;
            double effective = Math.Max(0.0, bitsPerSecond) * Math.Max(0.0, Math.Min(1.0, signalStrength));
            if (!(effective > 0.0)) return double.PositiveInfinity;
            return bits / effective;
        }

        /// <summary>
        /// Electric charge one exposure consumes: the instrument's exposure draw over the open-shutter time,
        /// plus its idle draw over the readout.
        /// </summary>
        public static double ExposureCharge(double exposureSeconds, double readoutSeconds,
                                            double exposureChargePerSecond, double idleChargePerSecond)
        {
            return Math.Max(0.0, exposureSeconds) * Math.Max(0.0, exposureChargePerSecond)
                 + Math.Max(0.0, readoutSeconds) * Math.Max(0.0, idleChargePerSecond);
        }

        /// <summary>
        /// Human-readable size of a frame, for the UI: bits are the physical quantity but nobody
        /// reads a telescope's output in bits.
        /// </summary>
        public static string DescribeBits(double bits)
        {
            if (!(bits > 0.0)) return "0 B";
            double bytes = bits / 8.0;
            if (bytes < 1024.0) return $"{bytes:F0} B";
            if (bytes < 1024.0 * 1024.0) return $"{bytes / 1024.0:F1} kB";
            if (bytes < 1024.0 * 1024.0 * 1024.0) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }
}
