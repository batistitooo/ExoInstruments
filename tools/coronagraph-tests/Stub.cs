// CameraFilter lives in the Unity-dependent Visualization layer (SolarSystemCameraTexture.cs), but
// VisualTelescopeSpec names it to describe which filters each instrument physically has. The enum
// itself carries no Unity dependency, so restating it here lets the harness compile the REAL
// VisualTelescopeCatalog, which is the point: the pixel pitch, focal length, full well, read noise,
// conversion factor, PRNU, DSNU, linearity and field stop tested below are the mod's own shipped
// figures rather than copies of them. Same device, and same reason, as tools/frame-tests/Stub.cs.
namespace ExoInstruments.Visualization { public enum CameraFilter { Luminance, Red, Green, Blue, HAlpha, OIII, SII, NII, OII, OI } }
