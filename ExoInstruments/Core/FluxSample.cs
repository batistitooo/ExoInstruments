namespace ExoInstruments.Core
{
    /// <summary>
    /// One photometric measurement: universal time (seconds), normalized flux
    /// (1.0 = baseline), and its 1-sigma uncertainty (same normalized units) --
    /// the quoted per-exposure precision a real photometry pipeline reports
    /// alongside each point.
    /// </summary>
    public struct FluxSample
    {
        public double Ut;
        public double Flux;
        public double UncertaintyFlux;

        public FluxSample(double ut, double flux, double uncertaintyFlux)
        {
            Ut = ut;
            Flux = flux;
            UncertaintyFlux = uncertaintyFlux;
        }
    }
}
