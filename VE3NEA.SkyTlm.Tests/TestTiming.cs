using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit.Sdk;

// applied assembly-wide: xUnit runs Before/After around every test method in this assembly.
[assembly: VE3NEA.SkyTlm.Tests.TestTiming]

namespace VE3NEA.SkyTlm.Tests
{
  /// <summary>
  /// Times every test in the assembly and prints a summary table (slowest first) once the
  /// test process exits. Applied once via the assembly-level attribute above.
  /// </summary>
  [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
  public sealed class TestTimingAttribute : BeforeAfterTestAttribute
  {
    // one stopwatch per worker thread; Before/After for a given test run on the same thread.
    [ThreadStatic] private static Stopwatch? Timer;

    // results accumulate across parallel collections, so a concurrent collection is required.
    private static readonly ConcurrentBag<(string Name, double Ms)> Results = new();

    // ensure the table is printed exactly once, after all tests have finished.
    private static int TablePrinted;

    static TestTimingAttribute()
    {
      AppDomain.CurrentDomain.ProcessExit += (_, _) => PrintTable();
    }

    public override void Before(MethodInfo methodUnderTest)
    {
      Timer = Stopwatch.StartNew();
    }

    public override void After(MethodInfo methodUnderTest)
    {
      Timer?.Stop();
      string name = $"{methodUnderTest.DeclaringType?.Name}.{methodUnderTest.Name}";
      Results.Add((name, Timer?.Elapsed.TotalMilliseconds ?? 0.0));
    }


    // ----------------------------------------------------------------------------------------------------
    //                                            table output
    // ----------------------------------------------------------------------------------------------------
    private static void PrintTable()
    {
      // run only on the first ProcessExit, and only if at least one test was timed.
      if (Interlocked.Exchange(ref TablePrinted, 1) != 0) return;
      var rows = Results.OrderByDescending(r => r.Ms).ToList();
      if (rows.Count == 0) return;

      int nameWidth = Math.Max(4, rows.Max(r => r.Name.Length));
      double total = rows.Sum(r => r.Ms);

      var sb = new StringBuilder();
      sb.AppendLine();
      sb.AppendLine($"Test execution times ({rows.Count} tests, {total:N1} ms total):");
      sb.AppendLine($"  {"Test".PadRight(nameWidth)}  {"Time (ms)",10}");
      sb.AppendLine($"  {new string('-', nameWidth)}  {new string('-', 10)}");
      foreach (var r in rows)
        sb.AppendLine($"  {r.Name.PadRight(nameWidth)}  {r.Ms,10:N2}");
      sb.AppendLine($"  {"TOTAL".PadRight(nameWidth)}  {total,10:N2}");

      // console output is visible when the test assembly is run directly; under `dotnet test`
      // the VSTest host swallows it, so also write the table to a file next to the assembly.
      string path = Path.Combine(AppContext.BaseDirectory, "test-timings.txt");
      try { File.WriteAllText(path, sb.ToString()); } catch { /* best effort */ }
      sb.AppendLine($"  (written to {path})");

      Console.WriteLine(sb.ToString());
    }
  }
}
