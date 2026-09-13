namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser comparisons preserve the task objects returned to scenarios.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Cached and newly allocated tasks remain distinguishable even when their completed values match.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="generic">Whether the selected method declares the task's result type.</param>
    /// <returns>The completed task identity assertions.</returns>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", false)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_TaskComparisonPreservesIdentity(string browser, bool generic)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var task = "class Task" + (generic ? "`1<int32>" : "");
        await SubmitEditSourceAsync(page, $$"""
            .class public Tasks {
              .field public static class Task`1<int32> Cached
              .method private static void .cctor() {
                ldc.i4.s 42
                call class Task`1<!!0> Task::FromResult<int32>(!!0)
                stsfld class Task`1<int32> Tasks::Cached
                ret
              }
              .method public static {{task}} Read() {
                ldsfld class Task`1<int32> Tasks::Cached
                ret
              }
            }
            .edit {{task}} Tasks::Read() as Copy {
              .method public static {{task}} Read() cil managed {
                ldc.i4.s 42
                call class Task`1<!!0> Task::FromResult<int32>(!!0)
                ret
              }
            }
            .method bool Scenario() {
              call Copy
              call Copy
              call bool Object::ReferenceEquals(object, object)
              ret
            }
            """, "end of method Scenario");

        await TypeLineAsync(page, ".compare Copy using Scenario");
        await ExpectComparisonTextAsync(page, "Copy: different");
        var text = await BufferTextAsync(page);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.Contains("\"true\"", text);
        Assert.Contains("\"false\"", text);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    /// <summary>
    /// A scenario can read the asynchronous state stored on the selected method's task.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="generic">Whether the selected method declares the task's result type.</param>
    /// <returns>The completed task state assertions.</returns>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", false)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_TaskComparisonPreservesAsyncState(string browser, bool generic)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var task = "class Task" + (generic ? "`1<int32>" : "");
        var body = """
            ldstr "before"
            newobj instance void class TaskCompletionSource`1<int32>::.ctor(object)
            dup
            ldc.i4.s 42
            callvirt instance void class TaskCompletionSource`1<int32>::SetResult(!0)
            callvirt instance class Task`1<!0> class TaskCompletionSource`1<int32>::get_Task()
            ret
            """;
        await SubmitEditSourceAsync(page, ".method " + task + " Read() {\n" + body + "\n}\n"
            + ".edit Read as Copy {\n.method public static " + task + " Read() cil managed {\n"
            + body.Replace("\"before\"", "\"after\"", StringComparison.Ordinal) + "\n}\n}\n"
            + ".method string Scenario() {\ncall Copy\n"
            + "callvirt instance object Task::get_AsyncState()\ncastclass string\nret\n}",
            "end of method Scenario");

        await TypeLineAsync(page, ".compare Copy using Scenario");
        await ExpectComparisonTextAsync(page, "Copy: different");
        var text = await BufferTextAsync(page);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.Contains("\"before\"", text);
        Assert.Contains("\"after\"", text);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    /// <summary>
    /// Queued task observations finish after the scenario completes and awaits the original task.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="generic">Whether the selected method declares the task's result type.</param>
    /// <returns>The completed asynchronous task identity assertions.</returns>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", false)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_PendingTaskComparisonPreservesIdentity(string browser, bool generic)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var task = "class Task" + (generic ? "`1<int32>" : "");
        await SubmitEditSourceAsync(page, $$"""
            .class public Tasks {
              .field public static class TaskCompletionSource`1<int32> Completion
              .method private static void .cctor() {
                ldc.i4.s 64
                newobj instance void class TaskCompletionSource`1<int32>::.ctor(valuetype TaskCreationOptions)
                stsfld class TaskCompletionSource`1<int32> Tasks::Completion
                ret
              }
              .method public static {{task}} Read() {
                ldsfld class TaskCompletionSource`1<int32> Tasks::Completion
                callvirt instance class Task`1<!0> class TaskCompletionSource`1<int32>::get_Task()
                ret
              }
            }
            .edit {{task}} Tasks::Read() as Copy {
              .method public static {{task}} Read() cil managed {
                ldsfld class TaskCompletionSource`1<int32> Tasks::Completion
                callvirt instance class Task`1<!0> class TaskCompletionSource`1<int32>::get_Task()
                ret
              }
            }
            .method {{task}} Scenario() {
              .locals init ({{task}} pending)
              call Copy
              stloc.0
              ldloc.0
              ldsfld class TaskCompletionSource`1<int32> IlRepl.Edits.Copy.Owner::Completion
              callvirt instance class Task`1<!0> class TaskCompletionSource`1<int32>::get_Task()
              call bool Object::ReferenceEquals(object, object)
              brtrue.s SAME
              ldstr "task identity changed"
              newobj instance void InvalidOperationException::.ctor(string)
              throw
            SAME:
              ldsfld class TaskCompletionSource`1<int32> IlRepl.Edits.Copy.Owner::Completion
              ldc.i4.s 42
              callvirt instance void class TaskCompletionSource`1<int32>::SetResult(!0)
              ldloc.0
              ret
            }
            """, "end of method Scenario");

        await TypeLineAsync(page, ".compare Copy using Scenario");
        await ExpectComparisonTextAsync(page, "Copy: match");
        var text = await BufferTextAsync(page);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.Contains("\"42\"", text);
        Assert.DoesNotContain("still running", text);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
