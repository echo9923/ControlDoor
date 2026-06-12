using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace ControlDoor.GrpcApi
{
    internal static class JsonResponse
    {
        public static string Create(string requestId, bool success, string code, string message, IDictionary<string, object> business = null, IList<string> errors = null)
        {
            var body = new Dictionary<string, object>
            {
                ["requestId"] = requestId ?? string.Empty,
                ["success"] = success,
                ["code"] = code ?? string.Empty,
                ["message"] = message ?? string.Empty,
                ["errors"] = errors ?? new List<string>(),
                ["errorDetails"] = new List<object>()
            };

            if (business != null)
            {
                foreach (var item in business)
                {
                    body[item.Key] = item.Value;
                }
            }

            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(body);
        }
    }
}
