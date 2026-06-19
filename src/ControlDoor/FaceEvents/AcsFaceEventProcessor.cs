using System;
using System.Collections.Generic;
using ControlDoor.Observability;

namespace ControlDoor.FaceEvents
{
    public sealed class AcsFaceEventProcessor : IAcsFaceEventProcessor
    {
        private readonly AcsEventParser parser;
        private readonly FaceEventRepository repository;
        private readonly ServiceLogger logger;
        private readonly Func<IReadOnlyList<AcsFaceEvent>, IReadOnlyList<FaceEventInsertResult>> insertEvents;

        public AcsFaceEventProcessor(AcsEventParser parser, FaceEventRepository repository, ServiceLogger logger = null)
            : this(parser, repository, logger, null)
        {
        }

        internal AcsFaceEventProcessor(
            AcsEventParser parser,
            FaceEventRepository repository,
            Func<IReadOnlyList<AcsFaceEvent>, IReadOnlyList<FaceEventInsertResult>> insertEventsOverride)
            : this(parser, repository, null, insertEventsOverride)
        {
        }

        private AcsFaceEventProcessor(
            AcsEventParser parser,
            FaceEventRepository repository,
            ServiceLogger logger,
            Func<IReadOnlyList<AcsFaceEvent>, IReadOnlyList<FaceEventInsertResult>> insertEventsOverride)
        {
            this.parser = parser ?? throw new ArgumentNullException(nameof(parser));
            this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
            this.logger = logger;
            insertEvents = insertEventsOverride ?? repository.InsertEvents;
        }

        public FaceEventProcessResult Process(RawAcsAlarmEvent rawEvent)
        {
            var parsed = parser.Parse(rawEvent);
            if (!parsed.Success)
            {
                logger?.Warn("AcsFaceEventProcessor", parsed.Message, new LogFields
                {
                    DeviceId = rawEvent != null && rawEvent.DeviceId > 0 ? (int?)rawEvent.DeviceId : null,
                    RequestId = rawEvent?.RequestId,
                    ErrorCode = parsed.Code
                });
                return FaceEventProcessResult.Failed(parsed.Code, parsed.Message);
            }

            var inserted = repository.InsertEvent(parsed.Event);
            if (inserted.Success)
            {
                return FaceEventProcessResult.Ok(inserted.Code, inserted.Message);
            }

            return FaceEventProcessResult.Failed(inserted.Code, inserted.Message);
        }

        public IReadOnlyList<FaceEventBatchItemResult> ProcessBatch(IReadOnlyList<RawAcsAlarmEvent> rawEvents)
        {
            if (rawEvents == null || rawEvents.Count == 0)
            {
                return Array.Empty<FaceEventBatchItemResult>();
            }

            // 1. 逐条 parse，失败的直接出结果，成功的收集成 AcsFaceEvent 列表。
            var results = new FaceEventBatchItemResult[rawEvents.Count];
            var parsedEvents = new List<AcsFaceEvent>(rawEvents.Count);
            var parsedIndexes = new List<int>(rawEvents.Count);

            for (var i = 0; i < rawEvents.Count; i++)
            {
                var rawEvent = rawEvents[i];
                var parsed = parser.Parse(rawEvent);
                if (!parsed.Success)
                {
                    logger?.Warn("AcsFaceEventProcessor", parsed.Message, new LogFields
                    {
                        DeviceId = rawEvent != null && rawEvent.DeviceId > 0 ? (int?)rawEvent.DeviceId : null,
                        RequestId = rawEvent?.RequestId,
                        ErrorCode = parsed.Code
                    });
                    results[i] = FaceEventBatchItemResult.Failed(0, parsed.Code, parsed.Message);
                    continue;
                }
                parsedEvents.Add(parsed.Event);
                parsedIndexes.Add(i);
            }

            if (parsedEvents.Count == 0)
            {
                return results;
            }

            // 2. 一次性批量插入，把 InsertResult 列表回填到对应位置。
            IReadOnlyList<FaceEventInsertResult> inserted;
            try
            {
                inserted = insertEvents(parsedEvents);
            }
            catch (Exception ex)
            {
                for (var j = 0; j < parsedEvents.Count; j++)
                {
                    var targetIndex = parsedIndexes[j];
                    results[targetIndex] = FaceEventBatchItemResult.Failed(parsedEvents[j].EventId, "RETRYABLE_FAILURE", ex.Message);
                }
                return results;
            }

            if (inserted == null)
            {
                for (var j = 0; j < parsedEvents.Count; j++)
                {
                    var targetIndex = parsedIndexes[j];
                    results[targetIndex] = FaceEventBatchItemResult.Failed(parsedEvents[j].EventId, "RETRYABLE_FAILURE", "repository processing failed");
                }
                return results;
            }

            for (var j = 0; j < inserted.Count; j++)
            {
                var insertResult = inserted[j];
                var targetIndex = parsedIndexes[j];
                if (insertResult.Success)
                {
                    results[targetIndex] = FaceEventBatchItemResult.Ok(insertResult.EventId, insertResult.Code, insertResult.Message);
                }
                else
                {
                    results[targetIndex] = FaceEventBatchItemResult.Failed(insertResult.EventId, insertResult.Code, insertResult.Message);
                }
            }

            return results;
        }
    }
}
