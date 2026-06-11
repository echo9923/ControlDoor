using System;
using System.Reflection;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    internal static class Stage3TestReflection
    {
        public static T Expect<T>(Action action)
            where T : Exception
        {
            try
            {
                action();
            }
            catch (T ex)
            {
                return ex;
            }

            throw new InvalidOperationException("Expected exception: " + typeof(T).Name);
        }

        public static string Serialize(object value)
        {
            var type = typeof(HikvisionSdkWrapper).Assembly.GetType("ControlDoor.Hikvision.HikvisionGatewayJson");
            var method = type.GetMethod("Serialize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return (string)method.Invoke(null, new[] { value });
        }

        public static T Deserialize<T>(string json)
        {
            var type = typeof(HikvisionSdkWrapper).Assembly.GetType("ControlDoor.Hikvision.HikvisionGatewayJson");
            var method = type.GetMethod("Deserialize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return (T)method.MakeGenericMethod(typeof(T)).Invoke(null, new object[] { json });
        }

        public static byte[] JpegBytes()
        {
            return new byte[] { 0xFF, 0xD8, 0x10, 0x20, 0xFF, 0xD9 };
        }
    }
}
