using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using ControlDoor.GrpcApi;
using Grpc.Core;

namespace ControlEntradaSalida.Tests
{
    public static class Stage8GrpcCompatibilityDeepTests
    {
        [TestCase]
        public static void Stage8GrpcCompatibility_AccessControlBinder_UsesUnaryUtf8JsonStringMethods()
        {
            foreach (var item in new Dictionary<string, string>
            {
                ["GetDeviceStatus"] = AccessControlGrpcService.GetDeviceStatusFullName,
                ["AddDevice"] = AccessControlGrpcService.AddDeviceFullName,
                ["DeleteDevice"] = AccessControlGrpcService.DeleteDeviceFullName,
                ["DisconnectDevice"] = AccessControlGrpcService.DisconnectDeviceFullName,
                ["ReconnectDevice"] = AccessControlGrpcService.ReconnectDeviceFullName,
                ["RearmDeviceAlarm"] = AccessControlGrpcService.RearmDeviceAlarmFullName,
                ["DisarmDeviceAlarm"] = AccessControlGrpcService.DisarmDeviceAlarmFullName,
                ["GetDeviceAlarmStatus"] = AccessControlGrpcService.GetDeviceAlarmStatusFullName
            })
            {
                var method = InvokePrivateMethod<Method<string, string>>(
                    typeof(AccessControlGrpcBinder),
                    "CreateMethod",
                    item.Key);

                Assert.Equal(MethodType.Unary, method.Type);
                Assert.Equal(AccessControlGrpcService.ServiceName, method.ServiceName);
                Assert.Equal(item.Key, method.Name);
                Assert.Equal(item.Value, method.FullName);
                AssertUtf8StringMarshaller(method.RequestMarshaller);
                AssertUtf8StringMarshaller(method.ResponseMarshaller);
            }
        }

        [TestCase]
        public static void Stage8GrpcCompatibility_PermissionBinder_UsesExpectedMethodTypesAndUtf8JsonString()
        {
            foreach (var item in new Dictionary<string, string>
            {
                ["SyncPermissions"] = PermissionSyncGrpcService.SyncPermissionsFullName,
                ["SyncPersons"] = PermissionSyncGrpcService.SyncPersonsFullName,
                ["DeleteFaces"] = PermissionSyncGrpcService.DeleteFacesFullName,
                ["DeletePersons"] = PermissionSyncGrpcService.DeletePersonsFullName,
                ["GetFaces"] = PermissionSyncGrpcService.GetFacesFullName,
                ["GetEnrollmentStatus"] = PermissionSyncGrpcService.GetEnrollmentStatusFullName
            })
            {
                var method = InvokePrivateMethod<Method<string, string>>(
                    typeof(PermissionSyncGrpcBinder),
                    "CreateUnaryMethod",
                    item.Key);

                Assert.Equal(MethodType.Unary, method.Type);
                Assert.Equal(PermissionSyncGrpcService.ServiceName, method.ServiceName);
                Assert.Equal(item.Key, method.Name);
                Assert.Equal(item.Value, method.FullName);
                AssertUtf8StringMarshaller(method.RequestMarshaller);
                AssertUtf8StringMarshaller(method.ResponseMarshaller);
            }

            var streaming = InvokePrivateMethod<Method<string, string>>(
                typeof(PermissionSyncGrpcBinder),
                "CreateServerStreamingMethod",
                "CaptureFaceStream");

            Assert.Equal(MethodType.ServerStreaming, streaming.Type);
            Assert.Equal(PermissionSyncGrpcService.ServiceName, streaming.ServiceName);
            Assert.Equal("CaptureFaceStream", streaming.Name);
            Assert.Equal(PermissionSyncGrpcService.CaptureFaceStreamFullName, streaming.FullName);
            AssertUtf8StringMarshaller(streaming.RequestMarshaller);
            AssertUtf8StringMarshaller(streaming.ResponseMarshaller);
        }

        [TestCase]
        public static void Stage8GrpcCompatibility_ContractDocumentListsEveryImplementedFullName()
        {
            var contract = System.IO.File.ReadAllText(System.IO.Path.Combine("docs", "gRPC\u63a5\u53e3\u6e05\u5355.md"), Encoding.UTF8);

            foreach (var fullName in new[]
            {
                AccessControlGrpcService.GetDeviceStatusFullName,
                AccessControlGrpcService.AddDeviceFullName,
                AccessControlGrpcService.DeleteDeviceFullName,
                AccessControlGrpcService.DisconnectDeviceFullName,
                AccessControlGrpcService.ReconnectDeviceFullName,
                AccessControlGrpcService.RearmDeviceAlarmFullName,
                AccessControlGrpcService.DisarmDeviceAlarmFullName,
                AccessControlGrpcService.GetDeviceAlarmStatusFullName,
                PermissionSyncGrpcService.SyncPermissionsFullName,
                PermissionSyncGrpcService.SyncPersonsFullName,
                PermissionSyncGrpcService.DeleteFacesFullName,
                PermissionSyncGrpcService.DeletePersonsFullName,
                PermissionSyncGrpcService.GetFacesFullName,
                PermissionSyncGrpcService.CaptureFaceStreamFullName,
                PermissionSyncGrpcService.GetEnrollmentStatusFullName
            })
            {
                Assert.Contains(fullName, contract);
            }
        }

        [TestCase]
        public static void Stage8GrpcCompatibility_BatchAndFaceLimits_StayCompatibleWithStage8Contract()
        {
            Assert.Equal(500, ReadPrivateStaticInt(typeof(PermissionSyncGrpcService), "MaxBatchSize"));
            Assert.Equal(200 * 1024, ReadPrivateStaticInt(typeof(PermissionSyncGrpcService), "MaxFaceBytes"));
        }

        [TestCase]
        public static void Stage8GrpcCompatibility_Binders_DoNotCompleteUnaryHandlersWithTaskFromResult()
        {
            var accessBinder = System.IO.File.ReadAllText(
                System.IO.Path.Combine("src", "ControlDoor", "GrpcApi", "AccessControlGrpcBinder.cs"),
                Encoding.UTF8);
            var permissionBinder = System.IO.File.ReadAllText(
                System.IO.Path.Combine("src", "ControlDoor", "GrpcApi", "PermissionSyncGrpcBinder.cs"),
                Encoding.UTF8);
            var permissionService = System.IO.File.ReadAllText(
                System.IO.Path.Combine("src", "ControlDoor", "GrpcApi", "PermissionSyncGrpcService.cs"),
                Encoding.UTF8);

            Assert.False(accessBinder.Contains("Task.FromResult(service."));
            Assert.False(permissionBinder.Contains("Task.FromResult(service."));
            Assert.Contains("Task.Run", accessBinder);
            Assert.Contains("SyncPermissionsAsync", permissionBinder);
            Assert.Contains("Task.Run", permissionService);
            Assert.Contains("context.CancellationToken", permissionService);
            Assert.Contains("CancellationToken = callContext.CancellationToken", accessBinder);
            Assert.Contains("CancellationToken = callContext.CancellationToken", permissionBinder);
        }

        private static void AssertUtf8StringMarshaller(Marshaller<string> marshaller)
        {
            var text = "json-string-\u95e8\u7981";
            var bytes = marshaller.Serializer(text);

            Assert.Equal(text, marshaller.Deserializer(bytes));
            Assert.Equal(Encoding.UTF8.GetByteCount(text), bytes.Length);
            Assert.Equal(string.Empty, marshaller.Deserializer(marshaller.Serializer(null)));
        }

        private static T InvokePrivateMethod<T>(Type type, string methodName, string grpcMethodName)
        {
            var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method, type.FullName + "." + methodName);
            return (T)method.Invoke(null, new object[] { grpcMethodName });
        }

        private static int ReadPrivateStaticInt(Type type, string fieldName)
        {
            var field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field, type.FullName + "." + fieldName);
            return (int)field.GetValue(null);
        }
    }
}
