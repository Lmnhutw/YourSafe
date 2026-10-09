using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PasswordTool.Core.Abstractions;
using PasswordTool.Core.Inspection;
using PasswordTool.Core.Models;
using PasswordTool.Core.Registry;
using PasswordTool.Core.Services;

namespace PasswordTool.Presentation;

public sealed partial class HashToolViewModel : ObservableObject
{
    private readonly IPasswordHasherRegistry registry;
    private readonly PasswordHasherFactory hasherFactory;
    private readonly PasswordHashInspector inspector;

    public HashToolViewModel(IPasswordHasherRegistry registry, PasswordHasherFactory hasherFactory, PasswordHashInspector inspector)
    {
        this.registry = registry;
        this.hasherFactory = hasherFactory;
        this.inspector = inspector;
        Algorithms = registry.GetAvailableHashers().Select(descriptor => new HashAlgorithmOption
        {
            AlgorithmName = descriptor.AlgorithmName,
            DisplayName = descriptor.IsDefaultRecommendation ? $"{descriptor.AlgorithmName} (recommended)" : descriptor.AlgorithmName,
            IsRecommendedForPasswordStorage = descriptor.SecurityCategory is PasswordHasherSecurityCategory.ProductionSafe or PasswordHasherSecurityCategory.FrameworkFormat,
            Description = descriptor.Description
        }).ToList();
        SelectedAlgorithm = Algorithms.FirstOrDefault();
    }

    public IReadOnlyList<HashAlgorithmOption> Algorithms { get; }

    [ObservableProperty] public partial HashAlgorithmOption? SelectedAlgorithm { get; set; }
    [ObservableProperty] public partial string GeneratedHash { get; set; } = string.Empty;
    [ObservableProperty] public partial string VerificationResult { get; set; } = string.Empty;
    [ObservableProperty] public partial PasswordHashInfo? Inspection { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;

    [RelayCommand]
    private void Generate(string? password)
    {
        ErrorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(password) || SelectedAlgorithm is null)
        {
            ErrorMessage = "Enter a password and select an algorithm.";
            return;
        }

        try
        {
            GeneratedHash = hasherFactory.Create(SelectedAlgorithm.AlgorithmName).HashPassword(password);
            Inspection = inspector.Inspect(GeneratedHash);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            GeneratedHash = string.Empty;
            Inspection = null;
            ErrorMessage = "The selected hash operation could not be completed.";
        }
    }

    public void ResetVerification() => VerificationResult = string.Empty;

    [RelayCommand]
    private void Verify(HashVerificationRequest? request)
    {
        ErrorMessage = string.Empty;
        ResetVerification();
        if (request is null || string.IsNullOrWhiteSpace(request.Password) || string.IsNullOrWhiteSpace(request.StoredHash))
        {
            ErrorMessage = "Enter both a password and stored hash.";
            return;
        }

        try
        {
            VerificationResult = registry.GetAvailableHashers()
                .Select(descriptor => hasherFactory.Create(descriptor.AlgorithmName))
                .Any(hasher => hasher.VerifyPassword(request.Password, request.StoredHash))
                ? "Password verified."
                : "Password does not match.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            VerificationResult = "Hash format is not supported.";
            ErrorMessage = "The stored hash format could not be verified.";
        }
    }

    [RelayCommand]
    private void Inspect(string? storedHash)
    {
        ErrorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(storedHash))
        {
            ErrorMessage = "Paste a hash to inspect.";
            return;
        }

        try
        {
            Inspection = inspector.Inspect(storedHash);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Inspection = null;
            ErrorMessage = "The stored hash format could not be inspected.";
        }
    }
}

public sealed record HashVerificationRequest(string Password, string StoredHash);
