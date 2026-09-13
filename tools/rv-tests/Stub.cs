// CameraFilter lives in the Unity-dependent Visualization layer (SolarSystemCameraTexture.cs), but
// VisualTelescopeSpec names it, and Observatories names VisualTelescopeCatalog. The enum carries no
// Unity dependency, so restating it here lets the harness use the mod's REAL HARPS spec rather than
// a copy of its precision figure. Same device, and same reason, as tools/photometry-tests/Stub.cs.
// NdFilterStop is restated for the same reason: VisualTelescopeSpec names it too.
namespace ExoInstruments.Visualization
{
    public enum CameraFilter { Luminance, Red, Green, Blue, HAlpha, OIII, SII, NII, OII, OI, Nuv220, Nuv250, Nuv330 }
    public enum NdFilterStop { None, Nd8, Nd64, Nd1000, Nd6300, Nd100000, ZimpolNd1, ZimpolNd2, ZimpolNd4 }
}
