using System.Collections.Generic;

namespace Sprig.App.ViewModels;

/// <summary>A node's place in a layout.</summary>
/// <param name="radius">Half the width it needs, label included.</param>
public sealed class LayoutBody(string key, string group, double radius)
{
    public string Key => key;
    public string Group => group;
    public double Radius => radius;
    public double X { get; set; }
    public double Y { get; set; }
    public double Vx { get; set; }
    public double Vy { get; set; }
    public bool Pinned { get; set; }
}

/// <summary>A group's (a repo's) box: round its nodes, with room above for its title and below for their labels.</summary>
public readonly record struct LayoutBox(double Left, double Top, double Right, double Bottom)
{
    /// <summary>Above a node's centre: its circle, its CPU and memory, and the box's title.</summary>
    public const double Above = 92;

    /// <summary>Below a node's centre: its circle, name, state and infra tags.</summary>
    public const double Below = 82;

    public const double Side = 6;

    public bool Overlaps(LayoutBox other, double gap)
        => Left < other.Right + gap && other.Left < Right + gap && Top < other.Bottom + gap && other.Top < Bottom + gap;
}

/// <summary>Where a workspace graph's nodes go: by forces, or in rows.</summary>
public interface IGraphLayout
{
    IReadOnlyList<LayoutBody> Bodies { get; }
    LayoutBody? Find(string key);

    /// <summary>The boxed groups, each with its box as it stands.</summary>
    IEnumerable<(string Group, LayoutBox Box)> Boxes();

    /// <summary>The points a link bends through between its ends; empty to draw it straight.</summary>
    IReadOnlyList<(double X, double Y)> Route(string from, string to);

    /// <summary>The link runs against the flow (from a node in a lower row to one above it), as a loop needs one to.</summary>
    bool RunsBack(string from, string to);

    /// <summary>Links leave and enter nodes at the top and bottom, rows apart, rather than straight between centres.</summary>
    bool Rows { get; }

    /// <summary>Nodes can be dragged to a place of their own.</summary>
    bool CanDrag { get; }

    bool Settled { get; }
    void Step();
    void Reheat(double alpha = 0.3);
}
