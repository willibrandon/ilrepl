using System.Reflection;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="CellCompiler"/> and persisted cells.
/// </summary>
[TestClass]
public sealed class CellCompilerTests
{
    /// <summary>
    /// A saved cell is a real assembly with a callable IlRepl.Cell.Run.
    /// </summary>
    [TestMethod]
    public void Save_WritesAssembly_ThatRunsWhenLoaded()
    {
        var session = new Session();
        session.AddLine(".args (int32 n = 0)");
        session.AddLine("ldarg n");
        session.AddLine("ldc.i4 2");
        session.AddLine("mul");

        var directory = Path.Combine(Path.GetTempPath(), "ilrepl-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "doubler.dll");
        try
        {
            session.Save(path);
            Assert.IsTrue(File.Exists(path));

            // Loading from a stream keeps the file unmapped, so the directory can be deleted on Windows too.
            var context = new System.Runtime.Loader.AssemblyLoadContext("saved-cell", isCollectible: true);
            try
            {
                using var stream = new MemoryStream(File.ReadAllBytes(path));
                var assembly = context.LoadFromStream(stream);
                var run = assembly.GetType("IlRepl.Cell")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
                Assert.AreEqual(42, run.Invoke(null, [21]));
            }
            finally
            {
                context.Unload();
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Saving keeps the cell so it can still run.
    /// </summary>
    [TestMethod]
    public void Save_KeepsCell()
    {
        var session = new Session();
        session.AddLine("ldc.i4 5");
        var path = Path.Combine(Path.GetTempPath(), "ilrepl-tests", Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            session.Save(path);
            Assert.IsFalse(session.State.IsEmpty);
            Assert.AreEqual(5, session.Run().Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Pending labels are reported before compilation.
    /// </summary>
    [TestMethod]
    public void Compile_PendingLabel_Throws()
    {
        var session = new Session();
        session.AddLine("br NOWHERE");
        var ex = Assert.ThrowsExactly<ReplException>(() => session.Run());
        Assert.Contains("NOWHERE", ex.Message);
    }

    /// <summary>
    /// Two values on the stack at the end of the cell are rejected with guidance.
    /// </summary>
    [TestMethod]
    public void Compile_TwoValuesOnStack_Throws()
    {
        var session = new Session();
        session.AddLine("ldc.i4 1");
        session.AddLine("ldc.i4 2");
        var ex = Assert.ThrowsExactly<ReplException>(() => session.Run());
        Assert.Contains("0 or 1 value", ex.Message);
    }

    /// <summary>
    /// A saved assembly carries the session methods as public static methods beside Run.
    /// </summary>
    [TestMethod]
    public void Save_WithMethods_WritesCallableMethods()
    {
        var session = new Session();
        foreach (var line in new[] { ".method int32 Fib(int32 n) {", "ldarg n", "ldc.i4 2", "blt BASE", "ldarg n", "ldc.i4 1", "sub", "call int32 Fib(int32)", "ldarg n", "ldc.i4 2", "sub", "call int32 Fib(int32)", "add", "ret", "BASE: ldarg n", "ret", "}", "ldc.i4 1" })
        {
            session.AddLine(line);
        }

        var directory = Path.Combine(Path.GetTempPath(), "ilrepl-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "fib.dll");
        try
        {
            session.Save(path);
            var context = new System.Runtime.Loader.AssemblyLoadContext("saved-methods", isCollectible: true);
            try
            {
                using var stream = new MemoryStream(File.ReadAllBytes(path));
                var type = context.LoadFromStream(stream).GetType("IlRepl.Cell")!;
                var fib = type.GetMethod("Fib", BindingFlags.Public | BindingFlags.Static)!;
                Assert.AreEqual("n", fib.GetParameters()[0].Name);
                Assert.AreEqual(55, fib.Invoke(null, [10]));
                Assert.AreEqual(1, type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, []));
            }
            finally
            {
                context.Unload();
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
