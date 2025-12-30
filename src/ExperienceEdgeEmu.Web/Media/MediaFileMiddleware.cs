using ExperienceEdgeEmu.Web.EmuSchema;
using Microsoft.AspNetCore.StaticFiles;

namespace ExperienceEdgeEmu.Web.Media;

public class MediaFileMiddleware(RequestDelegate next, EmuFileSystem emuFileSystem, FileExtensionContentTypeProvider fileExtension)
{
    private readonly string[] _acceptedPaths = ["/-/media/", "/-/jssmedia/"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.Value == null || !_acceptedPaths.Any(x => context.Request.Path.Value.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);

            return;
        }

        var filePath = emuFileSystem.GetMediaFilePath(context.Request.Path);

        if (!File.Exists(filePath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;

            return;
        }

        if (!fileExtension.TryGetContentType(filePath, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        context.Response.ContentType = contentType;
        context.Response.Headers.AccessControlAllowOrigin = "*";

        await context.Response.SendFileAsync(filePath);
    }
}
