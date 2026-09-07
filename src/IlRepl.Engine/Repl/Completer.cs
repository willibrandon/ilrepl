using System.Reflection.Emit;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Completes the first word of a line: opcode names, and REPL commands and directives when the
/// word starts with a dot.
/// </summary>
public static class Completer
{
    private static readonly CompletionItem[] CommandItems =
    [
        new(".help", "", "show help", false),
        new(".ops", "[filter]", "list opcodes with their stack transitions", true),
        new(".show", "", "the cell, with the stack after each instruction", false),
        new(".dis", "<method>", "disassemble a method: framework, loaded, session, or class member", true),
        new(".undo", "", "remove the last line of the cell", false),
        new(".clear", "", "drop the cell, keep declarations", false),
        new(".reset", "", "drop the cell, declarations, methods, and types", false),
        new(".il", "", "render the cell, its methods, and its types as ILAsm", false),
        new(".save", "<path.dll>", "write the cell to disk as an assembly", true),
        new(".load", "<assembly>", "load an assembly by name or path", true),
        new(".assemblies", "", "list the assemblies that were loaded", false),
        new(".locals", "init (T name, ...)", "declare locals", true),
        new(".args", "(T name = literal, ...)", "declare cell arguments", true),
        new(".typeparams", "(T, U)", "declare generic parameters for the cell", true),
        new(".typeargs", "(int32, ...)", "bind the generic parameters for the next run", true),
        new(".vararg", "", "give the cell the vararg calling convention", false),
        new(".try", "{", "open a protected region", true),
        new(".method", "T Name(T arg, ...) {", "define a method that persists across cells", true),
        new(".methods", "", "list the methods defined with .method", false),
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

            var op = OpcodeTable.ByName[name];
            items.Add(new CompletionItem(name, OpcodeTable.StackTransition(op), OpcodeTable.Describe(op), op.OperandType != OperandType.InlineNone));
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

            var op = OpcodeTable.ByName[name];
            items.Add(new CompletionItem(name, OpcodeTable.StackTransition(op), OpcodeTable.Describe(op), op.OperandType != OperandType.InlineNone));
        }

        return items;
    }
}
