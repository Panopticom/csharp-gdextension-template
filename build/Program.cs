// build/Program.cs
//
// Usage (from repo root):
//   dotnet run --project build -- bootstrap
//   dotnet run --project build -- build [--debug]
//   dotnet run --project build -- clean
//   dotnet run --project build -- reset
//   dotnet run --project build -- rename <teamName> <projectName>

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

// new-fangled C# allows main function to just exist at the top level without being explicitly declared

string rootDirectory = Directory.GetCurrentDirectory();

// Sanity check: make sure we're running from the repo root.
if (!Directory.Exists(Path.Combine(rootDirectory, "src"))
 || !Directory.Exists(Path.Combine(rootDirectory, "godot-project"))) {

    Console.Error.WriteLine("error: run this tool from the repo root (the directory containing src/ and godot-project/).");
    return 1;
}

if (args.Length == 0)
    return PrintUsageAndFail();

return args[0] switch {
    "bootstrap" => Bootstrap(rootDirectory),
    "build" => Build(rootDirectory, Array.IndexOf(args, "--debug") >= 0),
    "run" => RunGame(rootDirectory, Array.IndexOf(args, "--debug") >= 0),
    "run-editor-scene" => OpenEditor(rootDirectory, Array.IndexOf(args, "--debug") >= 0),
    "clean" => Clean(rootDirectory),
    "reset" => Reset(rootDirectory),
    "rename" => Rename(rootDirectory, args),
    _ => PrintUsageAndFail(),
};

#region Commands


static int Bootstrap(string repoRoot)
{
    string nugetSource = Environment.GetEnvironmentVariable("NUGET_SOURCE")
                      ?? Path.Combine(repoRoot, "nuget-packages");
    string godotDotnetDir = Environment.GetEnvironmentVariable("GODOT_DOTNET_DIR")
                         ?? Path.Combine(repoRoot, "git-submodules", "godot-dotnet");
    string rid = GetRuntimeId();

    Info($"==> Target RID: {rid}");

    // ── Submodule ──────────────────────────────────────────────────────────────
    // In a git submodule, .git is a file (not a directory) containing "gitdir: ../../.git/modules/..."
    string godotDotnetGit = Path.Combine(godotDotnetDir, ".git");
    if (Directory.Exists(godotDotnetGit) || File.Exists(godotDotnetGit))
    {
        Info($"\n==> godot-dotnet already present at {RelativePath(repoRoot, godotDotnetDir)} (skipping)");
    }
    else
    {
        // Remove placeholder directory so git can clone into it.
        if (Directory.Exists(godotDotnetDir))
            Directory.Delete(godotDotnetDir, recursive: true);

        string gitmodules = Path.Combine(repoRoot, ".gitmodules");
        bool alreadyRegistered = File.Exists(gitmodules) &&
                                 File.ReadAllText(gitmodules).Contains("godot-dotnet");

        if (alreadyRegistered)
        {
            Info("\n==> godot-dotnet submodule already registered (running update --init)");
            Run("git", ["-C", repoRoot, "submodule", "update", "--init", "git-submodules/godot-dotnet"]);
        }
        else
        {
            Info("\n==> Adding raulsntos/godot-dotnet as submodule at git-submodules/godot-dotnet ...");
            Run("git", ["-C", repoRoot, "submodule", "add",
                "https://github.com/raulsntos/godot-dotnet.git",
                "git-submodules/godot-dotnet"]);
        }
    }

    // ── Patch global.json ─────────────────────────────────────────────────────
    // godot-dotnet's build scripts use Arcade, which reads tools.dotnet from global.json
    // and tries to install that exact SDK version into .dotnet/. After just reset that
    // folder is gone, and the version (e.g. 9.0.200) may not match what's installed.
    // We rewrite global.json to use the system-installed SDK version and remove the
    // paths constraint so the build uses the system SDK instead of downloading its own.
    string globalJson = Path.Combine(godotDotnetDir, "global.json");
    if (File.Exists(globalJson)) {
        string installedSdk = Capture("dotnet", ["--version"]).Trim();
        string gjContent = File.ReadAllText(globalJson);
        string gjPatched = Regex.Replace(gjContent, @",?\s*""paths""\s*:\s*\[[^\]]*\]", "");
        gjPatched = Regex.Replace(gjPatched, @"""dotnet""\s*:\s*""[^""]+""", $@"""dotnet"": ""{installedSdk}""");
        gjPatched = Regex.Replace(gjPatched, @"""version""\s*:\s*""[^""]+""", $@"""version"": ""{installedSdk}""");
        if (gjPatched != gjContent) {
            File.WriteAllText(globalJson, gjPatched);
            Info($"    Patched: global.json (SDK {installedSdk}, removed paths constraint)");
        }
        else {
            Info("    Already patched: global.json");
        }
    }

    // ── Register local NuGet source ────────────────────────────────────────────
    Directory.CreateDirectory(nugetSource);
    string listOutput = Capture("dotnet", ["nuget", "list", "source"]);
    if (!listOutput.Contains("godot-dotnet-local"))
    {
        Info($"\n==> Registering local NuGet source: {RelativePath(repoRoot, nugetSource)}");
        Run("dotnet", ["nuget", "add", "source", nugetSource, "--name", "godot-dotnet-local"]);
    }
    else if (!listOutput.Contains(nugetSource))
    {
        Info($"\n==> Updating local NuGet source path to: {RelativePath(repoRoot, nugetSource)}");
        Run("dotnet", ["nuget", "update", "source", "godot-dotnet-local", "--source", nugetSource]);
    }
    else
    {
        Info("==> Local NuGet source already registered.");
    }

    // ── Build NuGet packages ───────────────────────────────────────────────────
    Info("\n==> Building godot-dotnet NuGet packages (takes ~2-5 min)...");
    string buildScript = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? Path.Combine(godotDotnetDir, "build.cmd")
        : Path.Combine(godotDotnetDir, "build.sh");

    // Arcade's build scripts pass -warnaserror to MSBuild by default, which turns every warning in
    // godot-dotnet's own code into an error (and a TreatWarningsAsErrors property can't undo it).
    // Turning that off lets the unmodified sources build; their warnings don't affect the packages.
    // build.ps1 takes it as a [bool] (0), build.sh as 'true' or 'false'.
    string[] buildArgs = [
        "-warnAsError", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "0" : "false",
        "--productBuild",
        "--pushNupkgsLocal", nugetSource,
        "/p:GenerateGodotBindings=true",
    ];

    // Point Arcade's SDK installer at the system dotnet root so it finds the already-
    // installed SDK instead of trying to download its own copy into .dotnet/.
    string? dotnetRoot = FindDotnetRoot();
    var godotDotnetBuildEnv = dotnetRoot is not null
        ? new Dictionary<string, string> { ["DOTNET_INSTALL_DIR"] = dotnetRoot }
        : null;

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        Run("cmd", ["/c", buildScript, .. buildArgs], workingDirectory: godotDotnetDir, envOverrides: godotDotnetBuildEnv);
    else
        Run("bash", [buildScript, .. buildArgs], workingDirectory: godotDotnetDir, envOverrides: godotDotnetBuildEnv);

    // ── Ask for Godot executable path ─────────────────────────────────────────
    (_, _, string? existingGodotPath) = LoadConfig(repoRoot);
    string? godotExe = existingGodotPath ?? FindGodot();
    if (godotExe is not null) {
        Info($"\n==> Godot executable: {godotExe} (already configured, skipping)");
    }
    else {
        Console.WriteLine();
        Console.WriteLine("==> Where is your Godot executable?");
        Console.WriteLine("    Paste the full path to your Godot engine executable and press Enter.");
        Console.WriteLine("    This is saved to build/build.config and used by 'just run'.");
        Console.WriteLine("    (Press Enter to skip — you can set GODOT4 env var instead.)");
        Console.Write("    Path: ");
        string? input = Console.ReadLine()?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(input)) {
            if (!File.Exists(input)) {
                Console.Error.WriteLine($"  warning: file not found: {input}");
                Console.Error.WriteLine($"           Saved anyway — fix the path in build/build-private.config if needed.");
            }
            else {
                Console.WriteLine($"  Saved: godotPath={input}");
            }
            SavePrivateConfig(repoRoot, input);
        }
        else {
            Console.WriteLine("  Skipped. Set the GODOT4 environment variable or add godot4/godot to your PATH.");
        }
    }

    Console.WriteLine();
    Info("==> Bootstrap complete. Run 'just build' to compile the extension, or 'just run' to build and launch.", ConsoleColor.Green);
    return 0;
}


static int Build(string repoRoot, bool debug) {
    (string teamName, string projectName, _) = LoadConfig(repoRoot);

    if (projectName.Contains('-')) {
        Console.Error.WriteLine($"error: projectName '{projectName}' contains '-', which is not valid in C# identifiers.");
        Console.Error.WriteLine($"       The project name becomes the assembly name and C# namespace.");
        Console.Error.WriteLine($"       Rename the project using underscores instead:  just rename {teamName} {projectName.Replace('-', '_')}");
        return 1;
    }

    string rid        = GetRuntimeId();
    string config     = debug ? "Debug" : "Release";
    string srcDir     = Path.Combine(repoRoot, "src");
    string publishDir = Path.Combine(srcDir, "publish_output", rid);

    (string _, string destBinaryName, string binSubDir) = GetBinaryNames(rid, projectName);
    string godotBinDir = Path.Combine(repoRoot, "godot-project", "addons", teamName, projectName, "bin");
    string destBinary  = Path.Combine(godotBinDir, binSubDir, destBinaryName);

    string configStamp = Path.Combine(srcDir, "obj", ".last-build-config");
    string? lastConfig = File.Exists(configStamp) ? File.ReadAllText(configStamp).Trim() : null;

    string? changedFile = FindChangedFile(destBinary, srcDir);
    if (changedFile is null && lastConfig == config) {
        Info($"==> {teamName}/{projectName} is up to date.", ConsoleColor.Green);
        return 0;
    }

    if (changedFile is not null)
        Info($"==> {RelativePath(repoRoot, changedFile)} changed — rebuilding {teamName}/{projectName} ({config})", ConsoleColor.Cyan);
    else
        Info($"==> Config changed ({lastConfig} → {config}) — rebuilding {teamName}/{projectName}", ConsoleColor.Cyan);

    // On Windows, strip Git's usr/bin from PATH before invoking NativeAOT publish.
    // Git Bash adds C:\Program Files\Git\usr\bin to PATH, which contains a GNU `link`
    // (coreutils file-linker) that shadows MSVC's link.exe and breaks NativeAOT linking.
    IReadOnlyDictionary<string, string>? publishEnv = null;
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
        string original = Environment.GetEnvironmentVariable("PATH") ?? "";
        string cleaned  = string.Join(Path.PathSeparator.ToString(),
            original.Split(Path.PathSeparator)
                    .Where(p => !p.Contains(@"\Git\usr\bin", StringComparison.OrdinalIgnoreCase) &&
                                !p.Contains( "/Git/usr/bin", StringComparison.OrdinalIgnoreCase)));
        if (cleaned != original)
            publishEnv = new Dictionary<string, string> { ["PATH"] = cleaned };
    }

    Run("dotnet", [
        "publish", $"{projectName}.csproj",
        "-r", rid,
        "-c", config,
        "--self-contained", "true",
        "-p:PublishAot=true",
        "-p:NativeLib=Shared",
        "-p:SkipBuildScript=true",
        "-o", publishDir,
    ], workingDirectory: srcDir, envOverrides: publishEnv);

    // Determine OS-specific paths.
    (string srcBinary, _, _) = GetBinaryNames(rid, projectName);

    string godotBin = godotBinDir;
    string src  = Path.Combine(publishDir, srcBinary);
    string dest = destBinary;

    if (!File.Exists(src)) {
        Console.Error.WriteLine($"error: binary not found: {RelativePath(repoRoot, src)}");
        return 1;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
    File.Copy(src, dest, overwrite: true);

    // Copy VC++ runtime DLLs on Windows so exported games include them.
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
        string system32 = Environment.SystemDirectory;
        foreach (string dll in new[] { "vcruntime140.dll", "vcruntime140_1.dll" })
        {
            string dllSrc  = Path.Combine(system32, dll);
            string dllDest = Path.Combine(godotBin, binSubDir, dll);
            if (File.Exists(dllSrc))
                File.Copy(dllSrc, dllDest, overwrite: true);
            else
                Console.Error.WriteLine($"warning: VC++ runtime DLL not found, skipping: {dll}");
        }
    }

    if (debug) {
        string pdbName = Path.ChangeExtension(srcBinary, ".pdb");
        string pdbSrc  = Path.Combine(publishDir, pdbName);
        string pdbDest = Path.Combine(godotBin, binSubDir, Path.ChangeExtension(destBinary, ".pdb"));
        if (File.Exists(pdbSrc))
        {
            File.Copy(pdbSrc, pdbDest, overwrite: true);
            Info($"==> PDB:    {RelativePath(repoRoot, pdbDest)}", ConsoleColor.DarkCyan);
        }
    }

    // Remove staging directory — binary is now in its final location.
    if (Directory.Exists(Path.Combine(srcDir, "publish_output")))
        Directory.Delete(Path.Combine(srcDir, "publish_output"), recursive: true);

    // Record which config was just built so the next run can detect a Debug/Release switch.
    Directory.CreateDirectory(Path.GetDirectoryName(configStamp)!);
    File.WriteAllText(configStamp, config);

    Console.WriteLine();
    Info($"==> Done. Binary: {RelativePath(repoRoot, dest)}", ConsoleColor.Green);
    Console.WriteLine();
    Console.WriteLine("    Open godot-project\\ in Godot 4.x and press F5, or run 'just run' to launch directly.");
    return 0;
}


static int RunGame(string repoRoot, bool debug) {
    int buildResult = Build(repoRoot, debug);
    if (buildResult != 0)
        return buildResult;

    (_, _, string? savedGodotPath) = LoadConfig(repoRoot);
    string? godotExe = FindGodot(savedGodotPath);
    if (godotExe is null) {
        Console.Error.WriteLine("error: Godot executable not found.");
        Console.Error.WriteLine("       Set the GODOT4 environment variable to the path of your Godot executable,");
        Console.Error.WriteLine("       or make sure 'godot4' or 'godot' is on your PATH.");
        return 1;
    }

    string projectDir = Path.Combine(repoRoot, "godot-project");
    string mainScene = ReadMainScene(projectDir) ?? "res://main.tscn";

    Info($"==> Running {mainScene}", ConsoleColor.Cyan);

    Process.Start(new ProcessStartInfo {
        FileName = godotExe,
        Arguments = $"--path \"{projectDir}\" {mainScene}",
        UseShellExecute = false,
    });

    return 0;
}

static int OpenEditor(string repoRoot, bool debug) {
    int buildResult = Build(repoRoot, debug);
    if (buildResult != 0)
        return buildResult;

    (_, _, string? savedGodotPath) = LoadConfig(repoRoot);
    string? godotExe = FindGodot(savedGodotPath);
    if (godotExe is null) {
        Console.Error.WriteLine("error: Godot executable not found.");
        Console.Error.WriteLine("       Set the GODOT4 environment variable to the path of your Godot executable,");
        Console.Error.WriteLine("       or make sure 'godot4' or 'godot' is on your PATH.");
        return 1;
    }

    string projectDir = Path.Combine(repoRoot, "godot-project");

    if (!IsGodotEditorRunning(godotExe)) {
        Console.Error.WriteLine("error: Godot editor is not running.");
        Console.Error.WriteLine("       Open the project in the Godot editor first, then run this command.");
        return 1;
    }

    string? currentScene = ReadEditorCurrentScene(projectDir);
    if (currentScene is null) {
        Console.Error.WriteLine("error: The Godot editor has no scene open.");
        Console.Error.WriteLine("       Open a scene in the editor, then run this command.");
        return 1;
    }

    Info($"==> Running editor scene: {currentScene}", ConsoleColor.Cyan);

    Process.Start(new ProcessStartInfo {
        FileName = godotExe,
        Arguments = $"--path \"{projectDir}\" {currentScene}",
        UseShellExecute = false,
    });

    return 0;
}

// Reads the currently active scene tab from the Godot editor's saved layout.
// Godot writes this to .godot/editor/editor_layout.cfg on every save/switch.
static string? ReadEditorCurrentScene(string godotProjectDir) {
    string layoutPath = Path.Combine(godotProjectDir, ".godot", "editor", "editor_layout.cfg");
    if (!File.Exists(layoutPath))
        return null;

    bool inEditorNode = false;
    string? currentScene = null;

    foreach (string line in File.ReadAllLines(layoutPath)) {
        if (line.Trim() == "[EditorNode]") { inEditorNode = true; continue; }
        if (line.StartsWith('[')) { inEditorNode = false; continue; }
        if (!inEditorNode) continue;

        int eq = line.IndexOf('=');
        if (eq < 0) continue;
        string key = line[..eq].Trim();
        string value = line[(eq + 1)..].Trim();

        if (key == "current_scene") {
            var match = Regex.Match(value, @"""(res://[^""]+)""");
            if (match.Success)
                currentScene = match.Groups[1].Value;
        }
    }

    return currentScene;
}

static string? ReadMainScene(string godotProjectDir) {
    string projectGodot = Path.Combine(godotProjectDir, "project.godot");
    if (!File.Exists(projectGodot))
        return null;

    foreach (string line in File.ReadAllLines(projectGodot)) {
        // Matches: run/main_scene="res://main.tscn"
        var match = Regex.Match(line, @"^run/main_scene=""([^""]+)""");
        if (match.Success)
            return match.Groups[1].Value;
    }
    return null;
}

static string? FindGodot(string? configuredPath = null) {
    // 1. Saved in build.config.
    if (configuredPath is not null && File.Exists(configuredPath))
        return configuredPath;

    // 2. Explicit env var.
    string? fromEnv = Environment.GetEnvironmentVariable("GODOT4")
                   ?? Environment.GetEnvironmentVariable("GODOT");
    if (fromEnv is not null && File.Exists(fromEnv))
        return fromEnv;

    // 3. Common executable names on PATH.
    string[] candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? ["godot4.exe", "godot.exe"]
        : ["godot4", "godot"];

    string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
    foreach (string dir in pathEnv.Split(Path.PathSeparator)) {
        foreach (string name in candidates) {
            string full = Path.Combine(dir, name);
            if (File.Exists(full))
                return full;
        }
    }

    return null;
}


// Returns the first source file newer than the binary, or null if everything is up to date.
// Returns the binary path itself if it doesn't exist yet.
static string? FindChangedFile(string destBinary, string srcDir) {
    if (!File.Exists(destBinary))
        return destBinary;

    DateTime binaryTime = File.GetLastWriteTimeUtc(destBinary);

    string objDir = Path.Combine(srcDir, "obj");
    string binDir = Path.Combine(srcDir, "bin");
    return Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !f.StartsWith(objDir, StringComparison.OrdinalIgnoreCase)
                             && !f.StartsWith(binDir, StringComparison.OrdinalIgnoreCase))
                    .Concat(Directory.EnumerateFiles(srcDir, "*.csproj", SearchOption.TopDirectoryOnly))
                    .FirstOrDefault(f => File.GetLastWriteTimeUtc(f) > binaryTime);
}

static string? FindDotnetRoot() {
    string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
    string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
    foreach (string dir in pathEnv.Split(Path.PathSeparator)) {
        if (File.Exists(Path.Combine(dir.Trim(), exeName)))
            return dir.Trim();
    }
    return null;
}

static bool IsGodotEditorRunning(string? godotExe) {
    // Check for the configured/discovered executable by name first.
    if (godotExe is not null) {
        string processName = Path.GetFileNameWithoutExtension(godotExe);
        if (Process.GetProcessesByName(processName).Length > 0)
            return true;
    }
    // Fall back to any process with "godot" in the name.
    return Process.GetProcesses()
                  .Any(p => p.ProcessName.Contains("godot", StringComparison.OrdinalIgnoreCase));
}

static int Clean(string repoRoot) {
    (string teamName, string projectName, _) = LoadConfig(repoRoot);

    Info("==> Cleaning unnecessary build artifacts...", ConsoleColor.Cyan);
    Console.WriteLine("    Safe to run at any time. Caches that speed up rebuilds are preserved.");
    Console.WriteLine();

    // Intermediate NativeAOT staging directory. The DLL has already been
    // copied to godot-project/addons/.../bin/ by the build command.
    RemoveIfExists(
        Path.Combine(repoRoot, "src", "publish_output"),
        @"src\publish_output  (NativeAOT staging — binary already in godot-project)");

    // NuGet packages produced by the godot-dotnet productBuild.
    // They were already pushed to nuget-packages\ by --pushNupkgsLocal, so
    // this directory is just a duplicate that wastes disk space.
    RemoveIfExists(
        Path.Combine(repoRoot, "git-submodules", "godot-dotnet", "artifacts", "packages"),
        @"git-submodules\godot-dotnet\artifacts\packages  (NuGet output — already in nuget-packages\)");

    // Compiled extension binaries in the Godot project — produced by build/debug,
    // regenerated on the next build. Platform folders and .gitkeep files are preserved.
    CleanBinaryFiles(
        Path.Combine(repoRoot, "godot-project", "addons", teamName, projectName, "bin"),
        $@"godot-project\addons\{teamName}\{projectName}\bin\");

    Console.WriteLine();
    Info("==> Done.", ConsoleColor.Green);
    Console.WriteLine();
    Console.WriteLine("    Preserved (removing these would slow down the next build):");
    Console.WriteLine(@"      src\obj\                                    — MSBuild incremental state");
    Console.WriteLine(@"      git-submodules\godot-dotnet\artifacts\bin\  — built godot-dotnet tool binaries");
    Console.WriteLine(@"      git-submodules\godot-dotnet\artifacts\obj\  — godot-dotnet intermediate objects");
    Console.WriteLine(@"      nuget-packages\                             — local NuGet feed (Godot.Bindings etc.)");
    Console.WriteLine();
    Console.WriteLine("    To wipe everything and start fresh, run:  just reset");
    return 0;
}


static int Reset(string repoRoot) {
    Info("==> Resetting project to a clean state...", ConsoleColor.Cyan);
    Console.WriteLine("    All build caches will be removed. Run 'just bootstrap' afterwards.");
    Console.WriteLine();

    // Everything clean removes.
    Clean(repoRoot);

    Console.WriteLine();
    Info("==> Removing build caches...", ConsoleColor.Cyan);
    Console.WriteLine();

    // MSBuild incremental state for the extension and build tool projects.
    RemoveIfExists(
        Path.Combine(repoRoot, "src", "obj"),
        @"src\obj  (MSBuild incremental state)");
    RemoveIfExists(
        Path.Combine(repoRoot, "src", "bin"),
        @"src\bin  (managed build output)");
    RemoveIfExists(
        Path.Combine(repoRoot, "build", "obj"),
        @"build\obj  (build tool MSBuild incremental state)");
    RemoveIfExists(
        Path.Combine(repoRoot, "build", "bin"),
        @"build\bin  (compiled build tool)");

    // Godot editor cache.
    RemoveIfExists(
        Path.Combine(repoRoot, "godot-project", ".godot"),
        @"godot-project\.godot  (Godot editor cache)");

    // godot-dotnet build artifacts.
    RemoveIfExists(
        Path.Combine(repoRoot, "git-submodules", "godot-dotnet", "artifacts", "bin"),
        @"git-submodules\godot-dotnet\artifacts\bin  (godot-dotnet tool binaries)");
    RemoveIfExists(
        Path.Combine(repoRoot, "git-submodules", "godot-dotnet", "artifacts", "obj"),
        @"git-submodules\godot-dotnet\artifacts\obj  (godot-dotnet intermediate objects)");

    // Local NuGet feed — clear packages but preserve the .gitkeep placeholder.
    string nugetSource = Path.Combine(repoRoot, "nuget-packages");

    if (Directory.Exists(nugetSource)) {
        int removed = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(nugetSource)) {
            if (Path.GetFileName(entry) == ".gitkeep") continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
            removed++;
        }
        Console.WriteLine(removed > 0
            ? $"  Removed:      nuget-packages\\  (local NuGet feed — {removed} item(s) deleted, .gitkeep preserved)"
            : $"  Already clean: nuget-packages\\");
    }

    Console.WriteLine();
    Info("==> Reset complete.", ConsoleColor.Green);
    Console.WriteLine("    Run 'just bootstrap' to rebuild everything from scratch.");
    return 0;
}


static int Rename(string repoRoot, string[] args) {
    if (args.Length < 3) {
        Console.Error.WriteLine("error: rename requires two arguments: <teamName> <projectName>");
        Console.Error.WriteLine("  e.g. just rename acme mygame");
        return 1;
    }

    string newTeam = args[1];
    string newProject = args[2];

    if (!IsValidName(newTeam)) {
        Console.Error.WriteLine($"error: invalid teamName '{newTeam}' — use letters, digits, hyphens, and underscores only");
        return 1;
    }
    if (!IsValidName(newProject)) {
        Console.Error.WriteLine($"error: invalid projectName '{newProject}' — use letters, digits, and underscores only");
        return 1;
    }
    if (newProject.Contains('-')) {
        Console.Error.WriteLine($"error: projectName '{newProject}' contains '-', which is not valid in C# identifiers.");
        Console.Error.WriteLine($"       The project name becomes the assembly name and C# namespace.");
        Console.Error.WriteLine($"       Use PascalCase or underscores instead, e.g.:");
        Console.Error.WriteLine($"         just rename {newTeam} {ToPascalCase(newProject)}");
        Console.Error.WriteLine($"         just rename {newTeam} {newProject.Replace('-', '_')}");
        return 1;
    }

    (string oldTeam, string oldProject, string? savedGodotPath) = LoadConfig(repoRoot);

    if (oldTeam == newTeam && oldProject == newProject){
        Info($"==> Already named {newTeam}/{newProject}, nothing to do.", ConsoleColor.Green);
        return 0;
    }

    Info($"==> Renaming {oldTeam}/{oldProject}  →  {newTeam}/{newProject}", ConsoleColor.Cyan);
    Console.WriteLine();

    string addonsDir = Path.Combine(repoRoot, "godot-project", "addons");
    string oldAddon = Path.Combine(addonsDir, oldTeam, oldProject);
    string newAddon = Path.Combine(addonsDir, newTeam, newProject);

    // ── Move addon directory ───────────────────────────────────────────────────
    if (!Directory.Exists(oldAddon)) {
        Console.Error.WriteLine($"error: addon directory not found: {RelativePath(repoRoot, oldAddon)}");
        Console.Error.WriteLine("       Has the project already been partially renamed?");
        return 1;
    }

    if (Directory.Exists(newAddon)) {
        Console.Error.WriteLine($"error: destination already exists: {RelativePath(repoRoot, newAddon)}");
        return 1;
    }

    Directory.CreateDirectory(Path.Combine(addonsDir, newTeam));
    try {
        Directory.Move(oldAddon, newAddon);
    } catch (IOException ex) when (ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase)
                                || ex.Message.Contains("Access", StringComparison.OrdinalIgnoreCase)) {
        Console.Error.WriteLine($"error: could not move addon directory — access denied.");
        Console.Error.WriteLine($"       Close the Godot editor (and any file explorer windows showing");
        Console.Error.WriteLine($"       godot-project/addons/) then try again.");
        // Clean up the empty team directory we just created if it is now empty.
        string createdTeamDir = Path.Combine(addonsDir, newTeam);
        if (Directory.Exists(createdTeamDir) && !Directory.EnumerateFileSystemEntries(createdTeamDir).Any())
            Directory.Delete(createdTeamDir);
        return 1;
    }

    Console.WriteLine($"  Moved:   godot-project/addons/{oldTeam}/{oldProject}/");
    Console.WriteLine($"       →   godot-project/addons/{newTeam}/{newProject}/");

    // Binaries built under the old name came along with the directory, and nothing would ever load or replace them.
    CleanBinaryFiles(Path.Combine(newAddon, "bin"), $"godot-project/addons/{newTeam}/{newProject}/bin/  (binaries built under the old name)");

    // Remove the old team directory if it is now empty.
    string oldTeamDir = Path.Combine(addonsDir, oldTeam);
    if (Directory.Exists(oldTeamDir) && !Directory.EnumerateFileSystemEntries(oldTeamDir).Any()) {
        Directory.Delete(oldTeamDir);
        Console.WriteLine($"  Removed: godot-project/addons/{oldTeam}/  (now empty)");
    }

    // ── Rename .gdextension file ───────────────────────────────────────────────
    string oldGdext = Path.Combine(newAddon, $"{oldProject}.gdextension");
    string newGdext = Path.Combine(newAddon, $"{newProject}.gdextension");

    if (File.Exists(oldGdext)) {
        File.Move(oldGdext, newGdext);
        Console.WriteLine($"  Renamed: {oldProject}.gdextension  →  {newProject}.gdextension");
    }

    // Godot keeps a resource's UID in a .uid file beside it, which has to follow the file so references by UID keep working.
    MoveUidFile(oldGdext, newGdext);

    // ── Update paths inside the .gdextension file ──────────────────────────────
    if (File.Exists(newGdext)) {
        string oldPath = $"addons/{oldTeam}/{oldProject}/";
        string newPath = $"addons/{newTeam}/{newProject}/";
        string content = File.ReadAllText(newGdext);
        string updated = content.Replace(oldPath, newPath);

        if (updated != content) {
            File.WriteAllText(newGdext, updated);
            int count = (content.Length - updated.Replace(newPath, oldPath).Length) / (newPath.Length - oldPath.Length);
            Console.WriteLine($"  Updated: {newProject}.gdextension  ({content.Split(oldPath).Length - 1} path(s) rewritten)");
        }
    }

    // ── Rename src/<project>.csproj and update its TrimmerRootAssembly + RootNamespace ──
    string oldCsproj = Path.Combine(repoRoot, "src", $"{oldProject}.csproj");
    string newCsproj = Path.Combine(repoRoot, "src", $"{newProject}.csproj");

    if (File.Exists(oldCsproj)) {
        string csprojContent = File.ReadAllText(oldCsproj);
        string csprojUpdated = csprojContent
            .Replace(
                $"<TrimmerRootAssembly Include=\"{oldProject}\" />",
                $"<TrimmerRootAssembly Include=\"{newProject}\" />")
            .Replace(
                $"<RootNamespace>{oldProject}</RootNamespace>",
                $"<RootNamespace>{newProject}</RootNamespace>");
        File.WriteAllText(oldCsproj, csprojUpdated);
        File.Move(oldCsproj, newCsproj);
        Console.WriteLine($"  Renamed: src/{oldProject}.csproj  →  {newProject}.csproj");
    }

    // ── Update namespace in all src .cs files (replaces any file-level namespace declaration) ──
    foreach (string csFile in Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs")) {
        string csContent = File.ReadAllText(csFile);
        string csUpdated = Regex.Replace(csContent,
            @"(?m)^namespace [A-Za-z_][A-Za-z0-9_]*;",
            $"namespace {newProject};");
        if (csUpdated != csContent) {
            File.WriteAllText(csFile, csUpdated);
            Console.WriteLine($"  Updated: src/{Path.GetFileName(csFile)}  (namespace → {newProject})");
        }
    }

    // ── Update binary filenames inside the .gdextension file ──────────────────
    if (File.Exists(newGdext)) {
        string content = File.ReadAllText(newGdext);
        // Replace the project name in binary filenames, e.g.:
        //   /old-project.x86_64.dll  →  /new-project.x86_64.dll
        //   /libold-project.arm64.so →  /libnew-project.arm64.so
        string updated = content
            .Replace($"/{oldProject}.", $"/{newProject}.")
            .Replace($"/lib{oldProject}.", $"/lib{newProject}.");
        if (updated != content) {
            File.WriteAllText(newGdext, updated);
            Console.WriteLine($"  Updated: {newProject}.gdextension  (binary filenames rewritten)");
        }
    }


    // ── Update solution project name and path ──────────────────────────────────
    string slnPath = Path.Combine(repoRoot, "MyGodotExtension.sln");
    if (File.Exists(slnPath)) {
        string slnContent = File.ReadAllText(slnPath);
        string slnUpdated = slnContent.Replace(
            $"= \"{oldProject}\", \"src\\{oldProject}.csproj\"",
            $"= \"{newProject}\", \"src\\{newProject}.csproj\"");
        if (slnUpdated != slnContent) {
            File.WriteAllText(slnPath, slnUpdated);
            Console.WriteLine("  Updated: MyGodotExtension.sln");
        }
    }

    // ── Update Godot project name ──────────────────────────────────────────────
    string godotProjectPath = Path.Combine(repoRoot, "godot-project", "project.godot");
    if (File.Exists(godotProjectPath)) {
        string godotContent = File.ReadAllText(godotProjectPath);
        string godotUpdated = Regex.Replace(godotContent,
            @"(?<=^config/name="")[^""]*(?="")",
            newProject,
            RegexOptions.Multiline);
        if (godotUpdated != godotContent) {
            File.WriteAllText(godotProjectPath, godotUpdated);
            Console.WriteLine($"  Updated: godot-project/project.godot  (config/name → \"{newProject}\")");
        }
    }

    // ── Update autoload test script (only while ExampleNode.cs still exists) ──
    string exampleNodePath = Path.Combine(repoRoot, "src", "ExampleNode.cs");
    string autoloadDir = Path.Combine(repoRoot, "godot-project", "autoload");
    if (File.Exists(exampleNodePath) && Directory.Exists(autoloadDir)) {
        string oldExtPath = $"res://addons/{oldTeam}/{oldProject}/{oldProject}.gdextension";
        string newExtPath = $"res://addons/{newTeam}/{newProject}/{newProject}.gdextension";

        foreach (string gdFile in Directory.EnumerateFiles(autoloadDir, "*.gd")) {
            string gdContent = File.ReadAllText(gdFile);

            // Match any res://addons/<team>/<project>/<project>.gdextension path.
            if (!Regex.IsMatch(gdContent, @"res://addons/[^/""]+/[^/""]+/[^/""]+\.gdextension"))
                continue;

            // Update the extension path and the autoload name in comments/prints.
            string oldScriptName = Path.GetFileNameWithoutExtension(gdFile);
            string newScriptName = $"{newProject}Test";
            string gdUpdated = Regex.Replace(gdContent,
                    @"res://addons/[^/""]+/[^/""]+/[^/""]+\.gdextension", newExtPath)
                .Replace($"[{oldScriptName}]", $"[{newScriptName}]")
                .Replace($"# {oldScriptName}.gd", $"# {newScriptName}.gd");

            // Rename the file.
            string newGdFile = Path.Combine(autoloadDir, $"{newScriptName}.gd");
            File.WriteAllText(gdFile, gdUpdated);
            if (newGdFile != gdFile) {
                File.Move(gdFile, newGdFile);
                MoveUidFile(gdFile, newGdFile);
            }

            // Update the autoload registration in project.godot.
            if (File.Exists(godotProjectPath)) {
                string pContent = File.ReadAllText(godotProjectPath);
                string pUpdated = pContent.Replace(
                    $"{oldScriptName}=\"*res://autoload/{oldScriptName}.gd\"",
                    $"{newScriptName}=\"*res://autoload/{newScriptName}.gd\"");
                if (pUpdated != pContent)
                    File.WriteAllText(godotProjectPath, pUpdated);
            }

            Console.WriteLine($"  Renamed: autoload/{oldScriptName}.gd  →  {newScriptName}.gd  (ext_path + autoload updated)");
        }
    }

    // ── Save updated config ────────────────────────────────────────────────────
    SaveConfig(repoRoot, newTeam, newProject, savedGodotPath);
    Console.WriteLine($"  Updated: build/build.config");

    Console.WriteLine();
    Info("==> Rename complete.", ConsoleColor.Green);
    Console.WriteLine($"    Run 'just build' to produce the binary at its new location.");
    return 0;
}

#endregion Commands

#region Helper Functions

static (string teamName, string projectName, string? godotPath) LoadConfig(string repoRoot) {
    string configPath = Path.Combine(repoRoot, "build", "build.config");
    string teamName = "my-group";
    string projectName = "MyProject";

    if (File.Exists(configPath)) {
        foreach (string line in File.ReadAllLines(configPath)) {
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (key == "teamName") teamName = value;
            if (key == "projectName") projectName = value;
        }
    }

    string? godotPath = LoadPrivateConfig(repoRoot);
    return (teamName, projectName, godotPath);
}

static void SaveConfig(string repoRoot, string teamName, string projectName, string? godotPath = null) {
    string configPath = Path.Combine(repoRoot, "build", "build.config");
    File.WriteAllText(configPath, $"teamName={teamName}\nprojectName={projectName}\n");
    if (godotPath is not null)
        SavePrivateConfig(repoRoot, godotPath);
}

static string? LoadPrivateConfig(string repoRoot) {
    string configPath = Path.Combine(repoRoot, "build", "build-private.config");
    if (!File.Exists(configPath))
        return null;

    foreach (string line in File.ReadAllLines(configPath)) {
        int eq = line.IndexOf('=');
        if (eq < 0) continue;
        if (line[..eq].Trim() == "godotPath")
            return line[(eq + 1)..].Trim();
    }
    return null;
}

static void SavePrivateConfig(string repoRoot, string godotPath) {
    string configPath = Path.Combine(repoRoot, "build", "build-private.config");
    File.WriteAllText(configPath, $"godotPath={godotPath}\n");
}

static string ToPascalCase(string name) =>
    string.Concat(name.Split('-').Select(word =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));

static bool IsValidName(string name) =>
    !string.IsNullOrEmpty(name) &&
    Regex.IsMatch(name, @"^[a-zA-Z0-9_-]+$");

/// <returns> The operating system and cpu arcitechture combo like 'win-x64' or 'linux-arm64'. </returns>
// need to mark that this method can throw
static string GetRuntimeId() {
    string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" :
                throw new PlatformNotSupportedException("Unsupported OS");

    string arch = RuntimeInformation.ProcessArchitecture switch {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        var a => throw new PlatformNotSupportedException($"Unsupported architecture: {a}"),
    };

    return $"{os}-{arch}";
}

// Returns (sourceBinaryName, destBinaryName, godotBinSubDir) for the current platform/arch.
static (string src, string dest, string subDir) GetBinaryNames(string rid, string projectName) {
    string archSuffix = rid.EndsWith("arm64") ? "arm64" : "x86_64";

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        return ($"{projectName}.dll", $"{projectName}.{archSuffix}.dll", "windows");

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        return ($"lib{projectName}.so", $"lib{projectName}.{archSuffix}.so", "linux");

    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        return ($"lib{projectName}.dylib", $"lib{projectName}.{archSuffix}.dylib", "macos");

    throw new PlatformNotSupportedException("Unsupported OS");
}

static void CleanBinaryFiles(string binDir, string label) {
    string[] binaryExtensions = [".dll", ".pdb", ".so", ".dylib", ".a"];

    if (!Directory.Exists(binDir)) {
        Console.WriteLine($"  Already clean: {label}");
        return;
    }

    int removed = 0;
    try {
        foreach (string file in Directory.EnumerateFiles(binDir, "*", SearchOption.AllDirectories)) {
            if (!binaryExtensions.Contains(Path.GetExtension(file))) continue;
            File.Delete(file);
            removed++;
        }

        // Remove any platform subdirectories that are now empty.
        foreach (string dir in Directory.EnumerateDirectories(binDir)) {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }

        Console.WriteLine(removed > 0
            ? $"  Cleaned:      {label}  ({removed} file(s) removed)"
            : $"  Already clean: {label}");
    }
    catch (UnauthorizedAccessException) {
        Console.Error.WriteLine($"  Skipped:      {label}");
        Console.Error.WriteLine($"    Access denied — close Godot and run 'just clean' again.");
    }
}

/// <summary> Moves the .uid file Godot keeps beside a resource, if it has one, so the resource keeps its UID under its new name. </summary>
static void MoveUidFile(string oldPath, string newPath) {
    string oldUid = oldPath + ".uid";
    string newUid = newPath + ".uid";
    if (!File.Exists(oldUid) || oldUid == newUid)
        return;

    File.Move(oldUid, newUid, overwrite: true);
    Console.WriteLine($"  Renamed: {Path.GetFileName(oldUid)}  →  {Path.GetFileName(newUid)}");
}

static void RemoveIfExists(string path, string label) {
    try {
        if (Directory.Exists(path)) {
            Directory.Delete(path, recursive: true);
            Console.WriteLine($"  Removed:      {label}");
        }
        else if (File.Exists(path)) {
            File.Delete(path);
            Console.WriteLine($"  Removed:      {label}");
        }
        else {
            Console.WriteLine($"  Already clean: {label}");
        }
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) {
        // File may be locked by Godot, antivirus, or (for build\bin) the running build tool itself.
        Console.Error.WriteLine($"  Skipped:      {label}");
        Console.Error.WriteLine($"    File in use — delete manually if needed: {path}");
    }
}

static void Run(string executable, IEnumerable<string> arguments, string? workingDirectory = null,
    IReadOnlyDictionary<string, string>? envOverrides = null)
{
    var startInfo = new ProcessStartInfo(executable) {
        UseShellExecute = false,
        WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
    };
    foreach (string arg in arguments)
        startInfo.ArgumentList.Add(arg);
    if (envOverrides != null)
        foreach (var (k, v) in envOverrides)
            startInfo.Environment[k] = v;

    using var process = Process.Start(startInfo) ?? throw new Exception($"Failed to start: {executable}");
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new Exception($"Process exited with code {process.ExitCode}: {executable}");
}

static string Capture(string executable, IEnumerable<string> arguments) {
    var startInfo = new ProcessStartInfo(executable) {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (string arg in arguments)
        startInfo.ArgumentList.Add(arg);

    using var proc = Process.Start(startInfo) ?? throw new Exception($"Failed to start: {executable}");
    string output = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();
    return output;
}

static string RelativePath(string repoRoot, string path) => Path.GetRelativePath(repoRoot, path);

static void Info(string message, ConsoleColor color = ConsoleColor.Cyan) {
    ConsoleColor prev = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ForegroundColor = prev;
}

static int PrintUsageAndFail() {
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project build -- bootstrap                    # first-time setup");
    Console.WriteLine("  dotnet run --project build -- build                        # release build");
    Console.WriteLine("  dotnet run --project build -- build --debug                # debug build");
    Console.WriteLine("  dotnet run --project build -- clean                        # remove stale artifacts");
    Console.WriteLine("  dotnet run --project build -- reset                        # wipe all caches (requires re-bootstrap)");
    Console.WriteLine("  dotnet run --project build -- rename <team> <project>      # rename the addon");
    return 1;
}

#endregion Helper Functions
