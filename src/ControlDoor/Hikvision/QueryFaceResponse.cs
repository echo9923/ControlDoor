using System.Collections.Generic;

namespace ControlDoor.Hikvision
{
    public sealed class QueryFaceResponse
    {
        public QueryFaceResponse()
        {
            Faces = new List<FaceInfo>();
        }

        public IList<FaceInfo> Faces { get; private set; }

        public int TotalCount { get; set; }

        public bool Exists { get; set; }

        public string RawResponse { get; set; }
    }
}
