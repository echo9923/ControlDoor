using System;
using System.Globalization;
using System.Linq;

namespace ControlDoor.Hikvision
{
    internal static class HikvisionPersonPayloadBuilder
    {
        private const string DefaultBeginTime = "2022-01-01T00:00:00";
        private const string DefaultEndTime = "2035-12-31T23:59:59";
        private const string UserVerifyModeFace = "face";

        public static object BuildUserInfoSetup(PersonInfo person, int doorCount = 1)
        {
            HikvisionGatewayValidator.RequirePerson(person);

            var enabled = person.Enabled;
            var normalizedDoorCount = NormalizeDoorCount(doorCount);
            var rightPlans = BuildRightPlans(enabled, normalizedDoorCount);
            var doorRight = enabled
                ? string.Join(",", Enumerable.Range(1, normalizedDoorCount).Select(item => item.ToString(CultureInfo.InvariantCulture)))
                : string.Empty;

            return new
            {
                UserInfo = new
                {
                    employeeNo = person.EmployeeId,
                    name = person.Name ?? string.Empty,
                    userType = "normal",
                    gender = ResolveGender(person),
                    userVerifyMode = UserVerifyModeFace,
                    Valid = new
                    {
                        enable = enabled,
                        beginTime = FormatDateTime(person.ValidFrom) ?? DefaultBeginTime,
                        endTime = FormatDateTime(person.ValidTo) ?? DefaultEndTime,
                        timeType = "local"
                    },
                    doorRight,
                    RightPlan = rightPlans
                }
            };
        }

        public static object BuildPermissionUserInfoSetup(PersonInfo person, int doorCount = 1)
        {
            HikvisionGatewayValidator.RequirePerson(person);

            var enabled = person.Enabled;
            var normalizedDoorCount = NormalizeDoorCount(doorCount);
            var rightPlans = BuildRightPlans(enabled, normalizedDoorCount);
            var doorRight = enabled
                ? string.Join(",", Enumerable.Range(1, normalizedDoorCount).Select(item => item.ToString(CultureInfo.InvariantCulture)))
                : string.Empty;

            return new
            {
                UserInfo = new
                {
                    employeeNo = person.EmployeeId,
                    name = person.Name ?? string.Empty,
                    userType = "normal",
                    Valid = new
                    {
                        enable = enabled,
                        beginTime = DefaultBeginTime,
                        endTime = enabled ? DefaultEndTime : DefaultBeginTime,
                        timeType = "local"
                    },
                    doorRight,
                    RightPlan = rightPlans
                }
            };
        }

        private static object[] BuildRightPlans(bool enabled, int doorCount)
        {
            if (!enabled)
            {
                return Array.Empty<object>();
            }

            return Enumerable.Range(1, doorCount)
                .Select(doorNo => (object)new
                {
                    doorNo,
                    planTemplateNo = "1"
                })
                .ToArray();
        }

        private static int NormalizeDoorCount(int doorCount)
        {
            return doorCount <= 0 ? 1 : doorCount;
        }

        private static string ResolveGender(PersonInfo person)
        {
            string value;
            if (person.Metadata != null && person.Metadata.TryGetValue("gender", out value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            return "unknown";
        }

        private static string FormatDateTime(DateTime? value)
        {
            return value.HasValue
                ? value.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
                : null;
        }
    }
}
