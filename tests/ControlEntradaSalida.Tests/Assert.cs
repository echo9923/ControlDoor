using System;
using System.Collections.Generic;

namespace ControlEntradaSalida.Tests
{
    public static class Assert
    {
        public static void True(bool condition, string message = null)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message ?? "Expected true.");
            }
        }

        public static void False(bool condition, string message = null)
        {
            if (condition)
            {
                throw new InvalidOperationException(message ?? "Expected false.");
            }
        }

        public static void Equal<T>(T expected, T actual, string message = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(message ?? $"Expected <{expected}>, actual <{actual}>.");
            }
        }

        public static void Contains(string expected, string actual, string message = null)
        {
            if (actual == null || !actual.Contains(expected))
            {
                throw new InvalidOperationException(message ?? $"Expected text containing <{expected}>.");
            }
        }

        public static void NotNull(object value, string message = null)
        {
            if (value == null)
            {
                throw new InvalidOperationException(message ?? "Expected non-null value.");
            }
        }
    }
}
