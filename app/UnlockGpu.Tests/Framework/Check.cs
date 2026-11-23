using System.Runtime.CompilerServices;

namespace UnlockGpu.Tests;

/// <summary>Assertion helpers.</summary>
public static class Check
{
    /// <summary>Fails unless the condition is true.</summary>
    public static void True(bool condition, [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (!condition) throw new AssertionException($"expected true: {expr}");
    }

    /// <summary>Fails unless the condition is false.</summary>
    public static void False(bool condition, [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (condition) throw new AssertionException($"expected false: {expr}");
    }

    /// <summary>Fails unless the values are equal.</summary>
    public static void Equal<T>(T expected, T actual, [CallerArgumentExpression(nameof(actual))] string? expr = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{expr}: expected <{expected}>, got <{actual}>");
    }

    /// <summary>Fails unless the doubles are within tolerance.</summary>
    public static void Near(double expected, double actual, double tolerance = 1e-9, [CallerArgumentExpression(nameof(actual))] string? expr = null)
    {
        if (double.IsNaN(expected) ? !double.IsNaN(actual) : Math.Abs(expected - actual) > tolerance)
            throw new AssertionException($"{expr}: expected {expected} +/- {tolerance}, got {actual}");
    }

    /// <summary>Fails unless the value is NaN.</summary>
    public static void NaN(double actual, [CallerArgumentExpression(nameof(actual))] string? expr = null)
    {
        if (!double.IsNaN(actual)) throw new AssertionException($"{expr}: expected NaN, got {actual}");
    }

    /// <summary>Fails unless the value is not null.</summary>
    public static T NotNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string? expr = null) where T : class =>
        value ?? throw new AssertionException($"{expr}: expected non-null");

    /// <summary>Fails unless the action throws <typeparamref name="TException"/>.</summary>
    public static TException Throws<TException>(Action action, [CallerArgumentExpression(nameof(action))] string? expr = null)
        where TException : Exception
    {
        try { action(); }
        catch (TException e) { return e; }
        catch (Exception e) { throw new AssertionException($"{expr}: expected {typeof(TException).Name}, got {e.GetType().Name}: {e.Message}"); }
        throw new AssertionException($"{expr}: expected {typeof(TException).Name}, nothing thrown");
    }

    /// <summary>Fails unless the text contains the fragment.</summary>
    public static void Contains(string fragment, string? text, [CallerArgumentExpression(nameof(text))] string? expr = null)
    {
        if (text == null || !text.Contains(fragment, StringComparison.Ordinal))
            throw new AssertionException($"{expr}: expected to contain \"{fragment}\", got \"{text}\"");
    }
}
