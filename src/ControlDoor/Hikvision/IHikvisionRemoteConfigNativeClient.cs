using System;

namespace ControlDoor.Hikvision
{
    internal interface IHikvisionRemoteConfigNativeClient
    {
        int Start(int userId, uint command, IntPtr inputBuffer, int inputBufferLength);

        int Send(int handle, IntPtr inputBuffer, uint inputBufferSize, IntPtr outputBuffer, uint outputBufferSize, ref uint outputDataLength);

        bool Stop(int handle);
    }
}
