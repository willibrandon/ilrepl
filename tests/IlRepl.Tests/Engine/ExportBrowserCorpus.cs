using System.Text.Json;
using IlRepl.Engine;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Generates test-only browser images from the same corpus and independent tools as desktop conformance.
/// </summary>
internal static class ExportBrowserCorpus
{
    /// <summary>
    /// Handles the explicit browser-corpus generation mode without starting the regular test suite.
    /// </summary>
    /// <param name="args">The executable arguments.</param>
    /// <returns>Whether the requested mode was handled.</returns>
    internal static async Task<bool> TryRunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != "--export-browser-corpus")
        {
            return false;
        }

        var path = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await using var json = new Utf8JsonWriter(stream);
        json.WriteStartArray();
        foreach (var example in ControlFlowExamples.All.Where(example => example.Accepted && example.BrowserCompatible))
        {
            foreach (var edited in new[] { false, true })
            {
                var session = IlLines.Load(example.Source.Split('\n'));
                if (edited)
                {
                    foreach (var line in IlLines.Expand(".method int32 ExportRendererWitness() { ldc.i4.0; ret }"))
                    {
                        session.AddLine(line);
                    }

                    var edit = session.PrepareEdit("ExportRendererWitness", "ExportRendererCopy");
                    session.CommitEdit(edit.Name, edit.Source);
                }

                session.AddLine("ldc.i4 " + example.Input);
                session.AddLine(example.Call);
                var saved = AssemblyExporter.Write(session, "FlowExport");
                var closed = example.Name + (example.GenericArguments.Length == 0 ? "" : "<" + example.GenericArguments + ">");
                var independent = ControlFlowSource.Original(example) + $$"""

                    .class public abstract sealed IlRepl.Cell extends [System.Runtime]System.Object {
                        .method public static int32 Run() cil managed {
                            .maxstack 1
                            ldc.i4 {{example.Input}}
                            call int32 Fixture::{{closed}}(int32)
                            ret
                        }
                    }
                    """;
                json.WriteStartObject();
                json.WriteString("name", example.Name + (edited ? "-edited" : "-text"));
                json.WriteString("type", "IlRepl.Cell");
                json.WriteString("method", "Run");
                json.WriteNumber("expected", example.Expected);
                json.WriteStartArray("arguments");
                json.WriteEndArray();
                json.WriteStartArray("genericArguments");
                json.WriteEndArray();
                json.WriteStartArray("images");
                Image("independent", IlasmLocator.Assemble(independent));
                Image("saved", saved);
                Image("ilasm", IlasmLocator.Assemble(session.ToIlAsm()));
                Image("ildasm", IldasmLocator.RoundTrip(saved));
                json.WriteEndArray();
                json.WriteEndObject();
            }
        }

        json.WriteEndArray();
        await json.FlushAsync();
        return true;

        void Image(string kind, byte[] image)
        {
            json.WriteStartObject();
            json.WriteString("kind", kind);
            json.WriteBase64String("image", image);
            json.WriteEndObject();
        }
    }
}
