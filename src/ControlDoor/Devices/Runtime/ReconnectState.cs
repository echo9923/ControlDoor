using System;

namespace ControlDoor.Devices.Runtime
{
    public sealed class ReconnectState
    {
        public int AttemptCount { get; set; }

        public DateTime? NextReconnectAt { get; set; }

        public DateTime? LastAttemptAt { get; set; }

        public DateTime? LastSuccessAt { get; set; }

        public bool InCooldown { get; set; }

        public string CooldownReason { get; set; }

        public bool ManualDisconnected { get; set; }

        public static ReconnectState New()
        {
            return new ReconnectState();
        }

        public ReconnectState Clone()
        {
            return new ReconnectState
            {
                AttemptCount = AttemptCount,
                NextReconnectAt = NextReconnectAt,
                LastAttemptAt = LastAttemptAt,
                LastSuccessAt = LastSuccessAt,
                InCooldown = InCooldown,
                CooldownReason = CooldownReason,
                ManualDisconnected = ManualDisconnected
            };
        }
    }
}
