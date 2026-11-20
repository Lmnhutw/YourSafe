namespace PasswordTool.Presentation.Tests;

public sealed class CredentialColumnsTests
{
    [Theory]
    [InlineData(800)] [InlineData(CredentialColumns.MinimumTableWidth)] [InlineData(1440)] [InlineData(2400)]
    public void Columns_keep_minimums_and_allocate_all_remaining_width(double width)
    {
        var columns = CredentialColumns.Calculate(width);
        Assert.Equal(76, columns[0]); Assert.Equal(136, columns[4]); Assert.Equal(144, columns[6]);
        Assert.True(columns[1] >= 180 && columns[2] >= 200 && columns[3] >= 156 && columns[5] >= 180);
        Assert.Equal(Math.Max(width, CredentialColumns.MinimumTableWidth), columns.Sum() + 144, 6);
        Assert.Equal((columns[3] - 156) * 2, columns[1] - 180, 6);
    }
}
