namespace ControlDoor.CameraDoorInterlock
{
    /// <summary>
    /// AIOP 报警事件消费入口（镜像 FaceEvents.IRawAcsAlarmEventSink）。
    /// 由 CameraDoorInterlockService 实现，在后台扫描线程消费事件。
    /// </summary>
    public interface IAiopAlarmEventSink
    {
        AiopAlarmEnqueueResult TryEnqueue(RawAiopAlarmEvent alarmEvent);
    }
}
