using Korat.Domain;

namespace Korat.Cloud.Web;

internal static class GrainExceptionExtensions
{
    public static IResult? ToDomainErrorResult(this Exception ex)
    {
        var domain = GrainExceptionUnwrap.Find(ex);
        if (domain is null)
            return null;

        var status = domain.Code switch
        {
            KoratErrorCode.NotFound => StatusCodes.Status404NotFound,
            KoratErrorCode.InvalidStateTransition => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Problem(KoratError.Message(domain.Code), statusCode: status);
    }
}
