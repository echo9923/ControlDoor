using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.Configuration;
using ControlDoor.FaceEvents;

namespace ControlEntradaSalida.Tests
{
    // 代码复核 R10/R11：批次大小必须有经验证的上限（配合仓储分块防超 SQL Server 参数限制）；
    // 批量路径必须应用统一数据库命令超时，非法值回退默认。
    public static class CodeReviewR10R11BatchRepositoryTests
    {
        [TestCase]
        public static void FaceEventIngestionService_BatchSize_IsClampedToConfiguredRange()
        {
            var oversized = new FaceEventIngestionService(new FaceEventLoggingOptions { BatchSize = 3000 });
            var undersized = new FaceEventIngestionService(new FaceEventLoggingOptions { BatchSize = 0 });
            var boundary = new FaceEventIngestionService(new FaceEventLoggingOptions { BatchSize = 999 });

            Assert.Equal(FaceEventIngestionService.MaxBatchSize, oversized.BatchSizeForTest);
            Assert.Equal(FaceEventIngestionService.DefaultBatchSize, undersized.BatchSizeForTest);
            Assert.Equal(999, boundary.BatchSizeForTest);
            oversized.Dispose();
            undersized.Dispose();
            boundary.Dispose();
        }

        [TestCase]
        public static void ConfigurationValidator_BatchSizeOutOfRange_FallsBackWithWarning()
        {
            var settings = NewSettings();
            settings.FaceEventLogging.BatchSize = 3000;
            settings.FaceEventLogging.FlushIntervalMs = 5;

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success);
            Assert.Equal(FaceEventIngestionService.DefaultBatchSize, result.Settings.FaceEventLogging.BatchSize);
            Assert.Equal(FaceEventIngestionService.DefaultFlushIntervalMs, result.Settings.FaceEventLogging.FlushIntervalMs);
            Assert.True(result.Warnings.Any(item => item.Contains("FaceEventLogging.BatchSize")));
            Assert.True(result.Warnings.Any(item => item.Contains("FaceEventLogging.FlushIntervalMs")));
        }

        [TestCase]
        public static void FaceEventRepository_CommandTimeout_AppliesConfiguredOrDefault()
        {
            var repository = NewRepository(7);
            var fallback = NewRepository(0);
            var standard = NewRepository(45);

            Assert.Equal(7, repository.CommandTimeoutForTest);
            Assert.Equal(30, fallback.CommandTimeoutForTest);
            Assert.Equal(45, standard.CommandTimeoutForTest);
        }

        private static AppSettings NewSettings()
        {
            var settings = new AppSettings();
            settings.Database.ConnectionString = "Server=.;Database=test;";
            return settings;
        }

        private static FaceEventRepository NewRepository(int commandTimeoutSeconds)
        {
            var runDirectory = TestWorkspace.Create();
            var snapshotStorage = new SnapshotStorage(runDirectory, new FaceEventLoggingOptions
            {
                SnapshotRootDirectory = System.IO.Path.Combine(runDirectory, "snapshots")
            });
            return new FaceEventRepository(new RecordingDatabaseClient(), snapshotStorage, "Server=.;Database=test;", commandTimeoutSeconds);
        }
    }
}
