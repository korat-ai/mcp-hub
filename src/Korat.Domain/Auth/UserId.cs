namespace Korat.Domain.Auth;

public readonly record struct UserId(Guid Value)
{
    public static UserId New() => new(Guid.NewGuid());
    public static UserId Parse(string s) => new(Guid.Parse(s));
    public static UserId? TryParse(string? s) => Guid.TryParse(s, out var g) ? new UserId(g) : null;
    public override string ToString() => Value.ToString("N");
}
