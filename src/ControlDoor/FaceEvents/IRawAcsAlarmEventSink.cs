namespace ControlDoor.FaceEvents
{
    public interface IRawAcsAlarmEventSink
    {
        FaceEventEnqueueResult TryEnqueue(RawAcsAlarmEvent alarmEvent);
    }
}
