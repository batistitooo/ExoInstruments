// CameraFilter lives in the Unity-dependent Visualization layer (SolarSystemCameraTexture.cs), but
// VisualTelescopeSpec names it to describe which filters each instrument physically has. The enum
// itself carries no Unity dependency, so restating it here lets the harness compile the REAL
// VisualTelescopeCatalog, which is the point: the aperture, obstruction, focal length, pixel
// pitch, spider geometry and site seeing fed to GalSim are then the mod's own shipped figures.
// Same device, and same reason, as tools/bandpass-wcs-tests/Stub.cs.
namespace ExoInstruments.Visualization { public enum CameraFilter { Luminance, Red, Green, Blue, HAlpha, OIII, SII, NII, OII, OI } }
