// Minimal stand-in for the Visualization-side type InstrumentSpec references; the harness
// only exercises Core physics, which never touches it.
namespace ExoInstruments.Visualization { public enum CameraFilter { Luminance, Red, Green, Blue, HAlpha } }
namespace ExoInstruments.Core { public sealed class VisualTelescopeSpec { public string Name; } }
