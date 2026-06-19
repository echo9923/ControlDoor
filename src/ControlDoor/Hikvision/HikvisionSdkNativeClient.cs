using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ControlDoor.Hikvision
{
    internal sealed class HikvisionSdkNativeClient : IHikvisionSdkNativeClient
    {
        private HikvisionAlarmNativeCallback callbackReference;

        public bool Init()
        {
            return NativeMethods.NET_DVR_Init();
        }

        public bool Cleanup()
        {
            return NativeMethods.NET_DVR_Cleanup();
        }

        public int Login(LoginRequest request, out DeviceInfo deviceInfo)
        {
            var loginInfo = new NativeMethods.NET_DVR_USER_LOGIN_INFO();
            loginInfo.Init();
            SetFixedBytes(loginInfo.sDeviceAddress, request.IpAddress);
            SetFixedBytes(loginInfo.sUserName, request.UserName);
            SetFixedBytes(loginInfo.sPassword, request.Password);
            loginInfo.wPort = (ushort)request.Port;
            loginInfo.bUseAsynLogin = false;
            loginInfo.byLoginMode = request.UseTcp ? (byte)0 : (byte)1;

            var nativeInfo = new NativeMethods.NET_DVR_DEVICEINFO_V40();
            var userId = NativeMethods.NET_DVR_Login_V40(ref loginInfo, ref nativeInfo);
            deviceInfo = ToDeviceInfo(nativeInfo, request.IpAddress);
            return userId;
        }

        public bool Logout(int userId)
        {
            return NativeMethods.NET_DVR_Logout(userId);
        }

        public bool SetMessageCallback(HikvisionAlarmNativeCallback callback)
        {
            callbackReference = callback;
            return NativeMethods.NET_DVR_SetDVRMessageCallBack_V50(0, callbackReference, IntPtr.Zero);
        }

        public int SetupAlarm(int userId, int level, int alarmInfoType, int deployType)
        {
            var setup = new NativeMethods.NET_DVR_SETUPALARM_PARAM
            {
                dwSize = Marshal.SizeOf(typeof(NativeMethods.NET_DVR_SETUPALARM_PARAM)),
                byLevel = (byte)level,
                byAlarmInfoType = (byte)alarmInfoType,
                byDeployType = (byte)deployType
            };
            return NativeMethods.NET_DVR_SetupAlarmChan_V41(userId, ref setup);
        }

        public bool CloseAlarm(int alarmHandle)
        {
            return NativeMethods.NET_DVR_CloseAlarmChan_V30(alarmHandle);
        }

        public bool GetAcsWorkStatus(int userId, int channel, out AcsWorkStatus status)
        {
            var nativeStatus = new NativeMethods.NET_DVR_ACS_WORK_STATUS_V50();
            nativeStatus.Init();
            nativeStatus.dwSize = (uint)Marshal.SizeOf(typeof(NativeMethods.NET_DVR_ACS_WORK_STATUS_V50));
            var size = Marshal.SizeOf(typeof(NativeMethods.NET_DVR_ACS_WORK_STATUS_V50));
            var ptr = IntPtr.Zero;
            uint returned = 0;
            try
            {
                ptr = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(nativeStatus, ptr, false);
                var ok = NativeMethods.NET_DVR_GetDVRConfig(
                    userId,
                    NativeMethods.NET_DVR_GET_ACS_WORK_STATUS_V50,
                    channel,
                    ptr,
                    (uint)size,
                    ref returned);
                if (ok)
                {
                    nativeStatus = (NativeMethods.NET_DVR_ACS_WORK_STATUS_V50)Marshal.PtrToStructure(
                        ptr,
                        typeof(NativeMethods.NET_DVR_ACS_WORK_STATUS_V50));
                }

                status = new AcsWorkStatus
                {
                    SetupAlarmStatus = nativeStatus.bySetupAlarmStatus == null
                        ? new byte[0]
                        : (byte[])nativeStatus.bySetupAlarmStatus.Clone()
                };
                return ok;
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }

        public bool ControlGateway(int userId, int gateIndex, GateControlCommand command)
        {
            return NativeMethods.NET_DVR_ControlGateway(userId, gateIndex, (int)command);
        }

        public bool CaptureJpegPicture(int userId, int channel, int pictureQuality, string filePath)
        {
            var jpegPara = new NativeMethods.NET_DVR_JPEGPARA
            {
                wPicQuality = (ushort)pictureQuality,
                wPicSize = 0xFF
            };
            return NativeMethods.NET_DVR_CaptureJPEGPicture(userId, channel, ref jpegPara, filePath);
        }

        public bool StandardXmlConfig(int userId, string requestUrl, string inputXml, out string outputXml)
        {
            var urlBytes = Encoding.UTF8.GetBytes(requestUrl ?? string.Empty);
            var inputBytes = Encoding.UTF8.GetBytes(inputXml ?? string.Empty);
            var outputBufferSize = 3 * 1024 * 1024;
            var statusBufferSize = 4096 * 4;

            var input = new NativeMethods.NET_DVR_XML_CONFIG_INPUT
            {
                dwSize = Marshal.SizeOf(typeof(NativeMethods.NET_DVR_XML_CONFIG_INPUT)),
                dwRequestUrlLen = (uint)urlBytes.Length,
                dwInBufferSize = (uint)inputBytes.Length,
                byRes = new byte[32]
            };
            var output = new NativeMethods.NET_DVR_XML_CONFIG_OUTPUT
            {
                dwSize = Marshal.SizeOf(typeof(NativeMethods.NET_DVR_XML_CONFIG_OUTPUT)),
                dwOutBufferSize = (uint)outputBufferSize,
                dwStatusSize = (uint)statusBufferSize,
                byRes = new byte[32]
            };

            IntPtr urlPtr = IntPtr.Zero;
            IntPtr inputPtr = IntPtr.Zero;
            IntPtr outputPtr = IntPtr.Zero;
            IntPtr statusPtr = IntPtr.Zero;
            try
            {
                urlPtr = AllocNullTerminatedUtf8Buffer(urlBytes);
                inputPtr = AllocNullTerminatedUtf8Buffer(inputBytes);
                outputPtr = Marshal.AllocHGlobal(outputBufferSize);
                statusPtr = Marshal.AllocHGlobal(statusBufferSize);

                input.lpRequestUrl = urlPtr;
                input.lpInBuffer = inputPtr;
                output.lpOutBuffer = outputPtr;
                output.lpStatusBuffer = statusPtr;

                var ok = NativeMethods.NET_DVR_STDXMLConfig(userId, ref input, ref output);
                var returnedSize = Math.Min((int)output.dwReturnedXMLSize, outputBufferSize);
                outputXml = returnedSize <= 0
                    ? ReadNullTerminatedUtf8(statusPtr, statusBufferSize)
                    : PtrToUtf8String(outputPtr, returnedSize);
                return ok;
            }
            finally
            {
                FreeHGlobal(urlPtr);
                FreeHGlobal(inputPtr);
                FreeHGlobal(outputPtr);
                FreeHGlobal(statusPtr);
            }
        }

        private static IntPtr AllocNullTerminatedUtf8Buffer(byte[] bytes)
        {
            var source = bytes ?? new byte[0];
            var ptr = Marshal.AllocHGlobal(source.Length + 1);
            if (source.Length > 0)
            {
                Marshal.Copy(source, 0, ptr, source.Length);
            }

            Marshal.WriteByte(ptr, source.Length, 0);
            return ptr;
        }

        private static string PtrToUtf8String(IntPtr ptr, int length)
        {
            if (ptr == IntPtr.Zero || length <= 0)
            {
                return string.Empty;
            }

            var buffer = new byte[length];
            Marshal.Copy(ptr, buffer, 0, length);
            return Encoding.UTF8.GetString(buffer).TrimEnd('\0');
        }

        private static string ReadNullTerminatedUtf8(IntPtr ptr, int maxLength)
        {
            if (ptr == IntPtr.Zero || maxLength <= 0)
            {
                return string.Empty;
            }

            var length = 0;
            while (length < maxLength && Marshal.ReadByte(ptr, length) != 0)
            {
                length++;
            }

            return PtrToUtf8String(ptr, length);
        }

        private static void FreeHGlobal(IntPtr ptr)
        {
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public int UploadFaceData(int userId, string requestUrl, string jsonPayload, byte[] pictureBytes, out string responseBody)
        {
            var urlBytes = Encoding.UTF8.GetBytes(requestUrl ?? string.Empty);
            var jsonBytes = Encoding.UTF8.GetBytes(jsonPayload ?? string.Empty);
            var pictureData = pictureBytes ?? new byte[0];
            var responseBuffer = new byte[2048];

            IntPtr urlPtr = IntPtr.Zero;
            IntPtr jsonPtr = IntPtr.Zero;
            IntPtr picturePtr = IntPtr.Zero;
            IntPtr configPtr = IntPtr.Zero;
            var handle = -1;
            responseBody = string.Empty;

            try
            {
                urlPtr = Marshal.AllocHGlobal(urlBytes.Length + 1);
                Marshal.Copy(urlBytes, 0, urlPtr, urlBytes.Length);
                Marshal.WriteByte(urlPtr, urlBytes.Length, 0);

                handle = NativeMethods.NET_DVR_StartRemoteConfig(
                    userId,
                    NativeMethods.NET_DVR_FACE_DATA_RECORD,
                    urlPtr,
                    urlBytes.Length,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (handle < 0)
                {
                    return -1;
                }

                if (jsonBytes.Length > 0)
                {
                    jsonPtr = Marshal.AllocHGlobal(jsonBytes.Length);
                    Marshal.Copy(jsonBytes, 0, jsonPtr, jsonBytes.Length);
                }

                if (pictureData.Length > 0)
                {
                    picturePtr = Marshal.AllocHGlobal(pictureData.Length);
                    Marshal.Copy(pictureData, 0, picturePtr, pictureData.Length);
                }

                var config = new NativeMethods.NET_DVR_JSON_DATA_CFG
                {
                    dwSize = (uint)Marshal.SizeOf(typeof(NativeMethods.NET_DVR_JSON_DATA_CFG)),
                    lpJsonData = jsonPtr,
                    dwJsonDataSize = (uint)jsonBytes.Length,
                    lpPicData = picturePtr,
                    dwPicDataSize = (uint)pictureData.Length,
                    byRes = new byte[256]
                };

                var configSize = Marshal.SizeOf(typeof(NativeMethods.NET_DVR_JSON_DATA_CFG));
                configPtr = Marshal.AllocHGlobal(configSize);
                Marshal.StructureToPtr(config, configPtr, false);

                var responseHandle = GCHandle.Alloc(responseBuffer, GCHandleType.Pinned);
                try
                {
                    uint responseSize = 0;
                    var status = NativeMethods.NET_DVR_SendWithRecvRemoteConfig(
                        handle,
                        configPtr,
                        (uint)configSize,
                        responseHandle.AddrOfPinnedObject(),
                        (uint)responseBuffer.Length,
                        ref responseSize);

                    responseBody = ReadUtf8String(responseBuffer, responseSize);
                    return status;
                }
                finally
                {
                    responseHandle.Free();
                }
            }
            finally
            {
                if (handle >= 0)
                {
                    NativeMethods.NET_DVR_StopRemoteConfig(handle);
                }

                if (urlPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(urlPtr);
                }

                if (jsonPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(jsonPtr);
                }

                if (picturePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(picturePtr);
                }

                if (configPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(configPtr);
                }
            }
        }

        public int CaptureFace(int userId, int maxAttempts, int waitIntervalMs, CancellationToken cancellationToken, out byte[] faceImage, out byte faceQuality, out int errorCode)
        {
            faceImage = null;
            faceQuality = 0;
            errorCode = 0;
            cancellationToken.ThrowIfCancellationRequested();

            var cond = new NativeMethods.NET_DVR_CAPTURE_FACE_COND();
            cond.Init();
            cond.dwSize = Marshal.SizeOf(typeof(NativeMethods.NET_DVR_CAPTURE_FACE_COND));

            IntPtr condPtr = IntPtr.Zero;
            var handle = -1;

            try
            {
                condPtr = Marshal.AllocHGlobal(cond.dwSize);
                Marshal.StructureToPtr(cond, condPtr, false);

                handle = NativeMethods.NET_DVR_StartRemoteConfig(
                    userId,
                    NativeMethods.NET_DVR_CAPTURE_FACE_INFO,
                    condPtr,
                    cond.dwSize,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (handle < 0)
                {
                    errorCode = NativeMethods.NET_DVR_GetLastError();
                    return -1;
                }

                var cfgSize = Marshal.SizeOf(typeof(NativeMethods.NET_DVR_CAPTURE_FACE_CFG));
                for (var attempt = 0; attempt < maxAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var faceCfg = new NativeMethods.NET_DVR_CAPTURE_FACE_CFG();
                    faceCfg.Init();
                    faceCfg.dwSize = cfgSize;

                    var status = NativeMethods.NET_DVR_GetNextRemoteConfig(handle, ref faceCfg, cfgSize);

                    if (status == NativeMethods.NET_SDK_GET_NEXT_STATUS_SUCCESS)
                    {
                        if (faceCfg.byCaptureProgress == 100)
                        {
                            if (faceCfg.dwFacePicSize > 0 && faceCfg.pFacePicBuffer != IntPtr.Zero)
                            {
                                faceImage = new byte[faceCfg.dwFacePicSize];
                                Marshal.Copy(faceCfg.pFacePicBuffer, faceImage, 0, faceCfg.dwFacePicSize);
                                faceQuality = faceCfg.byFaceQuality1;
                                return NativeMethods.NET_SDK_GET_NEXT_STATUS_SUCCESS;
                            }

                            return NativeMethods.NET_SDK_GET_NEXT_STATUS_FINISH;
                        }

                        continue;
                    }

                    if (status == NativeMethods.NET_SDK_GET_NEXT_STATUS_NEED_WAIT)
                    {
                        if (cancellationToken.WaitHandle.WaitOne(Math.Max(1, waitIntervalMs)))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        continue;
                    }

                    if (status == NativeMethods.NET_SDK_GET_NEXT_STATUS_FINISH)
                    {
                        return NativeMethods.NET_SDK_GET_NEXT_STATUS_FINISH;
                    }

                    if (status == NativeMethods.NET_SDK_GET_NEXT_STATUS_FAILED)
                    {
                        errorCode = NativeMethods.NET_DVR_GetLastError();
                        return NativeMethods.NET_SDK_GET_NEXT_STATUS_FAILED;
                    }

                    errorCode = NativeMethods.NET_DVR_GetLastError();
                    return NativeMethods.NET_SDK_GET_NEXT_STATUS_FAILED;
                }

                // 循环跑满仍未拿到人脸，按超时处理（返回 NEED_WAIT 由上层判定）。
                return NativeMethods.NET_SDK_GET_NEXT_STATUS_NEED_WAIT;
            }
            finally
            {
                if (handle >= 0)
                {
                    NativeMethods.NET_DVR_StopRemoteConfig(handle);
                }

                if (condPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(condPtr);
                }
            }
        }

        public int GetLastError()
        {
            return NativeMethods.NET_DVR_GetLastError();
        }

        public string GetErrorMessage(int errorCode)
        {
            var code = errorCode;
            var pointer = NativeMethods.NET_DVR_GetErrorMsg(ref code);
            if (pointer == IntPtr.Zero)
            {
                return SdkError.GetDefaultMessage(errorCode);
            }

            var message = Marshal.PtrToStringAnsi(pointer);
            return string.IsNullOrWhiteSpace(message) ? SdkError.GetDefaultMessage(errorCode) : message;
        }

        private static DeviceInfo ToDeviceInfo(NativeMethods.NET_DVR_DEVICEINFO_V40 info, string ipAddress)
        {
            return new DeviceInfo
            {
                SerialNumber = GetFixedString(info.struDeviceV30.sSerialNumber),
                ChannelCount = info.struDeviceV30.byChanNum,
                IpAddress = ipAddress
            };
        }

        private static string GetFixedString(byte[] value)
        {
            if (value == null)
            {
                return null;
            }

            var length = Array.IndexOf(value, (byte)0);
            if (length < 0)
            {
                length = value.Length;
            }

            return Encoding.Default.GetString(value, 0, length);
        }

        private static void SetFixedBytes(byte[] target, string value)
        {
            if (target == null)
            {
                return;
            }

            Array.Clear(target, 0, target.Length);
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var bytes = Encoding.Default.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, target, 0, Math.Min(bytes.Length, target.Length - 1));
        }

        private static string ReadUtf8String(byte[] buffer, uint length)
        {
            if (buffer == null || buffer.Length == 0)
            {
                return string.Empty;
            }

            var count = length > 0 ? Math.Min((int)length, buffer.Length) : Array.IndexOf(buffer, (byte)0);
            if (count < 0)
            {
                count = buffer.Length;
            }

            return count == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, count).TrimEnd('\0');
        }

        private static class NativeMethods
        {
            public const uint NET_DVR_GET_ACS_WORK_STATUS_V50 = 2180;
            private const int MAX_DOOR_NUM_256 = 256;
            private const int MAX_CASE_SENSOR_NUM = 8;
            private const int MAX_CARD_READER_NUM_512 = 512;
            private const int MAX_ALARMHOST_ALARMIN_NUM = 512;
            private const int MAX_ALARMHOST_ALARMOUT_NUM = 512;
            public const uint NET_DVR_FACE_DATA_RECORD = 2551;
            public const uint NET_DVR_CAPTURE_FACE_INFO = 2510;
            public const int NET_SDK_GET_NEXT_STATUS_SUCCESS = 1000;
            public const int NET_SDK_GET_NEXT_STATUS_NEED_WAIT = 1001;
            public const int NET_SDK_GET_NEXT_STATUS_FINISH = 1002;
            public const int NET_SDK_GET_NEXT_STATUS_FAILED = 1003;

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern bool NET_DVR_Init();

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern bool NET_DVR_Cleanup();

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int NET_DVR_Login_V40(ref NET_DVR_USER_LOGIN_INFO loginInfo, ref NET_DVR_DEVICEINFO_V40 deviceInfo);

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern bool NET_DVR_Logout(int userId);

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern bool NET_DVR_SetDVRMessageCallBack_V50(int index, HikvisionAlarmNativeCallback callback, IntPtr userData);

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int NET_DVR_SetupAlarmChan_V41(int userId, ref NET_DVR_SETUPALARM_PARAM setupParam);

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern bool NET_DVR_CloseAlarmChan_V30(int alarmHandle);

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern bool NET_DVR_ControlGateway(int userId, int gatewayIndex, int command);

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern bool NET_DVR_CaptureJPEGPicture(int userId, int channel, ref NET_DVR_JPEGPARA jpegPara, [MarshalAs(UnmanagedType.LPStr)] string filePath);

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern bool NET_DVR_GetDVRConfig(int userId, uint command, int channel, IntPtr outputBuffer, uint outputBufferSize, ref uint bytesReturned);

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern bool NET_DVR_STDXMLConfig(int userId, ref NET_DVR_XML_CONFIG_INPUT input, ref NET_DVR_XML_CONFIG_OUTPUT output);

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int NET_DVR_StartRemoteConfig(int userId, uint command, IntPtr inputBuffer, int inputBufferLength, IntPtr stateCallback, IntPtr userData);

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern bool NET_DVR_StopRemoteConfig(int handle);

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int NET_DVR_SendWithRecvRemoteConfig(int handle, IntPtr inputBuffer, uint inputBufferSize, IntPtr outputBuffer, uint outputBufferSize, ref uint outputDataLength);

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int NET_DVR_GetNextRemoteConfig(int handle, ref NET_DVR_CAPTURE_FACE_CFG outputBuffer, int outputBufferSize);

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int NET_DVR_GetLastError();

            [DllImport("HCNetSDK.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern IntPtr NET_DVR_GetErrorMsg(ref int errorCode);

            [StructLayout(LayoutKind.Sequential)]
            public struct NET_DVR_USER_LOGIN_INFO
            {
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 129)]
                public byte[] sDeviceAddress;

                public byte byUseTransport;

                public ushort wPort;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
                public byte[] sUserName;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
                public byte[] sPassword;

                public IntPtr cbLoginResult;

                public IntPtr pUser;

                public bool bUseAsynLogin;

                public byte byProxyType;

                public byte byUseUTCTime;

                public byte byLoginMode;

                public byte byHttps;

                public int iProxyID;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 119)]
                public byte[] byRes2;

                public void Init()
                {
                    sDeviceAddress = new byte[129];
                    sUserName = new byte[64];
                    sPassword = new byte[64];
                    byRes2 = new byte[119];
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct NET_DVR_DEVICEINFO_V40
            {
                public NET_DVR_DEVICEINFO_V30 struDeviceV30;

                public byte bySupportLock;

                public byte byRetryLoginTime;

                public byte byPasswordLevel;

                public byte byProxyType;

                public int dwSurplusLockTime;

                public byte byCharEncodeType;

                public byte bySupportDev5;

                public byte byLoginMode;

                public byte byRes2;

                public int dwOEMCode;

                public int iResidualValidity;

                public byte byResidualValidity;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 243)]
                public byte[] byRes;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct NET_DVR_DEVICEINFO_V30
            {
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
                public byte[] sSerialNumber;

                public byte byAlarmInPortNum;

                public byte byAlarmOutPortNum;

                public byte byDiskNum;

                public byte byDVRType;

                public byte byChanNum;

                public byte byStartChan;

                public byte byAudioChanNum;

                public byte byIPChanNum;

                public byte byZeroChanNum;

                public byte byMainProto;

                public byte bySubProto;

                public byte bySupport;

                public byte bySupport1;

                public byte bySupport2;

                public ushort wDevType;

                public byte bySupport3;

                public byte byMultiStreamProto;

                public byte byStartDChan;

                public byte byStartDTalkChan;

                public byte byHighDChanNum;

                public byte bySupport4;

                public byte byLanguageType;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
                public byte[] byRes2;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct NET_DVR_SETUPALARM_PARAM
            {
                public int dwSize;

                public byte byLevel;

                public byte byAlarmInfoType;

                public byte byRetAlarmTypeV40;

                public byte byRetDevInfoVersion;

                public byte byRetVQDAlarmType;

                public byte byFaceAlarmDetection;

                public byte bySupport;

                public byte byBrokenNetHttp;

                public ushort wTaskNo;

                public byte byDeployType;

                public byte bySubScription;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
                public byte[] byRes1;

                public byte byAlarmTypeURL;

                public byte byCustomCtrl;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct NET_DVR_JPEGPARA
            {
                public ushort wPicSize;

                public ushort wPicQuality;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct NET_DVR_ACS_WORK_STATUS_V50
            {
                public uint dwSize;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_DOOR_NUM_256)]
                public byte[] byDoorLockStatus;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_DOOR_NUM_256)]
                public byte[] byDoorStatus;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_DOOR_NUM_256)]
                public byte[] byMagneticStatus;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_CASE_SENSOR_NUM)]
                public byte[] byCaseStatus;

                public ushort wBatteryVoltage;

                public byte byBatteryLowVoltage;

                public byte byPowerSupplyStatus;

                public byte byMultiDoorInterlockStatus;

                public byte byAntiSneakStatus;

                public byte byHostAntiDismantleStatus;

                public byte byIndicatorLightStatus;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_CARD_READER_NUM_512)]
                public byte[] byCardReaderOnlineStatus;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_CARD_READER_NUM_512)]
                public byte[] byCardReaderAntiDismantleStatus;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_CARD_READER_NUM_512)]
                public byte[] byCardReaderVerifyMode;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_ALARMHOST_ALARMIN_NUM)]
                public byte[] bySetupAlarmStatus;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_ALARMHOST_ALARMIN_NUM)]
                public byte[] byAlarmInStatus;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_ALARMHOST_ALARMOUT_NUM)]
                public byte[] byAlarmOutStatus;

                public uint dwCardNum;

                public byte byFireAlarmStatus;

                public byte byBatteryChargeStatus;

                public byte byMasterChannelControllerStatus;

                public byte bySlaveChannelControllerStatus;

                public byte byAntiSneakServerStatus;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
                public byte[] byRes3;

                public uint dwWhiteFaceNum;

                public uint dwBlackFaceNum;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 108)]
                public byte[] byRes2;

                public void Init()
                {
                    byDoorLockStatus = new byte[MAX_DOOR_NUM_256];
                    byDoorStatus = new byte[MAX_DOOR_NUM_256];
                    byMagneticStatus = new byte[MAX_DOOR_NUM_256];
                    byCaseStatus = new byte[MAX_CASE_SENSOR_NUM];
                    byCardReaderOnlineStatus = new byte[MAX_CARD_READER_NUM_512];
                    byCardReaderAntiDismantleStatus = new byte[MAX_CARD_READER_NUM_512];
                    byCardReaderVerifyMode = new byte[MAX_CARD_READER_NUM_512];
                    bySetupAlarmStatus = new byte[MAX_ALARMHOST_ALARMIN_NUM];
                    byAlarmInStatus = new byte[MAX_ALARMHOST_ALARMIN_NUM];
                    byAlarmOutStatus = new byte[MAX_ALARMHOST_ALARMOUT_NUM];
                    byRes3 = new byte[3];
                    byRes2 = new byte[108];
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct NET_DVR_XML_CONFIG_INPUT
            {
                public int dwSize;

                public IntPtr lpRequestUrl;

                public uint dwRequestUrlLen;

                public IntPtr lpInBuffer;

                public uint dwInBufferSize;

                public uint dwRecvTimeOut;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
                public byte[] byRes;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct NET_DVR_XML_CONFIG_OUTPUT
            {
                public int dwSize;

                public IntPtr lpOutBuffer;

                public uint dwOutBufferSize;

                public uint dwReturnedXMLSize;

                public IntPtr lpStatusBuffer;

                public uint dwStatusSize;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
                public byte[] byRes;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct NET_DVR_JSON_DATA_CFG
            {
                public uint dwSize;

                public IntPtr lpJsonData;

                public uint dwJsonDataSize;

                public IntPtr lpPicData;

                public uint dwPicDataSize;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
                public byte[] byRes;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct NET_DVR_CAPTURE_FACE_COND
            {
                public int dwSize;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
                public byte[] byRes;

                public void Init()
                {
                    byRes = new byte[128];
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct NET_DVR_CAPTURE_FACE_CFG
            {
                public int dwSize;

                public int dwFaceTemplate1Size;

                public IntPtr pFaceTemplate1Buffer;

                public int dwFaceTemplate2Size;

                public IntPtr pFaceTemplate2Buffer;

                public int dwFacePicSize;

                public IntPtr pFacePicBuffer;

                public byte byFaceQuality1;

                public byte byFaceQuality2;

                public byte byCaptureProgress;

                public byte byRes1;

                public int dwInfraredFacePicSize;

                public IntPtr pInfraredFacePicBuffer;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 116)]
                public byte[] byRes;

                public void Init()
                {
                    byRes = new byte[116];
                }
            }
        }
    }
}
