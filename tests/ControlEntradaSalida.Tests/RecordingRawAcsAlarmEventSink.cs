using System.Collections.Generic;
using ControlDoor.FaceEvents;

namespace ControlEntradaSalida.Tests
{
    public sealed class RecordingRawAcsAlarmEventSink : IRawAcsAlarmEventSink
    {
        public IList<RawAcsAlarmEvent> Events { get; } = new List<RawAcsAlarmEvent>();

        public FaceEventEnqueueResult TryEnqueue(RawAcsAlarmEvent alarmEvent)
        {
            Events.Add(alarmEvent);
            return FaceEventEnqueueResult.AcceptedResult(Events.Count, 100);
        }
    }
}
