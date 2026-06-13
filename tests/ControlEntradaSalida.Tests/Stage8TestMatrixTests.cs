using System.Linq;
using ControlDoor.Deployment;

namespace ControlEntradaSalida.Tests
{
    public static class Stage8TestMatrixTests
    {
        [TestCase]
        public static void Stage8TestMatrix_Layers_MatchAcceptanceBoundaries()
        {
            Assert.Equal(6, Stage8TestMatrix.Layers.Count);

            var automaticLayers = Stage8TestMatrix.Layers.Where(item => item.Automatic).Select(item => item.Id).ToArray();
            Assert.Equal("L1,L2,L3,L4,L5", string.Join(",", automaticLayers));

            foreach (var layer in Stage8TestMatrix.Layers.Where(item => item.Id != "L6"))
            {
                Assert.False(layer.RequiresRealDevice, layer.Id + " must not require real hardware.");
            }

            var manualLayer = Stage8TestMatrix.Layers.Single(item => item.Id == "L6");
            Assert.False(manualLayer.Automatic);
            Assert.True(manualLayer.RequiresRealDevice);
        }

        [TestCase]
        public static void Stage8TestMatrix_ExecutionSteps_AreStableAndOrdered()
        {
            Assert.Equal(9, Stage8TestMatrix.ExecutionSteps.Count);
            for (var index = 0; index < Stage8TestMatrix.ExecutionSteps.Count; index++)
            {
                Assert.Equal(index + 1, Stage8TestMatrix.ExecutionSteps[index].Order);
            }

            Assert.Contains("nuget restore ControlEntradaSalida.sln", Stage8TestMatrix.ExecutionSteps[0].CommandOrAction);
            Assert.Contains("dotnet build ControlEntradaSalida.sln --verbosity minimal", Stage8TestMatrix.ExecutionSteps[1].CommandOrAction);
            Assert.Contains("msbuild ControlEntradaSalida.sln /p:Configuration=Release", Stage8TestMatrix.ExecutionSteps[3].CommandOrAction);
            Assert.Contains("tests\\ControlEntradaSalida.Tests\\ControlEntradaSalida.Tests.csproj", Stage8TestMatrix.ExecutionSteps[4].CommandOrAction);
            Assert.Contains("test-service-package.ps1", Stage8TestMatrix.ExecutionSteps[6].CommandOrAction);
            Assert.Contains("--validate-config", Stage8TestMatrix.ExecutionSteps[7].CommandOrAction);
            Assert.Contains("L6", Stage8TestMatrix.ExecutionSteps[8].CommandOrAction);
        }
    }
}
