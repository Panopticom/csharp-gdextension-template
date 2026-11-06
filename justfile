# justfile — run with `just <recipe>`
# https://github.com/casey/just

# List available recipes
default:
    @just --list

# First-time setup: clone godot-dotnet, build its NuGet packages, register the local feed
bootstrap:
    dotnet run --project build -- bootstrap

# Release build — output: ./godot-project/addons/<team>/<project>/bin/<os>/
build:
    dotnet run --project build -- build

# Debug build — output: ./godot-project/addons/<team>/<project>/bin/<os>/ (includes .pdb)
debug:
    dotnet run --project build -- build --debug

# Remove stale build artifacts
clean:
    dotnet run --project build -- clean

# Wipe all caches including MSBuild state, godot-dotnet artifacts, and local NuGet feed
# Run 'just bootstrap' afterwards to rebuild from scratch
reset:
    dotnet run --project build -- reset

# Build and run the main scene. Set GODOT4 env var if godot4 isn't on your PATH.
run:
    dotnet run --project build -- run

# Build and run the scene currently open in the Godot editor.
run-editor-scene:
    dotnet run --project build -- run-editor-scene

# Rename the addon — moves godot-project/addons/<old>/<old>/ and updates the .gdextension file
# Usage: just rename acme mygame
rename team project:
    dotnet run --project build -- rename {{team}} {{project}}
