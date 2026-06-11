using System.Collections.Generic;

namespace ControlDoor.Hikvision
{
    public sealed class QueryPersonResponse
    {
        public QueryPersonResponse()
        {
            Persons = new List<PersonInfo>();
        }

        public IList<PersonInfo> Persons { get; private set; }

        public int TotalCount { get; set; }

        public bool Exists { get; set; }

        public string RawResponse { get; set; }
    }
}
