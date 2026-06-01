# MyProjectTest.gd
# Autoload singleton — exercises the AOT-compiled C# ExampleNode.
#
# Expected output if everything works:
#   [ExampleNode] _Ready called. Count = 0
#   [ExampleNode] add(10) → Count = 10
#   [ExampleNode] add(5)  → Count = 15
#   [ExampleNode] reset() → Count = 0
#   [MyProjectTest] All assertions passed!

extends Node

func _ready() -> void:
	var ext_path := "res://addons/my-group/MyProject/MyProject.gdextension"
	if not GDExtensionManager.is_extension_loaded(ext_path):
		push_error(
			"[MyProjectTest] Extension not loaded: %s\n" % ext_path +
			"  Run 'just bootstrap', then 'just build'")
		return

	# Instantiate our C# class — it's registered in ClassDB by the extension.
	# Note: godot-dotnet registers methods using C# PascalCase names, not snake_case.
	var s: Node = ClassDB.instantiate("ExampleNode")
	add_child(s)  # triggers _Ready()

	s.Add(10)
	s.Add(5)

	var total: int = s.GetCount()
	assert(total == 15, "Expected 15, got %d" % total)

	s.Reset()
	assert(s.GetCount() == 0, "Expected 0 after reset")

	# Read the exported property directly
	assert(s.Count == 0, "Expected Count == 0")

	print("[MyProjectTest] All assertions passed!")
