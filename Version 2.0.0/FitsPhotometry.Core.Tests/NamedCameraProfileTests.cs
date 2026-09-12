using System.IO;
using FitsPhotometry.Core.Camera;

namespace FitsPhotometry.Core.Tests;

public class NamedCameraProfileTests
{
    [Fact]
    public void SaveAndLoad_RoundTrips_MultipleProfiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"camera_profile_library_test_{Guid.NewGuid():N}.json");
        try
        {
            var profiles = new List<NamedCameraProfile>
            {
                new() { Name = "ASI183MM Pro - gain 111", GainEPerAdu = 1.00858, AduScale = 16, FullWellElectrons = 15000, TargetElectrons = 100_000 },
                new() { Name = "ASI2600MM - unity gain", GainEPerAdu = 0.25, AduScale = 1, FullWellElectrons = 50_000, TargetElectrons = 150_000 },
            };
            CameraProfileLibrary.SaveToJson(path, profiles);

            var loaded = CameraProfileLibrary.LoadFromJson(path);

            Assert.Equal(2, loaded.Count);
            Assert.Equal("ASI183MM Pro - gain 111", loaded[0].Name);
            Assert.Equal(16, loaded[0].AduScale);
            Assert.Equal(15000, loaded[0].FullWellElectrons);
            Assert.Equal("ASI2600MM - unity gain", loaded[1].Name);
            Assert.Equal(50000, loaded[1].FullWellElectrons);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromJson_MissingFile_ReturnsEmptyList()
    {
        var path = Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}.json");
        Assert.Empty(CameraProfileLibrary.LoadFromJson(path));
    }
}
