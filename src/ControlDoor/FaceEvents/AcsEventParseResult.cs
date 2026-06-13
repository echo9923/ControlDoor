namespace ControlDoor.FaceEvents
{
    public sealed class AcsEventParseResult
    {
        public bool Success { get; set; }

        public string Code { get; set; }

        public string Message { get; set; }

        public AcsFaceEvent Event { get; set; }

        public static AcsEventParseResult Parsed(AcsFaceEvent faceEvent)
        {
            return new AcsEventParseResult
            {
                Success = true,
                Code = "OK",
                Message = "parsed",
                Event = faceEvent
            };
        }

        public static AcsEventParseResult Invalid(string code, string message)
        {
            return new AcsEventParseResult
            {
                Success = false,
                Code = code,
                Message = message
            };
        }
    }
}
