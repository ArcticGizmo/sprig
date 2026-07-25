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

/// <summary>
/// Append a source token (<c>${sprig.ports.x}</c> or <c>${sprig.workspace}</c>) to an input's
/// expression — dragging a second source into an existing transform node to fan it in.
/// </summary>
public sealed record AppendSourceRequest(string Repo, string Input, string Token);
