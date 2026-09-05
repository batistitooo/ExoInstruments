// CameraFilter lives in the Unity-dependent Visualization layer (SolarSystemCameraTexture.cs), and
// section F is about a cache in that file which is keyed on it. The enum itself carries no Unity
// dependency, so restating it here lets the harness name the filters the way the mod does rather
// than as bare strings. Same device, and same reason, as tools/fringe-tests/Stub.cs.
namespace ExoInstruments.Visualization { public enum CameraFilter { Luminance, Red, Green, Blue, HAlpha, OIII, SII, NII, OII, OI } }
