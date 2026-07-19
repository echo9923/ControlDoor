using System;
using System.Collections.Generic;
using System.Reflection;
using ControlDoor.Configuration;
using ControlDoor.FaceEvents;

namespace ControlEntradaSalida.Tests
{
    public static class FaceEventRepositoryCommandTimeoutTests
    {
        private const string CommandTimeoutFieldName = "commandTimeoutSeconds";

        [TestCase]
        public static void FaceEventRepository_DefaultConstructor_UsesDefaultTimeout()
        {
            var database = new RecordingDatabaseClient();
            var storage = new SnapshotStorage(TestWorkspace.Create(), new FaceEventLoggingOptions { SnapshotRootDirectory = "snapshots" });
            var repository = new FaceEventRepository(database, storage);

            Assert.Equal(30, GetCommandTimeoutSeconds(repository));
        }

        [TestCase]
        public static void FaceEventRepository_ConnectionStringOverload_PreservesDefaultTimeout()
        {
            var database = new RecordingDatabaseClient();
            var storage = new SnapshotStorage(TestWorkspace.Create(), new FaceEventLoggingOptions { SnapshotRootDirectory = "snapshots" });
            var repository = new FaceEventRepository(database, storage, "Server=.;Database=test;");

            Assert.Equal(30, GetCommandTimeoutSeconds(repository));
        }

        [TestCase]
        public static void FaceEventRepository_ConnectionStringWithTimeout_StoresProvidedTimeout()
        {
            var database = new RecordingDatabaseClient();
            var storage = new SnapshotStorage(TestWorkspace.Create(), new FaceEventLoggingOptions { SnapshotRootDirectory = "snapshots" });
            var repository = new FaceEventRepository(database, storage, "Server=.;Database=test;", 75);

            Assert.Equal(75, GetCommandTimeoutSeconds(repository));
        }

        [TestCase]
        public static void FaceEventRepository_ConnectionStringWithZeroTimeout_FallsBackToDefault()
        {
            var database = new RecordingDatabaseClient();
            var storage = new SnapshotStorage(TestWorkspace.Create(), new FaceEventLoggingOptions { SnapshotRootDirectory = "snapshots" });
            var repository = new FaceEventRepository(database, storage, "Server=.;Database=test;", 0);

            Assert.Equal(30, GetCommandTimeoutSeconds(repository));
        }

        [TestCase]
        public static void FaceEventRepository_ConnectionStringWithNegativeTimeout_FallsBackToDefault()
        {
            var database = new RecordingDatabaseClient();
            var storage = new SnapshotStorage(TestWorkspace.Create(), new FaceEventLoggingOptions { SnapshotRootDirectory = "snapshots" });
            var repository = new FaceEventRepository(database, storage, "Server=.;Database=test;", -5);

            Assert.Equal(30, GetCommandTimeoutSeconds(repository));
        }

        private static int GetCommandTimeoutSeconds(FaceEventRepository repository)
        {
            var field = typeof(FaceEventRepository).GetField(
                CommandTimeoutFieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, "commandTimeoutSeconds field is missing");
            return (int)field.GetValue(repository);
        }
    }
}
