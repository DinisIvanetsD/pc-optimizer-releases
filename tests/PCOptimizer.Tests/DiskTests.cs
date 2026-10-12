using PCOptimizer.Core.Storage;
using Xunit;

namespace PCOptimizer.Tests;

public class DiskTests
{
    [Theory]
    [InlineData("NTFS DisableDeleteNotify = 0  (Allows TRIM operations to be sent to the storage device)\nReFS DisableDeleteNotify = 0", true)]
    [InlineData("NTFS DisableDeleteNotify = 1  (Desativado)\r\n", false)]
    [InlineData("O pedido requer elevação.", null)]
    public void ParseTrim_reads_the_NTFS_line(string output, bool? expected) =>
        Assert.Equal(expected, StorageDrives.ParseTrim(output));

    [Fact]
    public void Only_ssd_and_hdd_can_be_optimized()
    {
        DriveSummary D(string media) => new("C:", "x", media, "m", 1, 1);
        Assert.True(StorageDrives.IsSsd(D("SSD NVMe")));
        Assert.True(StorageDrives.IsHdd(D("HDD")));
        Assert.False(StorageDrives.IsSsd(D("USB")) || StorageDrives.IsHdd(D("USB")));
    }
}
