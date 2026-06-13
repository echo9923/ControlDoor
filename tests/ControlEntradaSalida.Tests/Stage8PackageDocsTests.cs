using System.IO;

namespace ControlEntradaSalida.Tests
{
    public static class Stage8PackageDocsTests
    {
        [TestCase]
        public static void Stage8PackageDocs_AllTemplatesExist()
        {
            foreach (var fileName in new[] { "部署说明.md", "运行前检查.md", "联调记录模板.md" })
            {
                Assert.True(File.Exists(Path.Combine("docs", "stage8", "package-docs", fileName)), fileName);
            }
        }

        [TestCase]
        public static void Stage8PackageDocs_JointTestTemplate_CoversAllRequiredScenes()
        {
            var content = File.ReadAllText(Path.Combine("docs", "stage8", "package-docs", "联调记录模板.md"));

            foreach (var scene in new[]
            {
                "服务启动",
                "设备登录",
                "设备管理 gRPC",
                "权限同步",
                "人员人脸同步",
                "删除操作",
                "离线补偿",
                "ACS 实时事件",
                "抓拍保存",
                "离线事件上传补偿",
                "服务停止"
            })
            {
                Assert.Contains(scene, content);
            }
        }

        [TestCase]
        public static void Stage8PackageDocs_AgentsDirectoryMentionsNewDocs()
        {
            var agents = File.ReadAllText("AGENTS.md");

            Assert.Contains("docs/stage8/package-docs/部署说明.md", agents);
            Assert.Contains("docs/stage8/package-docs/运行前检查.md", agents);
            Assert.Contains("docs/stage8/package-docs/联调记录模板.md", agents);
        }
    }
}
