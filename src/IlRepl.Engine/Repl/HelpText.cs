using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// The text behind <c>.help</c>.
/// </summary>
public static class HelpText
{
    /// <summary>
    /// Builds the help transcript.
    /// </summary>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<TranscriptLine> Lines()
    {
        var lines = new List<TranscriptLine>();
        var tokenizer = new CilTokenizer(CilVocabularyBuilder.Vocabulary);
        void Heading(string text) => lines.Add(TranscriptLine.Of(LineKind.Listing, text, SpanStyle.Heading));
        void Plain(string text) => lines.Add(TranscriptLine.Of(LineKind.Listing, text));
        void Code(string text) => lines.Add(new TranscriptLine(LineKind.Listing, tokenizer.Spans("  " + text, SpanStyle.Input)));

        void Entry(string name, string description)
        {
            lines.Add(TranscriptLine.Of(LineKind.Listing, "  " + name, SpanStyle.Command));
            Plain("    " + description);
        }

        void Example(string title, params string[] instructions)
        {
            Plain("");
            lines.Add(TranscriptLine.Of(LineKind.Listing, "  " + title, SpanStyle.Dim));
            foreach (var instruction in instructions)
            {
                Code(instruction);
            }
        }

        Heading("ilrepl");
        Plain("Type one IL instruction per line. The simulated stack is shown after each one.");
        Plain("ret or an empty line compiles the cell, runs it, and prints the value left on");
        Plain("the stack. Nothing on the stack means void. Two or more values is an error.");
        Plain("");
        Heading("examples");
        Example("Multiply two numbers",
            "ldc.i4 6",
            "ldc.i4 7",
            "mul",
            "ret");
        Example("Print a string",
            "ldstr \"hi\"",
            "call Console::WriteLine(string)",
            "ret");
        Example("Count to ten",
            ".locals init (int32 i)",
            "ldc.i4.0",
            "stloc i",
            "LOOP: ldloc i",
            "ldc.i4.1",
            "add",
            "dup",
            "stloc i",
            "ldc.i4 10",
            "blt LOOP",
            "ldloc i",
            "ret");
        Example("Catch an exception",
            ".locals init (string s)",
            ".try {",
            "  ldstr \"boom\"",
            "  newobj Exception::.ctor(string)",
            "  throw",
            "} catch Exception {",
            "  callvirt Exception::get_Message()",
            "  stloc s",
            "  leave DONE",
            "}",
            "DONE: ldloc s",
            "ret");
        Plain("");
        Heading("member references");
        Plain("Return type and [assembly] prefix are optional; short names resolve in System.*.");
        Code("call int32 [System.Runtime]System.Math::Max(int32, int32)");
        Code("call Math::Max(int32, int32)");
        Code("callvirt instance int32 List<int32>::get_Count()");
        Code("call !!0 Enumerable::First<int32>(class IEnumerable`1<!!0>)");
        Code("ldsfld string String::Empty");
        Code("ldtoken int32");
        Code("ldtoken method void Console::WriteLine()");
        Code("calli int32(int32, int32)");
        Plain("");
        Plain("For methods and types you define or load:");
        Code("call int32 Fib(int32)");
        Code("ldftn int32 Fib(int32)");
        Code("newobj instance void Point::.ctor(int32, int32)");
        Code("ldfld int32 Point::X");
        Code("call int32 Outer/Inner::Bump()");
        Code("callvirt instance !0 class Box`1<int32>::Get()");
        Plain("Vararg calls require Windows:");
        Code("call vararg int32 Hello::Count(..., int32)");
        Plain("");
        Heading("declarations");
        Entry(".locals init (T name, ...)", "declare locals; kept across cells, values reset");
        Entry(".args (T name = literal, ...)", "cell arguments and the values passed each run");
        Entry(".typeparams (T, U)", "make the cell generic; use !!T or !!0 in types");
        Entry(".typeargs (int32, string)", "bind the generic parameters before running");
        Entry(".vararg", "vararg calling convention, so arglist works");
        Entry(".try {", "open an exception block; exit with leave");
        Entry("} catch T {", "catch an exception of type T");
        Entry("} filter {", "start an exception filter; finish with endfilter");
        Entry("} handler {", "handle an exception accepted by the filter");
        Entry("} finally {", "run when leaving the protected region");
        Entry("} fault {", "run when an exception leaves the protected region");
        Entry(".method T Name(T a, ...) {", "define a method kept across cells; } ends it");
        Entry(".class public Name extends T {", "define a type kept across cells; } ends it");
        Entry(".field public [static] T Name", "a field of the open class; [N] before T sets its offset");
        Entry(".method public instance T Name() {", "a member of the open class; ldarg.0 is this");
        Entry(".property T Name() {", "a property; .get and .set name its accessors");
        Entry(".event T Name {", "an event; .addon and .removeon name its accessors");
        Entry(".override T::Method", "inside a member: take that interface or base slot");
        Entry(".param [N] = value", "give a parameter of the open method a default");
        Entry(".custom instance void Attr::.ctor() = { }", "attach a custom attribute");
        Entry(".pack N", "set the field alignment of the open class");
        Entry(".size N", "set the minimum size of the open class");
        Plain("");
        Heading("commands");
        foreach (var c in Completer.Commands)
        {
            if (c.Name is ".locals" or ".args" or ".typeparams" or ".typeargs" or ".vararg" or ".try" or ".method"
                or ".class" or ".field" or ".property" or ".event" or ".override" or ".param" or ".custom" or ".pack" or ".size")
            {
                continue;
            }

            Entry((c.Name + " " + c.Detail).TrimEnd(), c.Description);
        }

        Plain("");
        Plain("Tab accepts a completion; Up and Down choose a match or browse history.");
        Plain("Right accepts a grey suffix. Escape closes the palette.");
        Plain("Enter continues an open block and sends it, line by line, once its braces balance.");
        Plain("History keeps complete submissions between runs.");
        Plain("Ctrl+L clears the screen, Ctrl+Q leaves.");
        return lines;
    }
}
