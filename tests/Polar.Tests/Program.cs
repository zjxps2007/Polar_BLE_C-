using System;
using System.Diagnostics;
using System.Reflection;

namespace Polar.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            var timer = Stopwatch.StartNew(); int suites = 0;
            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                if (!type.Name.EndsWith("Tests", StringComparison.Ordinal)) continue;
                var run = type.GetMethod("Run", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (run == null) continue;
                try { run.Invoke(null, null); suites++; Console.WriteLine("PASS " + type.Name); }
                catch (TargetInvocationException ex) { Console.Error.WriteLine("FAIL " + type.Name + ": " + ex.InnerException); return 1; }
            }
            Console.WriteLine($"Passed {suites} suites, {Assert.Count} assertions in {timer.Elapsed.TotalSeconds:F2}s. Hardware not exercised.");
            return suites == 0 ? 1 : 0;
        }
    }
}
