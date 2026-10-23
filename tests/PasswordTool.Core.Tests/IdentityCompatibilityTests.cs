using PasswordTool.Core.Hashers.Identity;

namespace PasswordTool.Core.Tests;

public sealed class IdentityCompatibilityTests
{
    // Generated with Microsoft.AspNetCore.App 10.0.13 before replacing the framework reference.
    [Theory]
    [InlineData(2, "ANcjuwdG6GvpIvUt0Z/mYvYCr45Z/bWO+Ic4UEq1U0ZF4Doz6bVrQIBqcdbOEp0IEw==")]
    [InlineData(3, "AQAAAAIAAYagAAAAEJVF0sdIA9PEOrCVOatJN0Ewgyb1UHnkx1nIXatimppwjzTypIruQZIaCX0e+jvk3A==")]
    public void Framework_hashes_remain_readable_after_dependency_change(int version, string storedHash)
    {
        var hasher = new AspNetCoreIdentityPasswordHasherAdapter();

        Assert.True(hasher.VerifyPassword("dependency-compatibility-test-password", storedHash));
        Assert.False(hasher.VerifyPassword("wrong password", storedHash));
        Assert.Equal(version, hasher.InspectHash(storedHash).Version);
    }
}
