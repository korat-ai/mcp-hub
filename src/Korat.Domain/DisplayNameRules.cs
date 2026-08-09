namespace Korat.Domain;

/// <summary>
/// Domain validation rules for human-readable display names on nodes, MCP servers,
/// agent clients, and user profiles.
/// </summary>
/// <remarks>
/// Lives in Korat.Domain so all layers (endpoint, grain, developer API) apply a single,
/// consistent validation rule without cross-layer duplication.
/// </remarks>
public static class DisplayNameRules
{
    /// <summary>Maximum length accepted for any display name (nodes, MCP servers, agent clients).</summary>
    public const int MaxLength = 256;

    /// <summary>
    /// Maximum length for a user profile display name. Deliberately smaller than
    /// <see cref="MaxLength"/> because profile names are echoed in UI headers and
    /// API responses that have tighter space constraints than infrastructure labels.
    /// </summary>
    public const int MaxProfileDisplayNameLength = 100;

    /// <summary>
    /// Returns true when <paramref name="name"/> is a valid display name.
    /// </summary>
    /// <param name="name">Candidate display name.</param>
    /// <param name="allowControlChars">
    ///   When false (the Node rule), control characters such as tab and newline are
    ///   rejected. When true (the McpServer / Consumer rule), they are permitted.
    /// </param>
    public static bool IsValid(string name, bool allowControlChars)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.Length > MaxLength)
            return false;

        if (!allowControlChars && name.Any(char.IsControl))
            return false;

        return true;
    }

    /// <summary>Returns the user-facing validation error message matching the same rule.</summary>
    public static string ValidationMessage(bool allowControlChars) =>
        allowControlChars
            ? $"displayName must be non-empty and ≤ {MaxLength} characters."
            : $"displayName must be non-empty, ≤ {MaxLength} characters, and contain no control characters.";

    /// <summary>
    /// Validates a user profile display name. Applies the profile-specific length cap
    /// (<see cref="MaxProfileDisplayNameLength"/>) and rejects control characters.
    /// </summary>
    /// <param name="name">Candidate profile display name (must already be trimmed).</param>
    /// <returns>True when the name is valid for use as a user profile display name.</returns>
    public static bool IsValidProfileDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.Length > MaxProfileDisplayNameLength)
            return false;

        if (name.Any(char.IsControl))
            return false;

        return true;
    }

    /// <summary>User-facing validation message for profile display name errors.</summary>
    public static string ProfileDisplayNameValidationMessage() =>
        $"displayName must be non-empty, ≤ {MaxProfileDisplayNameLength} characters, and contain no control characters.";
}
