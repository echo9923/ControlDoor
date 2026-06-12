namespace ControlEntradaSalida.Tests
{
    internal static class Stage5TestData
    {
        public static byte[] JpegBytes()
        {
            return new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9 };
        }

        public static string JpegBase64()
        {
            return System.Convert.ToBase64String(JpegBytes());
        }
    }
}
