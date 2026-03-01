namespace Nudge.Ui.Services;

public static class TargetIdentityResolver
{
    public static string Resolve(string showId, string? contactEmail)
    {
        var normalizedEmail = NormalizeEmail(contactEmail);
        if (!string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return $"email:{normalizedEmail}";
        }

        return $"show:{showId.Trim().ToLowerInvariant()}";
    }

    public static string NormalizeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().ToLowerInvariant();
    }
}
