namespace ControlDoor.FaceEvents
{
    public interface IAcsFaceEventProcessor
    {
        FaceEventProcessResult Process(RawAcsAlarmEvent rawEvent);
    }
}
