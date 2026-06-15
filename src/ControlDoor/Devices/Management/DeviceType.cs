namespace ControlDoor.Devices.Management
{
    // 设备声明态类型。JSON 设备清单按此枚举标记每台设备
    // 应承担的角色，用于启动期分类与配置校验；运行时能力探测（DeviceCapabilities）
    // 仍然保留，二者互不替代。
    //
    // 取值与 DeviceCapability 对齐，但只保留三类对外角色：
    //   Acs          门禁设备          对应运行时 SupportsAcs
    //   FaceCapture  人脸录入仪/明眸    对应运行时 SupportsFaceCapture
    //   Camera       摄像头/AIOP       对应运行时 SupportsAiop
    // 明眸这类复合设备可同时声明 Acs 与 FaceCapture。
    public enum DeviceType
    {
        Acs = 0,

        FaceCapture = 1,

        Camera = 2
    }
}
