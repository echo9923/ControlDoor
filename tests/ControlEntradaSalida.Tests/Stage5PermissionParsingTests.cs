using System;
using System.Linq;

namespace ControlEntradaSalida.Tests
{
    public static class Stage5PermissionParsingTests
    {
        [TestCase]
        public static void DeletePersons_SupportsSingleObjectAndRecordsContainer()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice();

                var single = fixture.Response(fixture.Service.DeletePersons(@"{""employee_id"":""10001""}", fixture.Context("delete-person-single")));
                var records = fixture.Response(fixture.Service.DeletePersons(@"{""records"":[{""employeeNo"":""10002""}]}", fixture.Context("delete-person-records")));

                Assert.Equal("OK", single["code"]);
                Assert.Equal("OK", records["code"]);
                Assert.Equal(1, Convert.ToInt32(single["total"]));
                Assert.Equal(1, Convert.ToInt32(records["total"]));
                Assert.True(fixture.Gateway.Calls.Count(call => call.MethodName == "DeletePersonAsync") >= 2);
                Assert.True(fixture.UserWriter.PersonsDeleted.Contains("10001"));
                Assert.True(fixture.UserWriter.PersonsDeleted.Contains("10002"));
            }
        }

        [TestCase]
        public static void GetFaces_StringArrayDeduplicatesAndUsesOnlyOnlineDevices()
        {
            using (var fixture = new Stage5Fixture())
            {
                fixture.AddOnlineDevice(1);
                fixture.AddOfflineDevice(2);
                fixture.Service.SyncPersons(@"{""items"":[{""employee_id"":""10001"",""face_image_base64"":""" + Stage5TestData.JpegBase64() + @"""}]}", fixture.Context("seed-face-string-array"));

                var response = fixture.Response(fixture.Service.GetFaces(@"[""10001"",""10001""]", fixture.Context("get-face-string-array")));

                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, Convert.ToInt32(response["total"]));
                Assert.Equal(1, Convert.ToInt32(response["succeeded"]));
                Assert.Equal(1, Convert.ToInt32(response["targetDevices"]));
            }
        }

        [TestCase]
        public static void SyncPersons_ValidFromAfterValidTo_ReturnsInvalidArgument()
        {
            using (var fixture = new Stage5Fixture())
            {
                var response = fixture.Response(fixture.Service.SyncPersons(
                    @"{""items"":[{""employee_id"":""10001"",""valid_from"":""2030-01-01T00:00:00"",""valid_to"":""2029-01-01T00:00:00""}]}",
                    fixture.Context("persons-invalid-range")));

                Assert.Equal(false, response["success"]);
                Assert.Equal("INVALID_ARGUMENT", response["code"]);
            }
        }

        [TestCase]
        public static void SyncPermissions_NonIntegerPermissionCode_ReturnsInvalidArgument()
        {
            using (var fixture = new Stage5Fixture())
            {
                var response = fixture.Response(fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10001"",""permission_code"":""admin""}]}",
                    fixture.Context("permission-non-integer")));

                Assert.Equal(false, response["success"]);
                Assert.Equal("INVALID_ARGUMENT", response["code"]);
            }
        }

        [TestCase]
        public static void GetFaces_BlankEmployeeInStringArray_ReturnsInvalidArgument()
        {
            using (var fixture = new Stage5Fixture())
            {
                var response = fixture.Response(fixture.Service.GetFaces(@"["" ""]", fixture.Context("get-face-blank")));

                Assert.Equal(false, response["success"]);
                Assert.Equal("INVALID_ARGUMENT", response["code"]);
            }
        }
    }
}
