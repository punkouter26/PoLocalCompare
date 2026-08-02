using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace PoLocalCompare.Api.Features.Archive;

/// <summary>
/// The Archive slice owns its own route. The path stays under /api/duels because the report is
/// addressed by duel id and that URL is already linked from the Arena and the Archive page —
/// only the file it is declared in moves, so the slice is self-contained.
/// </summary>
public static class ArchiveEndpoints
{
    public static IEndpointRouteBuilder MapArchiveEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/duels/{duelId}/report", async (
            DuelId duelId,
            HttpContext httpContext,
            [FromServices] ExportLabReportHandler handler) =>
        {
            var html = await handler.HandleAsync(duelId);
            if (html is null)
                return Results.NotFound(new { error = $"Duel '{duelId}' not found." });

            httpContext.Response.Headers["Content-Disposition"] = $"attachment; filename=\"lab-report-{duelId}.html\"";
            return Results.Content(
                html,
                contentType: "text/html",
                contentEncoding: Encoding.UTF8,
                statusCode: StatusCodes.Status200OK);
        })
        .WithTags("Archive")
        .RequireAuthorization()
        .WithName("ExportLabReport")
        .WithSummary("Exports a self-contained HTML Lab Report for the specified duel.")
        .Produces<string>(StatusCodes.Status200OK, contentType: "text/html")
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
