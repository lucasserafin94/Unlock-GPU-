using System.Diagnostics;
using System.Reflection;

namespace UnlockGpu.Tests;

/// <summary>Marks a public parameterless method of a public class as a test.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute;

/// <summary>Thrown by <see cref="Check"/> when an assertion fails.</summary>
public sealed class AssertionException(string message) : Exception(message);

/// <summary>Discovers and runs [Test] methods; one fresh instance per test, IDisposable honoured.</summary>
public static class TestRunner
{
    /// <summary>Runs all tests of an assembly; returns the number of failures (process exit code).</summary>
    public static int Run(Assembly assembly, string? filter)
    {
        var tests = assembly.GetTypes()
            .Where(t => t.IsClass && t.IsPublic && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null && m.GetParameters().Length == 0)
                .Select(m => (Type: t, Method: m)))
            .Where(x => filter == null || $"{x.Type.Name}.{x.Method.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Type.Name, StringComparer.Ordinal).ThenBy(x => x.Method.Name, StringComparer.Ordinal)
            .ToList();

        int failed = 0;
        var sw = Stopwatch.StartNew();
        foreach (var (type, method) in tests)
        {
            string name = $"{type.Name}.{method.Name}";
            object? instance = null;
            try
            {
                instance = Activator.CreateInstance(type);
                method.Invoke(instance, null);
                Console.WriteLine($"  ok    {name}");
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name}: {Describe(e.InnerException)}");
            }
            catch (Exception e)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name}: {Describe(e)}");
            }
            finally
            {
                (instance as IDisposable)?.Dispose();
            }
        }
        Console.WriteLine();
        Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed, {failed} failed ({sw.Elapsed.TotalSeconds:0.0} s)");
        return failed;
    }

    static string Describe(Exception e) => e is AssertionException ? e.Message : $"{e.GetType().Name}: {e.Message}\n{e.StackTrace}";
}
