// Minimal stand-ins for the Visualization-side types Core's instrument descriptions reference.
// The harness only times Core physics, which never reads them.
namespace ExoInstruments.Visualization { public enum CameraFilter { Luminance, Red, Green, Blue, HAlpha } }
namespace ExoInstruments.Core { public sealed class VisualTelescopeSpec { public string Name; } }
