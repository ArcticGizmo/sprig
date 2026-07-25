using Sprig.Core.Stacks;

namespace Sprig.App.Controls;

/// <summary>Wire a repo input to a stack port (from a drag pin → port on the canvas).</summary>
public sealed record WireRequest(string Repo, string Input, string Port);

/// <summary>Identifies one repo input pin — used to unbind it.</summary>
public sealed record PinRef(string Repo, string Input);

/// <summary>Re-shape a bound input's value with a transform preset (from the canvas pin menu).</summary>
public sealed record TransformRequest(string Repo, string Input, TransformPreset Preset);
