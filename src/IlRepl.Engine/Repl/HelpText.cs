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
        void Heading(string text) => lines.Add(TranscriptLine.Of(LineKind.Listing, text, SpanStyle.Heading));
        void Plain(string text) => lines.Add(TranscriptLine.Of(LineKind.Listing, text));
        void Entry(string name, string description) => lines.Add(new TranscriptLine(LineKind.Listing,
            [new TranscriptSpan("  " + name.PadRight(30), SpanStyle.Command), new TranscriptSpan(description)]));

        Heading("ilrepl");
        Plain("Type one IL instruction per line. The simulated stack is shown after each one.");
        Plain("ret or an empty line compiles the cell, runs it, and prints the value left on");
        Plain("the stack. Nothing on the stack means void. Two or more values is an error.");
        Plain("");
        Heading("examples");
        Plain("  ldc.i4 6                       .locals init (int32 i)");
        Plain("  ldc.i4 7                       ldc.i4.0");
        Plain("  mul                            stloc i");
        Plain("  ret                            LOOP: ldloc i");
        Plain("                                 ldc.i4.1");
        Plain("  ldstr \"hi\"                     add");
        Plain("  call Console::WriteLine(string)  dup");
        Plain("  ret                            stloc i");
        Plain("                                 ldc.i4 10");
        Plain("  .try {                         blt LOOP");
        Plain("  ldstr \"boom\"                   ldloc i");
        Plain("  newobj Exception::.ctor(string)  ret");
        Plain("  throw");
        Plain("  } catch Exception {");
        Plain("  callvirt Exception::get_Message()");
        Plain("  stloc s");
        Plain("  leave DONE");
        Plain("  }");
        Plain("  DONE: ldloc s");
        Plain("  ret");
        Plain("");
        Heading("member references");
        Plain("Return type and [assembly] prefix are optional; short names resolve in System.*.");
        Plain("  call int32 [System.Runtime]System.Math::Max(int32, int32)");
        Plain("  call Math::Max(int32, int32)");
        Plain("  callvirt instance int32 List<int32>::get_Count()");
        Plain("  call !!0 Enumerable::First<int32>(class IEnumerable`1<!!0>)");
        Plain("  ldsfld string String::Empty        ldtoken int32");
        Plain("  ldtoken method void Console::WriteLine()");
        Plain("  calli int32(int32, int32)          call vararg int32 Hello::Count(..., int32)");
        Plain("  call int32 Fib(int32)              ldftn int32 Fib(int32)");
        Plain("  newobj instance void Point::.ctor(int32, int32)  ldfld int32 Point::X");
        Plain("  call int32 Outer/Inner::Bump()     callvirt instance !0 class Box`1<int32>::Get()");
        Plain("");
        Heading("declarations");
        Entry(".locals init (T name, ...)", "declare locals; kept across cells, values reset");
        Entry(".args (T name = literal, ...)", "cell arguments and the values passed each run");
        Entry(".typeparams (T, U)", "make the cell generic; use !!T or !!0 in types");
        Entry(".typeargs (int32, string)", "bind the generic parameters before running");
        Entry(".vararg", "vararg calling convention, so arglist works");
        Entry(".try {  } catch T {  }", "exception blocks, exit them with leave");
        Entry("} finally {  } fault {", "more handlers; also } filter {  } handler {");
        Entry(".method T Name(T a, ...) {", "define a method kept across cells; } ends it");
        Entry(".class public Name extends T {", "define a type kept across cells; } ends it");
        Entry(".field public [static] T Name", "a field of the open class; [N] before T sets its offset");
        Entry(".method public instance T Name() {", "a member of the open class; ldarg.0 is this");
        Entry(".property T Name() {  .get ...  }", "a property; .event T Name { .addon ... } an event");
        Entry(".override T::Method", "inside a member: take that interface or base slot");
        Entry(".pack N  .size N", "layout of the open class; .custom attaches an attribute");
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
        Plain("Tab completes opcodes, commands, types, and members; Up and Down walk history.");
        Plain("Enter continues an open block and sends it, line by line, once its braces balance.");
        Plain("History is kept in ~/.config/ilrepl/history between runs.");
        Plain("Ctrl+L clears the screen, Ctrl+Q leaves.");
        return lines;
    }
}
