// GameRefsClosure — computes the transitive assembly-reference closure of one or more root
// assemblies against a directory of candidate DLLs, reading only PE/ECMA-335 metadata
// (System.Reflection.Metadata / System.Reflection.PortableExecutable) — never loads or executes
// any of the candidate assemblies. Used by tools/sync-game-refs.ps1 (WP2 §B.WP2.1 / LEAD OVERRIDE O2)
// to decide exactly which DLLs under D:/SPT41/EscapeFromTarkov_Data/Managed must be copied into
// UnityProject/Assets/ArenaMapPort/Editor/GameRefs/ so that Assembly-CSharp.dll (and
// Assembly-CSharp-firstpass.dll) resolve cleanly in the Unity Editor — computed from real
// metadata, not guessed.
//
// Usage:
//   GameRefsClosure <ManagedDir> <RootAssemblyNames semicolon-separated, no .dll> <OutFile>
//
// Output: <OutFile> — one line per assembly found in the transitive closure that is physically
// present as a .dll under <ManagedDir>, tab-separated "<AssemblyName>\t<FullPath>", sorted.
// Also writes <OutFile>.edges.tsv — every "<from>\t<to>" reference edge walked, for audit.
// Note: the closure includes assemblies later excluded from copying by sync-game-refs.ps1's own
// filter (UnityEngine*/UnityEditor*/mscorlib/netstandard/System*/Mono.*) — this tool's job is only
// to compute the full, correct reference graph; exclusion is the PowerShell script's job.

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: GameRefsClosure <ManagedDir> <RootAssemblyNames;semicolon;separated> <OutFile>");
    return 1;
}

string managedDir = args[0];
string[] roots = args[1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
string outFile = args[2];

if (!Directory.Exists(managedDir))
{
    Console.Error.WriteLine($"ERROR: managed dir does not exist: {managedDir}");
    return 1;
}

// Index every candidate DLL by its real assembly name (read from metadata, not the filename —
// filenames match assembly names for every DLL observed in this Managed folder, but we don't
// assume that).
var filesByAsmName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var duplicateAsmNames = new List<string>();
foreach (var path in Directory.EnumerateFiles(managedDir, "*.dll", SearchOption.TopDirectoryOnly))
{
    string? asmName = TryGetAssemblyName(path);
    if (asmName == null)
    {
        Console.Error.WriteLine($"WARNING: could not read assembly metadata (skipped): {path}");
        continue;
    }
    if (filesByAsmName.ContainsKey(asmName))
    {
        duplicateAsmNames.Add($"{asmName}: {filesByAsmName[asmName]} vs {path}");
        continue; // keep the first one found; flagged below
    }
    filesByAsmName[asmName] = path;
}

if (duplicateAsmNames.Count > 0)
{
    Console.Error.WriteLine("WARNING: duplicate assembly names found in managed dir (kept first occurrence):");
    foreach (var d in duplicateAsmNames) Console.Error.WriteLine("  " + d);
}

var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var queue = new Queue<string>();
foreach (var r in roots)
{
    string name = Path.GetFileNameWithoutExtension(r);
    if (filesByAsmName.ContainsKey(name))
    {
        if (visited.Add(name)) queue.Enqueue(name);
    }
    else
    {
        Console.Error.WriteLine($"ERROR: root assembly not found in {managedDir}: {r}");
        return 1;
    }
}

var edges = new List<(string From, string To)>();
var unresolvedRefs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

while (queue.Count > 0)
{
    string current = queue.Dequeue();
    string path = filesByAsmName[current];
    foreach (string refName in GetAssemblyReferenceNames(path))
    {
        edges.Add((current, refName));
        if (filesByAsmName.ContainsKey(refName))
        {
            if (visited.Add(refName)) queue.Enqueue(refName);
        }
        else
        {
            // A referenced assembly not physically present under ManagedDir (e.g. a facade
            // resolved elsewhere, or a reference-assembly-only dependency). Not fatal — recorded
            // for audit; sync-game-refs.ps1 can only copy what exists on disk.
            unresolvedRefs.Add(refName);
        }
    }
}

using (var writer = new StreamWriter(outFile))
{
    foreach (string name in visited.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
    {
        writer.WriteLine($"{name}\t{filesByAsmName[name]}");
    }
}

string edgesFile = outFile + ".edges.tsv";
using (var ew = new StreamWriter(edgesFile))
{
    foreach (var (from, to) in edges
                 .OrderBy(e => e.From, StringComparer.OrdinalIgnoreCase)
                 .ThenBy(e => e.To, StringComparer.OrdinalIgnoreCase))
    {
        ew.WriteLine($"{from}\t{to}");
    }
}

string unresolvedFile = outFile + ".unresolved.txt";
using (var uw = new StreamWriter(unresolvedFile))
{
    foreach (string name in unresolvedRefs) uw.WriteLine(name);
}

Console.WriteLine($"Closure: {visited.Count} assemblies (from {roots.Length} root(s)), present in {managedDir}.");
Console.WriteLine($"  Assembly list -> {outFile}");
Console.WriteLine($"  Reference edges -> {edgesFile}");
Console.WriteLine($"  Unresolved (referenced, not found on disk) -> {unresolvedFile} ({unresolvedRefs.Count})");
return 0;

static string? TryGetAssemblyName(string path)
{
    try
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata) return null;
        MetadataReader reader = pe.GetMetadataReader();
        if (!reader.IsAssembly) return null;
        AssemblyDefinition def = reader.GetAssemblyDefinition();
        return reader.GetString(def.Name);
    }
    catch
    {
        return null;
    }
}

static IEnumerable<string> GetAssemblyReferenceNames(string path)
{
    using var fs = File.OpenRead(path);
    using var pe = new PEReader(fs);
    if (!pe.HasMetadata) yield break;
    MetadataReader reader = pe.GetMetadataReader();
    foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
    {
        AssemblyReference ar = reader.GetAssemblyReference(handle);
        yield return reader.GetString(ar.Name);
    }
}
