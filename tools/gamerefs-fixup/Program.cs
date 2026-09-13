// GameRefsFixup — s1-finish task fix, extended by s1-fix-r1.
//
// ROOT CAUSE #1 (lead-confirmed, see docs/progress/wp2.md "S1 crash diagnosis" + this task's own
// instructions): the Unity 2022.3.43f1 Editor's native `MonoScriptInfoScraper` scrapes every
// loaded assembly at domain-reload time for a compiler-generated type named exactly
// `UnitySourceGeneratedAssemblyMonoScriptTypes_v1` and invokes its static data method BY THE
// EXPECTED NAME `Get` (see the real Unity-emitted source,
// D:/SPT41/Development/_decompiled/UnitySourceGeneratedAssemblyMonoScriptTypes_v1.cs, which still
// carries Unity's own `[GeneratedCode("Unity.MonoScriptGenerator.MonoScriptInfoGenerator", null)]`
// attribute proving the type's origin). SPT's deobfuscation/remapper pass renamed EVERY
// compiler-synthesized static method across the whole decompiled codebase to a generic
// `CG_smethod_<N>` scheme (confirmed: grepping the decompile for `CG_smethod_0` alone hits five
// entirely unrelated classes that each independently start their own local numbering at 0 — this
// is a per-class counter, not a global one) — and this specific method got caught by that same
// blanket rename, becoming `CG_smethod_0` instead of `Get`. The Editor's native scraper invokes
// the method it expects (`Get`) via `mono_runtime_invoke`, finds nothing at that name/slot, and
// crashes natively (SIGSEGV) inside `MonoScriptInfoScraper::GetAssemblyScrapedMonoScripts` ->
// `ScanForSourceGeneratedMonoScriptInfo` — this happens before any of ArenaMapPort's own
// `-executeMethod` code runs at all, during the very first post-compile domain reload.
//
// FIX #1: post-process the GameRefs COPIES (never D:/SPT41's originals — O4/O8) of
// Assembly-CSharp.dll and Assembly-CSharp-firstpass.dll with Mono.Cecil (the local, read-only
// D:/SPT41/BepInEx/core/Mono.Cecil.dll — no NuGet download) to REMOVE the whole
// `UnitySourceGeneratedAssemblyMonoScriptTypes_v1` type (incl. its nested `MonoScriptData`
// struct) after verifying nothing else in the module references it.
//
// FALLBACK #1 (only if removal is provably unsafe for a concrete, logged reason — e.g. some other
// type in the module genuinely references it): rename the method back from `CG_smethod_0` to
// `Get` instead, leaving the type itself untouched, and log exactly why removal was skipped.
//
// ROOT CAUSE #2 (s1-fix-r1, this pass — the "second, separate blocker" docs/progress/s1-finish.md
// diagnosed but did not resolve): with the SIGSEGV gone, `AddComponent<T>()` for every §D EFT type
// still permanently returned null ("Script X has not finished compilation yet"), for the entire
// life of the batchmode process, regardless of retries/ticks/caching/platform flags/API choice —
// see docs/progress/s1-fix-r1.md for the full trail, including why remove-vs-rename of fix #1 was
// a red herring (both were tested with the DLL still literally named `Assembly-CSharp.dll`). The
// real cause: `Assembly-CSharp`/`Assembly-CSharp-firstpass` are Unity's own RESERVED names for the
// implicit legacy script assemblies its Editor compiles from loose project .cs files — dropping a
// PRECOMPILED plugin DLL under those literal names makes `EditorCompilationInterface` treat it as
// "the predefined Assembly-CSharp[-firstpass] assembly, still waiting on a Roslyn compile that will
// never happen in this project", so it is never marked ready and every AddComponent for a type in
// it fails identically forever. FIX #2 (tools/sync-game-refs.ps1) renames the two GameRefs DLL
// FILES away from those reserved names (`SptGameRefs-AssemblyCSharp[Firstpass].dll`) — which then
// immediately surfaced a THIRD, Unity-enforced consistency rule: "Assembly name 'Assembly-CSharp'
// does not match file name 'SptGameRefs-AssemblyCSharp'" — Unity refuses to load ANY precompiled
// Plugin DLL whose embedded assembly identity (`AssemblyNameDefinition.Name`) doesn't exactly equal
// its own file name (extension-less). This tool's new `RenameAssemblyIdentity`/cross-reference pass
// below satisfies that rule directly: it rewrites the two renamed DLLs' own `AssemblyDefinition.Name`/
// `ModuleDefinition.Name` to match their new file names, and — since every DLL under GameRefs/ is
// now passed to this tool, not just the two — scans EVERY OTHER copied DLL's
// `ModuleDefinition.AssemblyReferences` for an entry still named `Assembly-CSharp`/
// `Assembly-CSharp-firstpass` and rewrites that reference's `.Name` too, so nothing in the closure
// is left trying to resolve the old (no-longer-existing) identity.
//
// Usage:
//   GameRefsFixup <dll-path> [<dll-path> ...]
// Patches each file IN PLACE (overwrites, only if actually modified). Exits 0 if every file was
// handled without an unexpected error, non-zero otherwise. Pass the FULL GameRefs/*.dll set (not
// just the two Assembly-CSharp ones) so the cross-reference-rewrite pass can see every DLL that
// might reference them.
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

const string TargetTypeName = "UnitySourceGeneratedAssemblyMonoScriptTypes_v1";
const string ExpectedMethodName = "Get";
const string ObfuscatedMethodName = "CG_smethod_0";

// s1-fix-r1 FIX #2 — must exactly match tools/sync-game-refs.ps1's $reservedNameRemap.
var identityRenameMap = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["Assembly-CSharp"] = "SptGameRefs-AssemblyCSharp",
    ["Assembly-CSharp-firstpass"] = "SptGameRefs-AssemblyCSharpFirstpass",
};

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: GameRefsFixup <dll-path> [<dll-path> ...]");
    return 1;
}

int failures = 0;
foreach (string rawPath in args)
{
    string path = Path.GetFullPath(rawPath);

    // O4/O8 belt-and-suspenders: this tool only ever runs against GameRefs COPIES, never the
    // read-only originals — refuse defensively if ever pointed at a protected source path.
    if (path.StartsWith(@"D:\SPT41", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("X:\\", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("X:/", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"REFUSING to patch a protected read-only source path: {path} (O4/O8)");
        failures++;
        continue;
    }

    try
    {
        ProcessOne(path, identityRenameMap);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[GameRefsFixup] ERROR patching {path}: {ex}");
        failures++;
    }
}

return failures == 0 ? 0 : 1;

static void ProcessOne(string path, Dictionary<string, string> identityRenameMap)
{
    if (!File.Exists(path))
        throw new FileNotFoundException($"DLL not found: {path}");

    // Assembly-CSharp.dll's own metadata (custom-attribute enum-typed arguments, parameter
    // default values, etc.) references types living in OTHER assemblies (Unity.Burst,
    // UnityEngine.CoreModule, ...). Cecil resolves those lazily even for an in-place edit that
    // never touches them, both on read (attribute signature parsing) and on write (constant-type
    // lookup) — so it needs a real resolver, not the default GAC/cwd-only one. Search the DLL's
    // own directory (GameRefs — covers same-closure deps actually copied there) plus the
    // original, read-only Managed folder (covers UnityEngine*/System*/etc., which
    // sync-game-refs.ps1 deliberately does NOT copy into GameRefs since the Editor already
    // provides them, but which the ORIGINAL Managed folder still has on disk for resolution
    // purposes — read-only access, never written, O4/O8 fine).
    var resolver = new DefaultAssemblyResolver();
    string? dllDir = Path.GetDirectoryName(path);
    if (dllDir != null) resolver.AddSearchDirectory(dllDir);
    const string ManagedDirFallback = "D:/SPT41/EscapeFromTarkov_Data/Managed";
    if (Directory.Exists(ManagedDirFallback)) resolver.AddSearchDirectory(ManagedDirFallback);

    byte[] originalBytes = ReadAllBytesWithRetry(path);
    using var readStream = new MemoryStream(originalBytes);
    var readerParams = new ReaderParameters(ReadingMode.Immediate) { ReadWrite = false, InMemory = true, AssemblyResolver = resolver };
    using AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(readStream, readerParams);

    ModuleDefinition module = asm.MainModule;
    string asmName = asm.Name.Name;
    bool modified = false;

    TypeDefinition? target = null;
    foreach (TypeDefinition t in module.Types)
    {
        if (t.Name == TargetTypeName)
        {
            target = t;
            break;
        }
    }

    if (target == null)
    {
        Console.WriteLine($"[GameRefsFixup] {asmName}: no '{TargetTypeName}' type present — nothing to do (fix #1).");
    }
    else
    {
    // Collect the target + every nested type transitively (§ task: "incl. nested types").
    var targetSubtree = new HashSet<TypeDefinition>();
    CollectSubtree(target, targetSubtree);
    var targetFullNames = new HashSet<string>(targetSubtree.Select(t => t.FullName), StringComparer.Ordinal);

    // Verify nothing else in the module references the type (or any nested type) before removing
    // it. A compiler-generated MonoScript-scrape type is never referenced from ordinary C# source
    // (confirmed this pass by grepping the full decompile: nothing anywhere calls
    // `UnitySourceGeneratedAssemblyMonoScriptTypes_v1.Get()`/`.CG_smethod_0()` — the type exists
    // solely for the Unity Editor's own native reflection scrape, by name/pattern, not by IL call)
    // — but verify empirically against THIS module's real metadata rather than assuming.
    var referencingSites = new List<string>();
    foreach (TypeDefinition other in AllTypes(module))
    {
        if (targetSubtree.Contains(other)) continue; // don't flag the subtree referencing itself
        FindReferences(other, targetFullNames, referencingSites);
    }
    // Module/assembly-level custom attributes and exported (forwarded) types, too.
    CheckAttributes(module.CustomAttributes, targetFullNames, referencingSites, $"module '{module.Name}'");
    CheckAttributes(asm.CustomAttributes, targetFullNames, referencingSites, $"assembly '{asmName}'");
    foreach (ExportedType et in module.ExportedTypes)
    {
        if (targetFullNames.Contains(et.FullName))
            referencingSites.Add($"module.ExportedTypes: {et.FullName}");
    }

    // s1-finish investigation note (see docs/progress/s1-finish.md for the full trail): a
    // hypothesis this pass suspected REMOVING the type (vs. renaming the method in place) might
    // be why `AddComponent<T>()` for the assembly's MonoBehaviour types (LevelSettings, BotZone,
    // etc.) separately, permanently returns null with Unity logging "Script X has not finished
    // compilation yet" — reasoning that the type's own `MonoScriptData` payload might be what
    // `MonoScriptInfoScraper` uses to populate its AddComponent-eligibility registry. Tested
    // directly: renamed the method back to `Get` INSTEAD of removing the type, wiped Library for
    // a truly clean rebuild, re-ran the same AddComponent probe (GameObject.AddComponent<T>(),
    // UnityEditor.ObjectFactory.AddComponent, and go.AddComponent(reflectedType) after confirming
    // via AppDomain.CurrentDomain.GetAssemblies() the CLR had already loaded the real type) — same
    // null/failure, byte-for-byte identical to the removal case. This DISPROVES the hypothesis:
    // the AddComponent issue is a separate, independent limitation of this Unity Editor version
    // with a non-"Any Platform" precompiled Plugin DLL, unaffected by remove vs. rename either
    // way (see s1-finish.md's "Second, separate blocker" section — not something this tool's
    // remove/rename choice can influence). Removal is therefore back to being the task's literal
    // preferred fix (safe per the zero-IL-references check below); rename is the true fallback,
    // used only when a reference is actually found — not a workaround for the AddComponent issue.
    bool typeFixDone = false;

    if (referencingSites.Count == 0)
    {
        module.Types.Remove(target);
        Console.WriteLine($"[GameRefsFixup] {asmName}: removed '{target.FullName}' (+ {targetSubtree.Count - 1} nested type(s)) — verified zero other references in the module.");
        typeFixDone = true;
    }
    else
    {
        Console.WriteLine($"[GameRefsFixup] {asmName}: '{target.FullName}' has {referencingSites.Count} real reference(s) elsewhere in the module — removal would be unsafe. Sites:");
        foreach (string site in referencingSites.Take(20))
            Console.WriteLine("    " + site);
        if (referencingSites.Count > 20)
            Console.WriteLine($"    ... and {referencingSites.Count - 20} more");

        // FALLBACK: rename the method Unity's native scraper expects back to 'Get', leave the
        // (apparently in-use) type itself alone.
        MethodDefinition? method = target.Methods.FirstOrDefault(m => m.Name == ObfuscatedMethodName && m.Parameters.Count == 0 && m.IsStatic);
        if (method == null)
        {
            MethodDefinition? already = target.Methods.FirstOrDefault(m => m.Name == ExpectedMethodName && m.Parameters.Count == 0 && m.IsStatic);
            if (already != null)
            {
                Console.WriteLine($"[GameRefsFixup] {asmName}: '{target.FullName}'.{ExpectedMethodName}() already has the expected name — nothing to do (fix #1).");
                typeFixDone = false; // nothing changed by fix #1; fix #2 below still runs.
            }
            else
            {
                throw new InvalidOperationException(
                    $"{asmName}: '{target.FullName}' is referenced elsewhere (can't remove) AND no static parameterless '{ObfuscatedMethodName}' method was found on it to rename back to '{ExpectedMethodName}' — neither fix is applicable, manual investigation needed.");
            }
        }
        else
        {
            method.Name = ExpectedMethodName;
            Console.WriteLine($"[GameRefsFixup] {asmName}: FALLBACK applied — renamed {target.FullName}.{ObfuscatedMethodName}() back to '{ExpectedMethodName}' (type left in place because it is genuinely referenced elsewhere; see sites above).");
            typeFixDone = true;
        }
    }

    modified = modified || typeFixDone;
    } // end fix #1 (type present) branch

    // -----------------------------------------------------------------------------------------
    // s1-fix-r1 FIX #2a — this DLL's OWN identity: if its current assembly name is one of the
    // reserved names ("Assembly-CSharp"/"Assembly-CSharp-firstpass") and its on-disk file name has
    // already been changed away from that (by tools/sync-game-refs.ps1's copy step) to the paired
    // new name, rewrite AssemblyDefinition.Name/ModuleDefinition.Name to match — Unity refuses to
    // load a precompiled Plugin DLL whose embedded identity doesn't equal its file name ("Assembly
    // name 'X' does not match file name 'Y'"), confirmed empirically this pass (see the header
    // comment's ROOT CAUSE #2 / docs/progress/s1-fix-r1.md).
    // -----------------------------------------------------------------------------------------
    string fileNameNoExt = Path.GetFileNameWithoutExtension(path);
    if (identityRenameMap.TryGetValue(asmName, out string? newIdentityName) && fileNameNoExt == newIdentityName)
    {
        asm.Name.Name = newIdentityName;
        module.Name = fileNameNoExt + Path.GetExtension(path);
        Console.WriteLine($"[GameRefsFixup] {asmName}: renamed assembly identity -> '{newIdentityName}' to match its on-disk file name (fix #2a).");
        modified = true;
        asmName = newIdentityName; // for the rest of this method's logging only
    }

    // -----------------------------------------------------------------------------------------
    // s1-fix-r1 FIX #2b — cross-reference rewrite: ANY other DLL in the same GameRefs/ closure may
    // hold an AssemblyReference row still pointing at the OLD reserved name ("Assembly-CSharp"/
    // "Assembly-CSharp-firstpass") — that reference would fail to resolve once the referenced DLL's
    // own identity has been renamed by fix #2a above (run against the whole GameRefs/*.dll set, not
    // just the two, precisely so this pass can see and fix every referencing site, not just the
    // renamed DLLs' own metadata).
    // -----------------------------------------------------------------------------------------
    foreach (AssemblyNameReference reference in module.AssemblyReferences)
    {
        if (identityRenameMap.TryGetValue(reference.Name, out string? refNewName))
        {
            Console.WriteLine($"[GameRefsFixup] {asmName}: rewrote AssemblyReference '{reference.Name}' -> '{refNewName}' (fix #2b).");
            reference.Name = refNewName;
            modified = true;
        }
    }

    if (!modified)
    {
        Console.WriteLine($"[GameRefsFixup] {asmName}: no changes needed.");
        return;
    }

    using var outStream = new MemoryStream();
    asm.Write(outStream);
    WriteAllBytesWithRetry(path, outStream.ToArray());
    Console.WriteLine($"[GameRefsFixup] {asmName}: wrote {outStream.Length:N0} bytes -> {path} (was {originalBytes.Length:N0} bytes).");
}

// s1-fix-r1: running against the FULL ~48-file GameRefs/*.dll set right after
// tools/sync-game-refs.ps1 freshly Copy-Item'd every one of them made a real, reproducible
// transient failure visible that never showed up patching just the 2 original targets:
// "The process cannot access the file '...dll' because it is being used by another process"
// (observed on Unity.Burst.dll / Unity.TextMeshPro.dll) — Windows Defender/AV real-time scanning a
// just-created file holds a brief exclusive handle on it. Not a logic bug; retry with a short
// backoff rather than failing the whole sync over a transient OS-level lock.
static byte[] ReadAllBytesWithRetry(string path, int maxAttempts = 5, int delayMs = 200)
{
    for (int attempt = 1; ; attempt++)
    {
        try { return File.ReadAllBytes(path); }
        catch (IOException) when (attempt < maxAttempts) { Thread.Sleep(delayMs); }
    }
}

static void WriteAllBytesWithRetry(string path, byte[] bytes, int maxAttempts = 5, int delayMs = 200)
{
    for (int attempt = 1; ; attempt++)
    {
        try { File.WriteAllBytes(path, bytes); return; }
        catch (IOException) when (attempt < maxAttempts) { Thread.Sleep(delayMs); }
    }
}

static void CollectSubtree(TypeDefinition t, HashSet<TypeDefinition> into)
{
    if (!into.Add(t)) return;
    foreach (TypeDefinition nested in t.NestedTypes)
        CollectSubtree(nested, into);
}

static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
{
    foreach (TypeDefinition t in module.Types)
    {
        yield return t;
        foreach (TypeDefinition n in AllNested(t))
            yield return n;
    }
}

static IEnumerable<TypeDefinition> AllNested(TypeDefinition t)
{
    foreach (TypeDefinition n in t.NestedTypes)
    {
        yield return n;
        foreach (TypeDefinition n2 in AllNested(n))
            yield return n2;
    }
}

static bool ReferencesTarget(TypeReference? type, HashSet<string> targetFullNames)
{
    if (type == null) return false;
    if (targetFullNames.Contains(type.FullName)) return true;

    if (type is GenericInstanceType git)
    {
        if (ReferencesTarget(git.ElementType, targetFullNames)) return true;
        foreach (TypeReference ga in git.GenericArguments)
            if (ReferencesTarget(ga, targetFullNames)) return true;
        return false;
    }
    if (type is TypeSpecification spec) // ArrayType, PointerType, ByReferenceType, PinnedType,
                                         // OptionalModifierType, RequiredModifierType, SentinelType
        return ReferencesTarget(spec.ElementType, targetFullNames);

    return false;
}

static void CheckAttributes(IEnumerable<CustomAttribute> attrs, HashSet<string> targetFullNames, List<string> sites, string ownerDesc)
{
    foreach (CustomAttribute ca in attrs)
    {
        if (ReferencesTarget(ca.Constructor?.DeclaringType, targetFullNames))
            sites.Add($"{ownerDesc}: [CustomAttribute {ca.AttributeType.FullName}]");
        foreach (CustomAttributeArgument arg in ca.ConstructorArguments)
            if (ReferencesTarget(arg.Type, targetFullNames))
                sites.Add($"{ownerDesc}: [CustomAttribute {ca.AttributeType.FullName} ctor-arg type {arg.Type.FullName}]");
        foreach (CustomAttributeNamedArgument na in ca.Fields.Concat(ca.Properties))
            if (ReferencesTarget(na.Argument.Type, targetFullNames))
                sites.Add($"{ownerDesc}: [CustomAttribute {ca.AttributeType.FullName} named-arg '{na.Name}' type {na.Argument.Type.FullName}]");
    }
}

static void FindReferences(TypeDefinition type, HashSet<string> targetFullNames, List<string> sites)
{
    if (ReferencesTarget(type.BaseType, targetFullNames))
        sites.Add($"{type.FullName}: BaseType = {type.BaseType!.FullName}");

    foreach (InterfaceImplementation iface in type.Interfaces)
        if (ReferencesTarget(iface.InterfaceType, targetFullNames))
            sites.Add($"{type.FullName}: implements {iface.InterfaceType.FullName}");

    CheckAttributes(type.CustomAttributes, targetFullNames, sites, type.FullName);

    foreach (GenericParameter gp in type.GenericParameters)
        foreach (TypeReference c in gp.Constraints)
            if (ReferencesTarget(c, targetFullNames))
                sites.Add($"{type.FullName}: generic constraint {c.FullName}");

    foreach (FieldDefinition f in type.Fields)
    {
        if (ReferencesTarget(f.FieldType, targetFullNames))
            sites.Add($"{type.FullName}.{f.Name}: field type {f.FieldType.FullName}");
        CheckAttributes(f.CustomAttributes, targetFullNames, sites, $"{type.FullName}.{f.Name}");
    }

    foreach (PropertyDefinition p in type.Properties)
    {
        if (ReferencesTarget(p.PropertyType, targetFullNames))
            sites.Add($"{type.FullName}.{p.Name}: property type {p.PropertyType.FullName}");
        CheckAttributes(p.CustomAttributes, targetFullNames, sites, $"{type.FullName}.{p.Name}");
    }

    foreach (EventDefinition e in type.Events)
        if (ReferencesTarget(e.EventType, targetFullNames))
            sites.Add($"{type.FullName}.{e.Name}: event type {e.EventType.FullName}");

    foreach (MethodDefinition m in type.Methods)
        FindReferencesInMethod(m, targetFullNames, sites);
}

static void FindReferencesInMethod(MethodDefinition m, HashSet<string> targetFullNames, List<string> sites)
{
    string owner = $"{m.DeclaringType.FullName}::{m.Name}";

    if (ReferencesTarget(m.ReturnType, targetFullNames))
        sites.Add($"{owner}: return type {m.ReturnType.FullName}");

    foreach (ParameterDefinition p in m.Parameters)
    {
        if (ReferencesTarget(p.ParameterType, targetFullNames))
            sites.Add($"{owner}: parameter '{p.Name}' type {p.ParameterType.FullName}");
        CheckAttributes(p.CustomAttributes, targetFullNames, sites, $"{owner}(param {p.Name})");
    }

    foreach (GenericParameter gp in m.GenericParameters)
        foreach (TypeReference c in gp.Constraints)
            if (ReferencesTarget(c, targetFullNames))
                sites.Add($"{owner}: generic constraint {c.FullName}");

    foreach (MethodReference ov in m.Overrides)
        if (ReferencesTarget(ov.DeclaringType, targetFullNames))
            sites.Add($"{owner}: explicit override of {ov.FullName}");

    CheckAttributes(m.CustomAttributes, targetFullNames, sites, owner);

    if (!m.HasBody) return;
    MethodBody body = m.Body;

    foreach (VariableDefinition v in body.Variables)
        if (ReferencesTarget(v.VariableType, targetFullNames))
            sites.Add($"{owner}: local variable type {v.VariableType.FullName}");

    foreach (ExceptionHandler eh in body.ExceptionHandlers)
        if (eh.CatchType != null && ReferencesTarget(eh.CatchType, targetFullNames))
            sites.Add($"{owner}: catch({eh.CatchType.FullName})");

    foreach (Instruction insn in body.Instructions)
    {
        switch (insn.Operand)
        {
            case TypeReference tr:
                if (ReferencesTarget(tr, targetFullNames))
                    sites.Add($"{owner}: IL @{insn.Offset:X4} {insn.OpCode} {tr.FullName}");
                break;
            case FieldReference fr:
                if (ReferencesTarget(fr.DeclaringType, targetFullNames) || ReferencesTarget(fr.FieldType, targetFullNames))
                    sites.Add($"{owner}: IL @{insn.Offset:X4} {insn.OpCode} field {fr.FullName}");
                break;
            case MethodReference mr:
                if (ReferencesTarget(mr.DeclaringType, targetFullNames) || ReferencesTarget(mr.ReturnType, targetFullNames)
                    || mr.Parameters.Any(pp => ReferencesTarget(pp.ParameterType, targetFullNames)))
                    sites.Add($"{owner}: IL @{insn.Offset:X4} {insn.OpCode} call {mr.FullName}");
                if (mr is GenericInstanceMethod gim && gim.GenericArguments.Any(ga => ReferencesTarget(ga, targetFullNames)))
                    sites.Add($"{owner}: IL @{insn.Offset:X4} {insn.OpCode} generic-call {mr.FullName}");
                break;
            case CallSite cs:
                if (ReferencesTarget(cs.ReturnType, targetFullNames) || cs.Parameters.Any(pp => ReferencesTarget(pp.ParameterType, targetFullNames)))
                    sites.Add($"{owner}: IL @{insn.Offset:X4} {insn.OpCode} calli");
                break;
        }
    }
}
