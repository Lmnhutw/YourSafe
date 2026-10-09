namespace PasswordTool.Core.Utilities;

public static class PasswordHashLimits
{
    public const int MaxPasswordCharacters = 4096;
    public const int MaxStoredHashCharacters = 2048;
    public const int MaxPbkdf2Iterations = 1_000_000;
    public const int MaxBcryptWorkFactor = 14;
    public const int RecommendedPbkdf2Sha256Iterations = 600000;
    public const int RecommendedPbkdf2Sha512Iterations = 220000;

    public static void ValidatePassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (password.Length > MaxPasswordCharacters)
            throw new ArgumentException("Password exceeds the supported length.", nameof(password));
    }

    public static bool IsValidSaltSize(int size) => size is >= 16 and <= 64;
    public static bool IsValidHashSize(int size) => size is >= 16 and <= 64;

    public static bool IsValidArgon2Cost(int memory, int iterations, int parallelism) =>
        memory is >= 8192 and <= 131072 && iterations is >= 1 and <= 10
        && parallelism is >= 1 and <= 8 && (long)memory * iterations <= 393216;

    public static bool IsRecommendedArgon2Cost(int memory, int iterations) =>
        (memory >= 47104 && iterations >= 1) || (memory >= 19456 && iterations >= 2)
        || (memory >= 12288 && iterations >= 3) || (memory >= 9216 && iterations >= 4)
        || (memory >= 7168 && iterations >= 5);

    public static bool IsValidScryptCost(int cost, int blockSize, int parallelization) =>
        cost is >= 2 and <= 131072 && (cost & (cost - 1)) == 0
        && blockSize is >= 1 and <= 32 && parallelization is >= 1 and <= 16
        && 128L * cost * blockSize <= 128 * 1024 * 1024
        && (long)cost * blockSize * parallelization <= 1_048_576;

    public static bool IsRecommendedScryptCost(int cost, int blockSize, int parallelization) =>
        blockSize >= 8 && ((cost >= 131072 && parallelization >= 1)
            || (cost >= 65536 && parallelization >= 2) || (cost >= 32768 && parallelization >= 3)
            || (cost >= 16384 && parallelization >= 5) || (cost >= 8192 && parallelization >= 10));
}
