using System.Reflection.Emit;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Completes opcode names, REPL commands, and directives at the start of a line.
/// </summary>
/// <remarks>
/// Completes the first word of a line: opcode names, and REPL commands and directives when the
/// word starts with a dot.
/// </remarks>
public static class Completer
{
    private static readonly CompletionItem[] CommandItems =
    [
        new(".help", "", "show help", false),
        new(".ops", "[filter]", "list opcodes with their stack transitions", true),
        new(".show", "", "the cell, with the stack after each instruction", false),
        new(".dis", "<method>", "disassemble a method: framework, loaded, session, or class member", true),
        new(".jit", "[method | cell N] [--tier fullopts|tier0|tier1] [--against method]",
            "inspect or compare actual native code in a fresh CoreCLR worker", true),
        new(".edit", "[<method> [as Name]]", "open an editable copy; omit the method to edit the last disassembly", true),
        new(".diff", "[Name] [--raw] [--native]", "compare original and edited IL or native code", true),
        new(".compare", "Name (<arguments>) | Name using Scenario",
            "run the original and edit from the same explicit starting conditions", true),
        new(".undo", "", "remove the last line of the cell", false),
        new(".clear", "", "drop the cell, keep declarations", false),
        new(".session", "[save|open|restore|cells|cell|run]", "save, reopen, inspect, and explicitly run an experiment", true),
        new(".reset", "", "drop the cell, declarations, methods, and types", false),
        new(".il", "", "render the cell, its methods, and its types as ILAsm", false),
        new(".save", "<path.dll | path.ilrepl.json>", "export an assembly or save an editable session", true),
        new(".load", "<assembly | project | nuget:Id[,range] | session>", "load a dependency or reopen session source", true),
        new(".assemblies", "", "list the assemblies that were loaded", false),
        new(".locals", "init (T name, ...)", "declare locals", true),
        new(".args", "(T name = literal, ...)", "declare cell arguments", true),
        new(".typeparams", "(T, U)", "declare generic parameters for the cell", true),
        new(".typeargs", "(int32, ...)", "bind the generic parameters for the next run", true),
        new(".vararg", "", "give the cell the vararg calling convention", false),
        new(".try", "{", "open a protected region", true),
        new(".method", "T Name(T arg, ...) {", "define a method that persists across cells", true),
        new(".methods", "[Edit]", "list methods or inspect the dependencies of an edit", true),
        new(".class", "[attrs] Name [extends T] {", "define a type that persists across cells", true),
        new(".field", "[public] [static] T Name", "declare a field of the open class", true),
        new(".property", "T Name() {", "declare a property of the open class; .get and .set name its accessors", true),
        new(".event", "T Name {", "declare an event of the open class; .addon and .removeon name its accessors", true),
        new(".override", "T::Method", "implement an interface or base slot under this method's name", true),
        new(".param", "[N] = value", "give a parameter of the open method a default", true),
        new(".custom", "instance void Attr::.ctor() = { }", "attach a custom attribute", true),
        new(".pack", "N", "align the fields of the open class", true),
        new(".size", "N", "the least size of the open class", true),
        new(".types", "", "list the types defined with .class", false),
        new(".stack", "", "show the stack", false),
        new(".time", "[on|off]", "toggle timing of each run", true),
        new(".quiet", "[on|off]", "toggle the stack echo", true),
        new(".run", "", "run the cell", false),
        new(".quit", "", "leave", false),
    ];

    /// <summary>
    /// The commands and directives, in display order.
    /// </summary>
    public static IReadOnlyList<CompletionItem> Commands => CommandItems;

    /// <summary>
    /// Every opcode followed by every command, the catalog the front-end completes from.
    /// </summary>
    public static IReadOnlyList<CompletionItem> Catalog { get; } = BuildCatalog();

    private static CompletionItem[] BuildCatalog()
    {
        var items = new List<CompletionItem>();
        foreach (var name in OpcodeTable.Names)
        {
            if (OpcodeTable.IsReserved(name))
            {
                continue;
            }

            var op = OpcodeTable.BySourceName[name];
            items.Add(new CompletionItem(name, OpcodeTable.StackTransition(op), InstructionReference.For(name).Explanation,
                op.OperandType != OperandType.InlineNone) { InstructionHelp = InstructionReference.For(name) });
        }

        items.AddRange(CommandItems);
        return [.. items];
    }

    /// <summary>
    /// Returns the candidates whose name starts with <paramref name="word"/>.
    /// </summary>
    /// <param name="word">The first word typed so far.</param>
    /// <returns>The matching candidates, or an empty list for an empty word.</returns>
    public static IReadOnlyList<CompletionItem> Complete(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (word.Length == 0)
        {
            return [];
        }

        if (word.StartsWith('.'))
        {
            return CommandItems.Where(c => c.Name.StartsWith(word, StringComparison.Ordinal)).ToArray();
        }

        var items = new List<CompletionItem>();
        foreach (var name in OpcodeTable.Names)
        {
            if (OpcodeTable.IsReserved(name) || !name.StartsWith(word, StringComparison.Ordinal))
            {
                continue;
            }

            var op = OpcodeTable.BySourceName[name];
            items.Add(new CompletionItem(name, OpcodeTable.StackTransition(op), InstructionReference.For(name).Explanation,
                op.OperandType != OperandType.InlineNone) { InstructionHelp = InstructionReference.For(name) });
        }

        return items;
    }
}
