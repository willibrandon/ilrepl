using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using IlRepl.Protocol;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Responsiveness;

/// <summary>
/// Produces deterministic source and metadata workloads without checking generated binaries into the repository.
/// </summary>
internal static class ResponsivenessFixtures
{
    /// <summary>
    /// Identifies the generator and all workload contents independently of artifact paths.
    /// </summary>
    public const string Version = "responsiveness-v1";

    /// <summary>
    /// Generates or reuses a content-addressed assembly containing the requested number of types and callable members.
    /// </summary>
    public static string Assembly(string cache, int types, int membersPerType)
    {
        var identity = $"{Version}:{types}:{membersPerType}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var name = "Responsiveness" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
        var directory = Path.Combine(cache, Version);
        Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, name + ".sha256");
        if (File.Exists(manifest))
        {
            var expected = File.ReadAllText(manifest);
            if (expected.Length == 64 && expected.All(char.IsAsciiHexDigit))
            {
                var cached = Path.Combine(directory, expected + ".dll");
                if (File.Exists(cached) && SessionCodec.Hash(File.ReadAllBytes(cached)) == expected) return cached;
            }
        }
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name,
            ModuleKind.Dll);
        var module = assembly.MainModule;
        module.Mvid = new Guid(hash.AsSpan(0, 16));
        for (var index = 0; index < types; index++)
        {
            var type = new TypeDefinition("Responsiveness", "CatalogType" + index.ToString("D5", CultureInfo.InvariantCulture),
                TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(type);
            for (var member = 0; member < membersPerType; member++)
            {
                var method = new MethodDefinition("Method" + member.ToString("D2", CultureInfo.InvariantCulture),
                    MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
                type.Methods.Add(method);
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, index + member));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            }
        }
        using var image = new MemoryStream();
        assembly.Write(image, new WriterParameters { Timestamp = 0 });
        var content = image.ToArray();
        var contentHash = SessionCodec.Hash(content);
        var path = Path.Combine(directory, contentHash + ".dll");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite: true);
            File.WriteAllText(manifest, contentHash);
        }
        finally
        {
            File.Delete(temporary);
        }
        return path;
    }

    /// <summary>
    /// Builds a valid multiline method draft with a known final stack and exact physical line count.
    /// </summary>
    public static string Draft(int lines) => string.Join('\n',
        Enumerable.Repeat("nop", lines - 4).Prepend("ldc.i4.1").Prepend(".method int32 Draft() {").Append("ret").Append("}"));

    /// <summary>
    /// Produces successively nested generic type syntax to exercise binding and completion of unfinished constructions.
    /// </summary>
    public static string Generic(int depth) => "ldtoken " + string.Concat(Enumerable.Repeat("List<", depth))
        + "int32" + new string('>', depth);

    /// <summary>
    /// Creates retained source and historical submissions that hydrate definitions without executing old cells.
    /// </summary>
    public static SessionDocument Session(int submissions, int definitions)
    {
        var entries = new List<SessionEntry>();
        var cells = new List<SessionCell>();
        for (var index = 0; index < submissions; index++)
        {
            var number = index + 1;
            var definition = index < definitions;
            string[] source = definition
                ? [$".method int32 Retained{index}() {{", "ldc.i4 " + index.ToString(CultureInfo.InvariantCulture), "ret", "}"]
                : ["ldc.i4.1", "ret"];
            entries.Add(new SessionEntry
            {
                Identity = "source-" + number, Number = number, Kind = SessionEntryKind.Source,
                Source = definition ? source : [source[0]],
            });
            if (!definition)
            {
                entries.Add(new SessionEntry
                {
                    Identity = "run-" + number, Number = number, Kind = SessionEntryKind.Run, Source = ["ret"],
                });
            }
            cells.Add(new SessionCell
            {
                Identity = "cell-" + number, Number = number, Source = source, Kind = definition ? "definition" : "cell",
                State = "succeeded", Output = [TranscriptLine.Of(LineKind.Result, "= 1 : int32", SpanStyle.Number)],
            });
        }
        return new SessionDocument { Entries = [.. entries], Cells = [.. cells] };
    }
}
