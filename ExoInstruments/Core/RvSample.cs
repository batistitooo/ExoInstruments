namespace ExoInstruments.Core
{
    /// <summary>
    /// One radial-velocity measurement: universal time (seconds), velocity (m/s,
    /// systemic velocity subtracted), and its 1-sigma uncertainty (m/s) -- the
    /// quoted per-epoch precision a real RV pipeline reports alongside each point.
    /// </summary>
    public struct RvSample
    {
        public double Ut;
        public double VelocityMps;
        public double UncertaintyMps;

        public RvSample(double ut, double velocityMps, double uncertaintyMps)
        {
            Ut = ut;
            VelocityMps = velocityMps;
            UncertaintyMps = uncertaintyMps;
        }
    }
}
