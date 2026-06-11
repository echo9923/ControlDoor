using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ControlEntradaSalida.Tests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var filter = args != null && args.Length > 0 ? args[0] : string.Empty;
            var failures = new List<string>();
            var tests = Assembly.GetExecutingAssembly()
                .GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(method => method.GetCustomAttributes(typeof(TestCaseAttribute), false).Any())
                .Where(method => string.IsNullOrWhiteSpace(filter) ||
                    (method.DeclaringType.FullName + "." + method.Name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(method => method.DeclaringType.FullName)
                .ThenBy(method => method.Name)
                .ToList();

            foreach (var test in tests)
            {
                try
                {
                    Console.WriteLine("[RUN] " + test.DeclaringType.Name + "." + test.Name);
                    test.Invoke(null, null);
                    Console.WriteLine("[PASS] " + test.DeclaringType.Name + "." + test.Name);
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    failures.Add(test.DeclaringType.Name + "." + test.Name + ": " + inner.Message);
                    Console.WriteLine("[FAIL] " + test.DeclaringType.Name + "." + test.Name);
                    Console.WriteLine(inner);
                }
            }

            Console.WriteLine($"Total: {tests.Count}, Failed: {failures.Count}");
            if (failures.Count == 0)
            {
                return 0;
            }

            Console.WriteLine("Failures:");
            foreach (var failure in failures)
            {
                Console.WriteLine(" - " + failure);
            }

            return 1;
        }
    }
}
