// ExampleNode.cs
//
// A Godot Node written in C# using godot-dotnet bindings.
//
// Attribute reference (confirmed from Godot.Analyzers source file names):
//   [GodotClass]    — marks this class for ClassDB registration
//   [BindMethod]    — exposes a method to GDScript / ClassDB
//   [BindProperty]  — exposes a property to the Inspector and GDScript
//   [BindSignal]    — exposes a signal (not used here, shown for reference)
//
// The Godot.SourceGenerators Roslyn analyzer reads these at build time and
// emits a second partial class file containing:
//   - GDExtensionClassCreationInfo4 with all method/property descriptors
//   - Variant marshalling trampolines for each bound method and property
//   - Virtual method dispatch stubs (_Ready, _Process, etc.)
//   - DynamicDependency attributes to protect members from NativeAOT trimming

using Godot;
using Godot.Bridge;

namespace MyProject;

[GodotClass]
public partial class ExampleNode : Godot.Node {

    private int _count;


    /// <summary>
    /// The current running total. [BindProperty] makes it visible in the
    /// Godot Inspector and readable/writable from GDScript as node.Count.
    /// </summary>
    [BindProperty]
    public int Count {
        get => _count;
        set => _count = value;
    }


    /// <summary>
    /// Called by Godot when this node enters the scene tree.
    /// Because ExampleNode inherits from Node (a Godot class), _Ready is a
    /// virtual that godot-dotnet's source generator wires up automatically —
    /// no attribute needed, just override it as normal.
    /// </summary>
    protected override void _Ready() {
        GD.PrintS($"[ExampleNode] _Ready called. Count = {_count}");
    }


    /// <summary>
    /// Add a value to the running total.
    /// Callable from GDScript as: node.Add(n)
    /// </summary>
    [BindMethod]
    public void Add(int value) {
        _count += value;
        GD.PrintS($"[ExampleNode] add({value}) → Count = {_count}");
    }

    /// <summary>Reset the counter to zero.</summary>
    [BindMethod]
    public void Reset() {
        _count = 0;
        GD.PrintS("[ExampleNode] reset() → Count = 0");
    }

    /// <summary>
    /// Return the current count.
    /// Callable from GDScript as: var n = node.GetCount()
    /// </summary>
    [BindMethod]
    public int GetCount() => _count;
}
