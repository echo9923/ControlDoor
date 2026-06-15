using System;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace ControlDoor.GrpcApi
{
    internal sealed class JsonObject
    {
        private readonly IDictionary<string, object> values;

        private JsonObject(IDictionary<string, object> values)
        {
            this.values = values ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public static JsonObject Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new JsonObject(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));
            }

            var serializer = new JavaScriptSerializer();
            var value = serializer.DeserializeObject(json);
            var dictionary = value as IDictionary<string, object>;
            if (dictionary == null)
            {
                throw new ArgumentException("请求 JSON 必须是对象。");
            }

            return new JsonObject(new Dictionary<string, object>(dictionary, StringComparer.OrdinalIgnoreCase));
        }

        public int? GetInt(params string[] names)
        {
            object value;
            if (!TryGet(out value, names) || value == null)
            {
                return null;
            }

            if (value is int)
            {
                return (int)value;
            }

            int parsed;
            return int.TryParse(Convert.ToString(value), out parsed) ? parsed : (int?)null;
        }

        public IList<int> GetIntList(params string[] names)
        {
            object value;
            var result = new List<int>();
            if (!TryGet(out value, names) || value == null)
            {
                return result;
            }

            var array = value as IEnumerable;
            if (array != null && !(value is string))
            {
                foreach (var item in array)
                {
                    int parsed;
                    if (item != null && int.TryParse(Convert.ToString(item), out parsed))
                    {
                        result.Add(parsed);
                    }
                }

                return result;
            }

            var single = GetInt(names);
            if (single.HasValue)
            {
                result.Add(single.Value);
            }

            return result;
        }

        public string GetString(params string[] names)
        {
            object value;
            if (!TryGet(out value, names) || value == null)
            {
                return null;
            }

            return Convert.ToString(value);
        }

        public IList<string> GetStringList(params string[] names)
        {
            object value;
            var result = new List<string>();
            if (!TryGet(out value, names) || value == null)
            {
                return result;
            }

            var array = value as IEnumerable;
            if (array != null && !(value is string))
            {
                foreach (var item in array)
                {
                    if (item != null)
                    {
                        result.Add(Convert.ToString(item));
                    }
                }

                return result;
            }

            result.Add(Convert.ToString(value));
            return result;
        }

        public bool? GetBool(params string[] names)
        {
            object value;
            if (!TryGet(out value, names) || value == null)
            {
                return null;
            }

            if (value is bool)
            {
                return (bool)value;
            }

            bool parsed;
            return bool.TryParse(Convert.ToString(value), out parsed) ? parsed : (bool?)null;
        }

        private bool TryGet(out object value, params string[] names)
        {
            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name) && values.TryGetValue(name, out value))
                {
                    return true;
                }
            }

            value = null;
            return false;
        }
    }
}
