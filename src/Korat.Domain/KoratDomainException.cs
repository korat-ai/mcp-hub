namespace Korat.Domain;

/// <summary>
/// Доменная ошибка с кодом KoratErrorCode. [GenerateSerializer] нужен для Orleans.
/// </summary>
[GenerateSerializer]
[Alias("korat.KoratDomainException")]
public sealed class KoratDomainException : Exception
{
    [Id(0)]
    public KoratErrorCode Code { get; }

    public KoratDomainException() : this(KoratErrorCode.NotFound)
    {
    }

    public KoratDomainException(KoratErrorCode code) : base(KoratError.Message(code))
    {
        Code = code;
    }

    public KoratDomainException(KoratErrorCode code, string message) : base(message)
    {
        Code = code;
    }
}
