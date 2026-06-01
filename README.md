# C# AOT GDExtension — godot-dotnet

---

## What is this?

A template project anyone can use as a base to create C# extensions for Godot 4.x that are natively compiled ahead of time. It relies on [raulsntos/godot-dotnet](https://github.com/raulsntos/godot-dotnet) to generate the engine bindings while we're all waiting for Godot('s official C# GDExtension integration).

## Why would I want that?

- You want to write libraries or editor plugins for Godot in C# that anyone can use *without needing to install the mono version of the editor*
- You are an advanced C# user and prefer it over c++ extensions
- You do not want your distributed C# code to be trivial to decompile
- You are going to make a large project (a few hundred thousand lines of C# at least), and need to keep startup time down so designers aren't miserable

> [!WARNING]
> This project is **not** for folks who want to try C# with the Godot game engine for the first time, you should just download the mono version of the editor.

See [Limitations](#limitations) for the tradeoffs that come with ahead of time compiling with dotnet.


## What is writing AOT C# extensions like?

- Annotate your classes with `[GodotClass]`, `[BindMethod]`, `[BindProperty]`
- `Godot.SourceGeneration` wires up Variant marshalling, virtual dispatch trampolines, and class registration at build time
- NativeAOT compiles everything to a self-contained native shared library — no .NET runtime required at runtime
- Override `protected override void _Ready()` and it just works

## I love it, how do I get started?

---

## Requirements

| | Windows | macOS 12 | Linux |
|---|---|---|---|
| .NET SDK | 9.0+ | 9.0+ | 9.0+ |
| Native toolchain | VS Build Tools (MSVC) | Xcode Command Line Tools | clang, zlib, libstdc++ |
| Godot | 4.2+ standard build | 4.2+ standard build | 4.2+ standard build |
| git | any | any | any |

> **Use the standard (non-Mono) Godot build.** The standard build can still
> load GDExtensions. The Mono build is only needed for Godot's *built-in* C#
> scripting system, which is completely separate from this.

> **Godot 4.2 is required** for the `reloadable = true` flag in the `.gdextension` file, which lets the editor hot-reload the extension after a build without restarting. If you need Godot 4.1 compatibility, remove `reloadable = true` from `godot-project/addons/<team>/<project>/<project>.gdextension` and set `compatibility_minimum = "4.1"`.

<details>
<summary>Installing dependencies</summary>

You can download the dotnet 9 SDK here for all platforms https://dotnet.microsoft.com/en-us/download/dotnet/9.0
Detailed information on platform support for dotnet 9 can be found [here](https://github.com/dotnet/core/diffs/3?base_sha=7ca4e0603924112cffaae1c615d29514b522aec3&name=os-support-9.0&pull_number=9604&sha1=7ca4e0603924112cffaae1c615d29514b522aec3&sha2=a97085b46cc988b251298eb5bbedd99bd8b7b4fb&short_path=4a789e8&w=false). Short version is included for each platform below along with required tools.

If you want to know which versions of dotnet are still recieving patches, [this link](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) will help you. Dotnet 9 is the minimum for this project.

**Windows**
Windows 10 1607 or newer is reccomended for explicit dotnet 9 support.
Requires MSVC build tools.
```sh
winget install Microsoft.VisualStudio.2022.BuildTools
```
Select the **Desktop development with C++** workload in the Visual Studio installer.

**macOS**
Officially MacOS 13-15 is supported by Microsoft for dotnet 9. MacOS 12 Monterey may also work, 11 or earlier does not.
```sh
xcode-select --install
```

**Linux — Debian/Ubuntu**
I'm not sure which versions are the minimum, but 22.04-24.10 are explicitly supported by microsoft for dotnet 9.
```sh
sudo apt install clang zlib1g-dev libstdc++-12-dev
```

**Linux — Fedora/RHEL**
I'm not sure which versions are the minimum, Fedora version 40 and RHEL 8, or 9 are the versions explicitly supported by microsoft for dotnet 9.
```sh
sudo dnf install clang zlib-devel libstdc++-devel
```

</details>

### Recommended: just

[just](https://github.com/casey/just) is a command runner that provides short aliases for the build tool commands. It is **not required** — every `just <recipe>` is a thin wrapper over a `dotnet run --project build -- <command>` call that you can run directly.

| With just | Without just |
|---|---|
| `just bootstrap` | `dotnet run --project build -- bootstrap` |
| `just build` | `dotnet run --project build -- build` |
| `just debug` | `dotnet run --project build -- build --debug` |
| `just run` | `dotnet run --project build -- run` |
| `just clean` | `dotnet run --project build -- clean` |
| `just reset` | `dotnet run --project build -- reset` |
| `just rename <team> <project>` | `dotnet run --project build -- rename <team> <project>` |

Install via `winget install Casey.Just`, `brew install just`, or see the [installation docs](https://github.com/casey/just#installation).

---

## Limitations

**godot-dotnet is unofficial and experimental.** It is a community project, not maintained by the Godot team. It may lag behind Godot releases, have incomplete API coverage, or introduce breaking changes between commits. Pin to a known-good commit for any project that needs to be perfectly stable.

**No managed debugger.** You cannot attach a debugger like the one in Visual Studio or Rider to a NativeAOT binary. The `-Debug` build copies a `.pdb` that a native debugger can use — [RAD Debugger](https://github.com/EpicGames/raddebugger) on Windows or [LLDB](https://lldb.llvm.org) on Linux/macOS — but you lose C# source-level step debugging. print statements with`GD.PrintS` is one practical debugging tool during development.

**NativeAOT forbids runtime code generation.** `Reflection.Emit`, compiled `Expression` trees, `Assembly.Load`, and dynamic proxies do not work. Any NuGet package that relies on these at runtime (many serializers, and DI containers) will either fail to publish or crash at runtime. Check for a "NativeAOT compatible" or "trimming safe" label before adding dependencies. COM interop ([`IL3052`](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/warnings/il3052)) is completely unsupported with no workaround. The most common warning you'll encounter is [`IL3050`](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/warnings/il3050) — it fires whenever you call something annotated with `[RequiresDynamicCode]` and is the reliable signal that a dependency isn't AOT-safe. If a third-party assembly produces AOT warnings that are collapsed into a single line, set `<TrimmerSingleWarn>false</TrimmerSingleWarn>` in your project to expand them ([`IL3053`](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/warnings/il3053)).

**Trimming analysis is suppressed.** Normally the NativeAOT linker removes code it can prove is unreachable — but Godot calls into your C# code from native side through function pointers resolved at runtime, so the linker cannot statically trace those call paths. The project sets `<SuppressTrimAnalysisWarnings>true</SuppressTrimAnalysisWarnings>` to silence the flood of false-positive warnings this would otherwise produce, and `TrimmerRootAssembly` roots your entire assembly so nothing gets trimmed out from under Godot. The practical consequence is that your binary will be larger than a fully trimmed native executable — all reachable IL is compiled, not just the subset the linker can prove is called.

**Reflection is limited by trimming.** The NativeAOT linker removes types and members it cannot statically prove are reachable. `TrimmerRootAssembly` protects your own assembly, and godot-dotnet emits `[DynamicDependency]` attributes for its binding trampolines, but ad-hoc reflection on types from *other* assemblies (NuGet packages, the Godot bindings) will silently return `null` or throw at runtime if the target was trimmed.

```csharp
// Breaks — "Godot.Node" lives in Godot.Bindings.dll which is not rooted by TrimmerRootAssembly.
// The linker may remove Node's constructor, so Activator returns null or throws.
var type = Type.GetType("Godot.Node");
var node = (Godot.Node?)Activator.CreateInstance(type!);

// Fix — reference the type directly so the linker sees it as reachable.
var node = new Godot.Node();
```

**Runtime generic instantiation is not supported.** Generic methods where all type arguments are statically known at the call site compile and run fine. What NativeAOT cannot do is create a new generic specialization on demand at runtime — there is no JIT to generate it. This is a distinct failure from trimming: the type is present and fully compiled, but the particular generic combination was never seen at build time so no code was emitted for it.

```csharp
// Fine — the compiler sees List<Vector3> and emits code for it.
var positions = new List<Vector3>();

// Breaks at runtime — MakeGenericType asks the runtime to compile List<T> for a
// type argument it only learns about when the line executes. No JIT means no code.
Type elementType = Type.GetType("Godot.Vector3")!;
var listType = typeof(List<>).MakeGenericType(elementType);
var list = Activator.CreateInstance(listType); // throws NotSupportedException

// Fix — use a concrete type argument the compiler can see, or restructure
// so that the generic instantiation appears explicitly in source.
var list = new List<Vector3>();
```

Note that `SuppressTrimAnalysisWarnings` hides the [`IL3050`](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/warnings/il3050) warning that would normally catch both of these patterns at publish time — the build succeeds silently and the crash only surfaces at runtime in a Release build.

**LINQ queries work; compiled expression trees are slower.** `System.Linq` (`Where`, `Select`, `OrderBy`, etc.) is fine — it compiles to static IL like any other code. `Expression<Func<T>>.Compile()` cannot JIT-compile under NativeAOT, so .NET 9 falls back to an expression tree interpreter instead — correct, but slower. 

**Cross compiling is more complicated.** No operating system can currently compile dotnet binaries for all supported platforms. NativeAOT requires the target platform's linker and CRT:

| ↓Build From↓   Target →| **Windows** | **Linux** | **MacOS** | **Android** | **iOS** |
|---|---|---|---|---|---|
| Windows | ✅ | ✅ WSL2 | ❌ | ✅ NDK | ❌ |
| Linux | ⚠️ non-trivial | ✅ | ❌ | ✅ NDK | ❌ |
| macOS | ❌ | ✅ clang + sysroot | ✅ | ✅ NDK | ✅ |

- ❌ requires Apple SDK or Windows CRT + PE linker which are not distributed for other OS's
- ⚠️ possible but requires manually sourcing Windows CRT headers/libs and a PE-targeting clang.
- Android NDK requires Android Studio or `sdkmanager`

---

## Directory layout

```
csharp_gdextension_dotnet/
│
├── MyGodotExtension.sln                 ← open this in Visual Studio or Rider
│
├── build/                               ← cross-platform build tool (C# console app)
│   ├── Build.csproj
│   ├── build.config                     ← teamName / projectName
│   └── Program.cs                       ← bootstrap / build / clean / rename subcommands
│
├── src/                                 ← the C# GDExtension source
│   ├── MyProject.csproj                 ← NativeAOT shared library (renamed by just rename)
│   ├── EntryPoint.cs                    ← [UnmanagedCallersOnly] entry point
│   └── ExampleNode.cs                   ← [GodotClass] node with [BindMethod]/[BindProperty]
│
└── godot-project/                       ← open this in Godot 4.x
    ├── project.godot
    ├── main.tscn
    ├── autoload/
    │   └── MyProjectTest.gd             ← GDScript that exercises the C# node
    └── addons/my-group/MyProject/
        ├── MyProject.gdextension        ← tells Godot where to find the binary
        └── bin/                         ← compiled binaries go here (created by build scripts)
```

---

## Step 1 — Bootstrap (first time only)

```sh
dotnet run --project build -- bootstrap
```

The bootstrap script:
1. Clones `raulsntos/godot-dotnet` to `./git-submodules/godot-dotnet`
2. Patches `.csproj` files in godot-dotnet to suppress spurious build warnings
3. Registers a local NuGet feed at `./nuget-packages`
4. Runs `build.cmd --productBuild --pushNupkgsLocal` to build the godot-dotnet packages
5. Prompts for your Godot executable path and saves it to `build/build-private.config`

Run `just build` afterwards to compile the extension.

---

## Step 2 — Run in Godot

### Option A — `just run` (build + launch in one step)

`just run` compiles the extension and then launches the Godot engine directly to run the project's main scene. It requires Godot to be findable — set the `GODOT4` environment variable to your Godot executable path if `godot4` or `godot` is not on your PATH:

**Windows (cmd.exe)**
```cmd
set GODOT4=C:\Program Files\Godot\Godot_v4.6.1-stable_win64.exe
just run
```

**Windows (PowerShell)**
```powershell
$env:GODOT4 = "C:\Program Files\Godot\Godot_v4.6.1-stable_win64.exe"
just run
```

**macOS / Linux**
```sh
export GODOT4="/Applications/Godot.app/Contents/MacOS/Godot"
just run
```

To avoid setting `GODOT4` every terminal session, add the `export` / `set` line to your shell profile (`~/.bashrc`, `~/.zshrc`) or use `setx` on Windows to persist it permanently:

```cmd
setx GODOT4 "C:\Program Files\Godot\Godot_v4.6.1-stable_win64.exe"
```

### Option B — Open in editor manually

1. Open `godot-project/` in Godot 4.2+ (standard build)
2. Press **F5**

### Expected output
```
Extension initialized via godot-dotnet!
[ExampleNode] _Ready called. Count = 0
[ExampleNode] add(10) → Count = 10
[ExampleNode] add(5)  → Count = 15
[ExampleNode] reset() → Count = 0
[VolumeTest] All assertions passed!
```

### GDScript naming — PascalCase, not snake_case

godot-dotnet registers methods and properties in ClassDB using their **C# names verbatim** (PascalCase). GDScript must call them with the same casing:

```gdscript
s.Add(10)       # not s.add(10)
s.GetCount()    # not s.get_count()
s.Reset()       # not s.reset()
s.Count         # property name is also PascalCase
```

To expose a snake_case name to GDScript instead, use the `Name` argument on the attribute:

```csharp
[BindMethod(Name = "add")]
public void Add(int value) { ... }
```

---

## Incremental builds

After bootstrap, use these commands for subsequent builds:

```sh
dotnet run --project build -- build           # Release
dotnet run --project build -- build --debug   # Debug — also copies .pdb alongside the DLL
```

To remove stale artifacts (while preserving caches that speed up rebuilds):

```sh
dotnet run --project build -- clean
```

---

## Known build issue: RS1038 and trim/AOT warnings

**Symptom:** The godot-dotnet `--productBuild` fails with errors like:
```
error RS1038: ...
error IL2111: ...
error IDE0040: ...
```

**Cause:** `--productBuild` enables `TreatWarningsAsErrors` globally. Several
diagnostics in godot-dotnet's own source and generated bindings are warnings
that become errors under this flag.

**Fix (applied automatically by bootstrap):** The bootstrap script patches
each affected `.csproj` file to add a `<NoWarn>` entry suppressing the
diagnostics that are noise rather than real bugs.

---

## NativeAOT trimming and reflection

The NativeAOT linker removes any type or member it cannot prove is reachable by static analysis. `TrimmerRootAssembly` protects your assembly's public API, and godot-dotnet emits `[DynamicDependency]` attributes to protect the binding trampolines it generates — but ad-hoc reflection on types known only at runtime will silently return `null` or throw.

**Example — factory pattern that breaks under trimming:**

```csharp
// Compiles fine. Passes tests in Debug. Crashes in Release (NativeAOT).
public static Godot.Node CreateNode(string typeName)
{
    var type = Type.GetType($"GDExtensionLoader.{typeName}");
    return (Godot.Node)Activator.CreateInstance(type!); // type is null — trimmed
}
```

The linker sees `Activator.CreateInstance` with a string it cannot evaluate at compile time, concludes `ExampleNode` is unreachable, and removes it. `Type.GetType` returns `null` at runtime.

**Fix — `[DynamicDependency]` tells the linker to keep the type:**

```csharp
[DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ExampleNode))]
public static Godot.Node CreateNode(string typeName)
{
    var type = Type.GetType($"GDExtensionLoader.{typeName}");
    return (Godot.Node)Activator.CreateInstance(type!);
}
```

For a larger registry of types, a static dispatch table avoids reflection entirely and has no trimming risk:

```csharp
private static readonly Dictionary<string, Func<Godot.Node>> _registry = new()
{
    ["ExampleNode"] = () => new ExampleNode(),
};

public static Godot.Node CreateNode(string typeName) => _registry[typeName]();
```

The failure is silent at publish time when `SuppressTrimAnalysisWarnings` is set (as it is in this project). Turning that property off and fixing the resulting `IL2` warnings is the right approach before shipping production code.

---

## How the pieces connect

```
dotnet run --project build -- bootstrap
  └─ clones godot-dotnet → patches .csproj files → builds NuGet packages
       └─ Godot.Bindings.*.nupkg          (Node, GD, Variant, ...)
          Godot.SourceGeneration.*.nupkg  (Roslyn source generator)
                │
                ▼
dotnet publish (NativeAOT, NativeLib=Shared)
  reads <projectName>.csproj
    → Godot.SourceGeneration sees [GodotClass] / [BindMethod] / [BindProperty]
    → emits binding trampolines, DynamicDependency attributes, and ClassDBExtensions.InitializeUserTypes()
    → EntryPoint.cs calls ClassDBExtensions.InitializeUserTypes(level) — all classes registered automatically
    → NativeAOT compiles everything to a single native .dll
    → exports "volume_extension_init" as a plain C symbol
                │
                ▼
Godot startup
  reads my-project.gdextension
    → entry_symbol = "volume_extension_init"
    → dlopen(<projectName>.dll)
    → calls EntryPoint.Initialize()
         → GodotBridge.Initialize() sets up proc-address table
         → at Scene level: GodotRegistry.RegisterClass<ExampleNode>(ExampleNode.BindMembers)
                │
                ▼
GDScript: ClassDB.instantiate("ExampleNode")
  → C# ExampleNode._Ready() fires
  → s.Add(10) / s.GetCount() / s.Count all work  (PascalCase — see note above)
```
