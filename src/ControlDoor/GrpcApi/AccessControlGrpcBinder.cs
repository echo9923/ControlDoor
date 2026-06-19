using System.Text;
using System.Threading.Tasks;
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
                .AddMethod(CreateMethod("RearmDeviceAlarm"), HandleRearmDeviceAlarm)
                .AddMethod(CreateMethod("DisarmDeviceAlarm"), HandleDisarmDeviceAlarm)
                .AddMethod(CreateMethod("GetDeviceAlarmStatus"), HandleGetDeviceAlarmStatus)
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

        private Task<string> HandleGetDeviceStatus(string request, ServerCallContext context)
        {
            return RunUnary(context, callContext => service.GetDeviceStatus(request, callContext));
        }

        private Task<string> HandleAddDevice(string request, ServerCallContext context)
        {
            return RunUnary(context, callContext => service.AddDevice(request, callContext));
        }

        private Task<string> HandleDeleteDevice(string request, ServerCallContext context)
        {
            return RunUnary(context, callContext => service.DeleteDevice(request, callContext));
        }

        private Task<string> HandleDisconnectDevice(string request, ServerCallContext context)
        {
            return RunUnary(context, callContext => service.DisconnectDevice(request, callContext));
        }

        private Task<string> HandleReconnectDevice(string request, ServerCallContext context)
        {
            return RunUnary(context, callContext => service.ReconnectDevice(request, callContext));
        }

        private Task<string> HandleRearmDeviceAlarm(string request, ServerCallContext context)
        {
            return RunUnary(context, callContext => service.RearmDeviceAlarm(request, callContext));
        }

        private Task<string> HandleDisarmDeviceAlarm(string request, ServerCallContext context)
        {
            return RunUnary(context, callContext => service.DisarmDeviceAlarm(request, callContext));
        }

        private Task<string> HandleGetDeviceAlarmStatus(string request, ServerCallContext context)
        {
            return RunUnary(context, callContext => service.GetDeviceAlarmStatus(request, callContext));
        }

        private static Task<string> RunUnary(ServerCallContext serverContext, System.Func<GrpcRequestContext, string> handler)
        {
            var requestContext = ToContext(serverContext);
            return Task.Run(() => handler(requestContext), requestContext.CancellationToken);
        }

        private static GrpcRequestContext ToContext(ServerCallContext callContext)
        {
            var context = new GrpcRequestContext
            {
                CancellationToken = callContext.CancellationToken
            };
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
