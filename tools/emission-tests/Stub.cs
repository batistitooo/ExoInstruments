// CameraFilter lives in the Unity-dependent Visualization layer (SolarSystemCameraTexture.cs), but
// VisualTelescopeSpec names it to describe which filters each instrument physically has. The enum
// itself carries no Unity dependency, so restating it here lets the harness compile the REAL
// VisualTelescopeCatalog, which is the point: the aperture, obstruction, focal length, pixel
// pitch, spider geometry and site seeing fed to GalSim are then the mod's own shipped figures.
// Same device, and same reason, as tools/bandpass-wcs-tests/Stub.cs.
// NdFilterStop is restated for the same reason: VisualTelescopeSpec names it too.
namespace ExoInstruments.Visualization
{
    public enum CameraFilter { Luminance, Red, Green, Blue, HAlpha, OIII, SII, NII, OII, OI, Nuv220, Nuv250, Nuv330 }
    public enum NdFilterStop { None, Nd8, Nd64, Nd1000, Nd6300, Nd100000, ZimpolNd1, ZimpolNd2, ZimpolNd4 }
}
