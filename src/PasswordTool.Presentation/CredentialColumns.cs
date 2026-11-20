namespace PasswordTool.Presentation;

public static class CredentialColumns
{
    public const double MinimumTableWidth = 1216; // columns + six 20px gaps + 24px padding
    public static double[] Calculate(double tableWidth)
    {
        var extra = Math.Max(0, tableWidth - MinimumTableWidth) / 7;
        return [76, 180 + extra * 2, 200 + extra * 2, 156 + extra, 136, 180 + extra * 2, 144];
    }
}
