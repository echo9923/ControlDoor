using System.Web.Script.Serialization;

namespace ControlDoor.Hikvision
{
    internal static class HikvisionGatewayJson
    {
        public static string Serialize(object value)
        {
            return new JavaScriptSerializer().Serialize(value);
        }

        public static T Deserialize<T>(string json)
        {
            return new JavaScriptSerializer().Deserialize<T>(json);
        }
    }
}
