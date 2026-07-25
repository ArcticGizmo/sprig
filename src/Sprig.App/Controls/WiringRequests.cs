using Sprig.Core.Stacks;

namespace Sprig.App.Controls;

/// <summary>Wire a repo input to a stack port (drag a port → input on the canvas).</summary>
public sealed record WireRequest(string Repo, string Input, string Port);

/// <summary>Identifies one repo input pin — used to unbind it, or to bind it to the workspace source.</summary>
public sealed record PinRef(string Repo, string Input);

/// <summary>
/// Create a new stack port named <see cref="PortName"/> and wire <see cref="Input"/> to it — raised
/// when a drag from the phantom "create new…" slot lands on an input and the user names the port.
/// </summary>
public sealed record CreatePortRequest(string Repo, string Input, string PortName);

/// <summary>Set an input's expression directly — from the inline editor on an input or a transform node.</summary>
public sealed record SetExpressionRequest(string Repo, string Input, string Expression);

/// <summary>Rename a stack port (from the canvas port menu); every binding that used it is rewritten.</summary>
public sealed record RenamePortRequest(string OldName, string NewName);

/// <summary>Re-shape a bound input's value with a transform preset (from the canvas pin menu).</summary>
public sealed record TransformRequest(string Repo, string Input, TransformPreset Preset);
