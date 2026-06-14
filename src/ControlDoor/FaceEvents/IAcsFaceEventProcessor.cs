using System.Collections.Generic;

namespace ControlDoor.FaceEvents
{
    public interface IAcsFaceEventProcessor
    {
        FaceEventProcessResult Process(RawAcsAlarmEvent rawEvent);

        IReadOnlyList<FaceEventBatchItemResult> ProcessBatch(IReadOnlyList<RawAcsAlarmEvent> rawEvents);
    }
}
