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

        int SetupAlarm(int userId, int level, int alarmInfoType, int deployType);

        bool CloseAlarm(int alarmHandle);

        bool GetAcsWorkStatus(int userId, int channel, out AcsWorkStatus status);

        bool ControlGateway(int userId, int gateIndex, GateControlCommand command);

        bool CaptureJpegPicture(int userId, int channel, int pictureQuality, string filePath);

        bool StandardXmlConfig(int userId, string requestUrl, string inputXml, out string outputXml);

        int UploadFaceData(int userId, string requestUrl, string jsonPayload, byte[] pictureBytes, out string responseBody);

        int CaptureFace(int userId, int maxAttempts, int waitIntervalMs, out byte[] faceImage, out byte faceQuality, out int errorCode);

        int GetLastError();

        string GetErrorMessage(int errorCode);
    }

    internal delegate bool HikvisionAlarmNativeCallback(int command, IntPtr alarmer, IntPtr alarmInfo, int alarmInfoLength, IntPtr userData);
}
