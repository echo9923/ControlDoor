using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace ControlDoor.GrpcApi
{
    internal static class JsonRequestReader
    {
        public static object ParseAny(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new ArgumentException("请求 JSON 不能为空。");
            }

            return Serializer().DeserializeObject(json);
        }

        public static IList<object> ReadItems(object root, params string[] containerNames)
        {
            if (root == null)
            {
                return new List<object>();
            }

            if (IsEnumerableContainer(root))
            {
                return ToObjectList(root);
            }

            var dictionary = root as IDictionary<string, object>;
            if (dictionary != null)
            {
                foreach (var name in containerNames ?? new string[0])
                {
                    object value;
                    if (TryGetValue(dictionary, out value, name))
                    {
                        if (value == null)
                        {
                            return new List<object>();
                        }

                        if (!IsEnumerableContainer(value))
                        {
                            throw new ArgumentException(name + " 必须是数组。");
                        }

                        return ToObjectList(value);
                    }
                }

                return new List<object> { dictionary };
            }

            if (root is string)
            {
                return new List<object> { root };
            }

            throw new ArgumentException("请求 JSON 必须是对象或数组。");
        }

        public static IDictionary<string, object> AsObject(object value, string message = "记录必须是 JSON 对象。")
        {
            var dictionary = value as IDictionary<string, object>;
            if (dictionary == null)
            {
                throw new ArgumentException(message);
            }

            return dictionary;
        }

        public static string GetString(IDictionary<string, object> values, params string[] names)
        {
            object value;
            if (!TryGetValue(values, out value, names) || value == null)
            {
                return null;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static bool? GetBool(IDictionary<string, object> values, params string[] names)
        {
            object value;
            if (!TryGetValue(values, out value, names) || value == null)
            {
                return null;
            }

            if (value is bool)
            {
                return (bool)value;
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool parsed;
            return bool.TryParse(text, out parsed) ? parsed : (bool?)null;
        }

        public static int? GetInt(IDictionary<string, object> values, params string[] names)
        {
            object value;
            if (!TryGetValue(values, out value, names) || value == null)
            {
                return null;
            }

            if (value is int)
            {
                return (int)value;
            }

            if (value is long)
            {
                return checked((int)(long)value);
            }

            decimal decimalValue;
            if (decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out decimalValue))
            {
                return decimalValue == Math.Truncate(decimalValue) ? (int?)decimal.ToInt32(decimalValue) : null;
            }

            int parsed;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : (int?)null;
        }

        public static DateTime? GetDateTime(IDictionary<string, object> values, params string[] names)
        {
            object value;
            if (!TryGetValue(values, out value, names) || value == null)
            {
                return null;
            }

            if (value is DateTime)
            {
                return (DateTime)value;
            }

            DateTime parsed;
            return DateTime.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out parsed)
                ? parsed
                : (DateTime?)null;
        }

        public static DateTime? TryGetDateTime(IDictionary<string, object> values, params string[] names)
        {
            object value;
            if (!TryGetValue(values, out value, names) || value == null)
            {
                return null;
            }

            if (value is DateTime)
            {
                return (DateTime)value;
            }

            DateTime parsed;
            if (!DateTime.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out parsed))
            {
                throw new ArgumentException($"无法解析日期时间字段 \"{names[0]}\"。");
            }

            return parsed;
        }

        public static bool TryGetValue(IDictionary<string, object> values, out object value, params string[] names)
        {
            value = null;
            if (values == null || names == null)
            {
                return false;
            }

            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (values.TryGetValue(name, out value))
                {
                    return true;
                }

                foreach (var item in values)
                {
                    if (string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = item.Value;
                        return true;
                    }
                }
            }

            return false;
        }

        public static string Serialize(object value)
        {
            return Serializer().Serialize(value);
        }

        private static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        }

        private static bool IsEnumerableContainer(object value)
        {
            return value is IEnumerable && !(value is string) && !(value is IDictionary<string, object>);
        }

        private static IList<object> ToObjectList(object value)
        {
            var enumerable = value as IEnumerable;
            if (enumerable == null || value is string)
            {
                return new List<object>();
            }

            return enumerable.Cast<object>().ToList();
        }
    }
}
