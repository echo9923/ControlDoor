using System;

namespace ControlDoor.Hikvision
{
    public sealed class MockGatewayCall
    {
        public string MethodName { get; set; }

        public object Request { get; set; }

        public DateTime CalledAt { get; set; }
    }
}
