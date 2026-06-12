using System.Text;
using Grpc.Core;

namespace ControlDoor.GrpcApi
{
    public sealed class AccessControlGrpcBinder
    {
        private static readonly Marshaller<string> StringMarshaller = Marshallers.Create(
            value => Encoding.UTF8.GetBytes(value ?? string.Empty),
            bytes => Encoding.UTF8.GetString(bytes ?? new byte[0]));

        private readonly AccessControlGrpcService service;

        public AccessControlGrpcBinder(AccessControlGrpcService service)
        {
            this.service = service;
        }

        public ServerServiceDefinition Bind()
        {
            return ServerServiceDefinition.CreateBuilder()
                .AddMethod(CreateMethod("GetDeviceStatus"), HandleGetDeviceStatus)
                .AddMethod(CreateMethod("AddDevice"), HandleAddDevice)
                .AddMethod(CreateMethod("DeleteDevice"), HandleDeleteDevice)
                .AddMethod(CreateMethod("DisconnectDevice"), HandleDisconnectDevice)
                .AddMethod(CreateMethod("ReconnectDevice"), HandleReconnectDevice)
                .Build();
        }

        private static Method<string, string> CreateMethod(string methodName)
        {
            return new Method<string, string>(
                MethodType.Unary,
                AccessControlGrpcService.ServiceName,
                methodName,
                StringMarshaller,
                StringMarshaller);
        }

        private System.Threading.Tasks.Task<string> HandleGetDeviceStatus(string request, ServerCallContext context)
        {
            return System.Threading.Tasks.Task.FromResult(service.GetDeviceStatus(request, ToContext(context)));
        }

        private System.Threading.Tasks.Task<string> HandleAddDevice(string request, ServerCallContext context)
        {
            return System.Threading.Tasks.Task.FromResult(service.AddDevice(request, ToContext(context)));
        }

        private System.Threading.Tasks.Task<string> HandleDeleteDevice(string request, ServerCallContext context)
        {
            return System.Threading.Tasks.Task.FromResult(service.DeleteDevice(request, ToContext(context)));
        }

        private System.Threading.Tasks.Task<string> HandleDisconnectDevice(string request, ServerCallContext context)
        {
            return System.Threading.Tasks.Task.FromResult(service.DisconnectDevice(request, ToContext(context)));
        }

        private System.Threading.Tasks.Task<string> HandleReconnectDevice(string request, ServerCallContext context)
        {
            return System.Threading.Tasks.Task.FromResult(service.ReconnectDevice(request, ToContext(context)));
        }

        private static GrpcRequestContext ToContext(ServerCallContext callContext)
        {
            var context = new GrpcRequestContext();
            foreach (var entry in callContext.RequestHeaders)
            {
                context.Metadata[entry.Key] = entry.Value;
            }

            string requestId;
            if (context.Metadata.TryGetValue("x-request-id", out requestId) ||
                context.Metadata.TryGetValue("x-trace-id", out requestId) ||
                context.Metadata.TryGetValue("x-correlation-id", out requestId))
            {
                context.RequestId = requestId;
            }

            string correlationId;
            if (context.Metadata.TryGetValue("x-correlation-id", out correlationId))
            {
                context.CorrelationId = correlationId;
            }

            return context;
        }
    }
}
