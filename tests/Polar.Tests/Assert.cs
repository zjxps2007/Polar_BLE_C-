using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Polar.Tests
{
    internal static class Assert
    {
        public static int Count { get; private set; }
        public static void True(bool value, string? message = null) { Count++; if (!value) throw new Exception(message ?? "Expected true."); }
        public static void False(bool value, string? message = null) => True(!value, message ?? "Expected false.");
        public static void Equal<T>(T expected, T actual, string? message = null)
        { Count++; if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(message ?? $"Expected {expected}; actual {actual}."); }
        public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? message = null)
        { Count++; if (!expected.SequenceEqual(actual)) throw new Exception(message ?? "Sequences differ."); }
        public static T Throws<T>(Action action) where T : Exception
        { Count++; try { action(); } catch (T e) { return e; } throw new Exception("Expected " + typeof(T).Name); }
        public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
        { Count++; try { await action(); } catch (T e) { return e; } throw new Exception("Expected " + typeof(T).Name); }
    }
}
