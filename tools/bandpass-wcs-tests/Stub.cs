// CameraFilter lives in the Unity-dependent Visualization layer (SolarSystemCameraTexture.cs), but
// VisualTelescopeSpec names it to describe which filters each instrument physically has. The enum
// itself carries no Unity dependency, so restating it here lets the harness compile the REAL
// VisualTelescopeCatalog rather than a stand-in -- which matters: these checks then verify the
// throughput and filter figures the mod actually ships, not a second copy of them that is free to
// drift. (tools/skyfield-tests stubs VisualTelescopeSpec instead, and so cannot check its values.)
namespace ExoInstruments.Visualization { public enum CameraFilter { Luminance, Red, Green, Blue, HAlpha } }
