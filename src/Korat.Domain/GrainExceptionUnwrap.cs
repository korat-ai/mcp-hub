namespace Korat.Domain;

/// <summary>
/// Walks the exception chain (including Orleans grain call wrappers) to find the
/// innermost <see cref="KoratDomainException"/>.
/// Extracted here so both the Web HTTP layer and the gRPC gateway can share the
/// same unwrap logic without a circular project reference.
/// </summary>
public static class GrainExceptionUnwrap
{
    /// <summary>
    /// Returns the first <see cref="KoratDomainException"/> found by walking
    /// <paramref name="ex"/> and its <see cref="Exception.InnerException"/> chain,
    /// or <c>null</c> if none is present.
    /// </summary>
    public static KoratDomainException? Find(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is KoratDomainException domain)
                return domain;
        }

        return null;
    }
}
