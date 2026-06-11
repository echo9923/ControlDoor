using System;

namespace ControlDoor.Hikvision
{
    internal interface IHikvisionSdkNativeClient
    {
        bool Init();

        bool Cleanup();

        int Login(LoginRequest request, out DeviceInfo deviceInfo);

        bool Logout(int userId);

        bool SetMessageCallback(HikvisionAlarmNativeCallback callback);

        int SetupAlarm(int userId, int level, int alarmInfoType);

        bool CloseAlarm(int alarmHandle);

        bool ControlGateway(int userId, int gateIndex, GateControlCommand command);

        bool CaptureJpegPicture(int userId, int channel, int pictureQuality, string filePath);

        bool StandardXmlConfig(int userId, string requestUrl, string inputXml, out string outputXml);

        int GetLastError();

        string GetErrorMessage(int errorCode);
    }

    internal delegate bool HikvisionAlarmNativeCallback(int command, IntPtr alarmer, IntPtr alarmInfo, int alarmInfoLength, IntPtr userData);
}
