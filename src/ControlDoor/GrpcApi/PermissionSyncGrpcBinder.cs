using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Grpc.Core;

namespace ControlDoor.GrpcApi
{
    public sealed class PermissionSyncGrpcBinder
    {
        private static readonly Marshaller<string> StringMarshaller = Marshallers.Create(
            value => Encoding.UTF8.GetBytes(value ?? string.Empty),
            bytes => Encoding.UTF8.GetString(bytes ?? new byte[0]));

        private readonly PermissionSyncGrpcService service;

        public PermissionSyncGrpcBinder(PermissionSyncGrpcService service)
        {
            this.service = service;
        }

        public ServerServiceDefinition Bind()
        {
            return ServerServiceDefinition.CreateBuilder()
                .AddMethod(CreateUnaryMethod("SyncPermissions"), HandleSyncPermissions)
                .AddMethod(CreateUnaryMethod("SyncPersons"), HandleSyncPersons)
                .AddMethod(CreateUnaryMethod("DeleteFaces"), HandleDeleteFaces)
                .AddMethod(CreateUnaryMethod("DeletePersons"), HandleDeletePersons)
                .AddMethod(CreateUnaryMethod("GetFaces"), HandleGetFaces)
                .AddMethod(CreateServerStreamingMethod("CaptureFaceStream"), HandleCaptureFaceStream)
                .AddMethod(CreateUnaryMethod("GetEnrollmentStatus"), HandleGetEnrollmentStatus)
                .Build();
        }

        private static Method<string, string> CreateUnaryMethod(string methodName)
        {
            return new Method<string, string>(
                MethodType.Unary,
                PermissionSyncGrpcService.ServiceName,
                methodName,
                StringMarshaller,
                StringMarshaller);
        }

        private static Method<string, string> CreateServerStreamingMethod(string methodName)
        {
            return new Method<string, string>(
                MethodType.ServerStreaming,
                PermissionSyncGrpcService.ServiceName,
                methodName,
                StringMarshaller,
                StringMarshaller);
        }

        private Task<string> HandleSyncPermissions(string request, ServerCallContext context)
        {
            return service.SyncPermissionsAsync(request, ToContext(context));
        }

        private Task<string> HandleSyncPersons(string request, ServerCallContext context)
        {
            return service.SyncPersonsAsync(request, ToContext(context));
        }

        private Task<string> HandleDeleteFaces(string request, ServerCallContext context)
        {
            return service.DeleteFacesAsync(request, ToContext(context));
        }

        private Task<string> HandleDeletePersons(string request, ServerCallContext context)
        {
            return service.DeletePersonsAsync(request, ToContext(context));
        }

        private Task<string> HandleGetFaces(string request, ServerCallContext context)
        {
            return service.GetFacesAsync(request, ToContext(context));
        }

        private async Task HandleCaptureFaceStream(string request, IServerStreamWriter<string> responseStream, ServerCallContext context)
        {
            var items = await service.CaptureFaceStreamAsync(request, ToContext(context)).ConfigureAwait(false);
            foreach (var item in items)
            {
                await responseStream.WriteAsync(item).ConfigureAwait(false);
            }
        }

        private Task<string> HandleGetEnrollmentStatus(string request, ServerCallContext context)
        {
            return service.GetEnrollmentStatusAsync(request, ToContext(context));
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
