using System;
using ControlDoor.Observability;

namespace ControlDoor.FaceEvents
{
    public sealed class AcsFaceEventProcessor : IAcsFaceEventProcessor
    {
        private readonly AcsEventParser parser;
        private readonly FaceEventRepository repository;
        private readonly ServiceLogger logger;

        public AcsFaceEventProcessor(AcsEventParser parser, FaceEventRepository repository, ServiceLogger logger = null)
        {
            this.parser = parser ?? throw new ArgumentNullException(nameof(parser));
            this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
            this.logger = logger;
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
    }
}
