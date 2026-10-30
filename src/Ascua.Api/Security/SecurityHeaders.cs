namespace Ascua.Api.Security;

/// <summary>
/// Encabezados de seguridad en todas las respuestas (API, cliente web y <c>/docs</c>). Se escriben en
/// <c>OnStarting</c> para que sobrevivan a <c>UseExceptionHandler</c>, que limpia los encabezados al fallar.
/// </summary>
public static class SecurityHeaders
{
    public const string DocsPath = "/docs";

    /// <summary>
    /// SHA-256 del <c>&lt;script type="module"&gt;</c> inline con que Scalar arranca <c>/docs</c>. Depende de la
    /// versión de Scalar y de sus opciones en <c>Program.cs</c>; si cambian, la prueba de integración de
    /// <c>/docs</c> falla e indica el hash nuevo.
    /// </summary>
    public const string ScalarInitScriptHash = "sha256-1nrFGIvFmdtqL/VLD6bWvaxJm1guE026CmvnbVzx5nQ=";

    /// <summary>Cliente web y API: todo del mismo origen, sin inline; <c>connect-src 'self'</c> cubre ws/wss al mismo host.</summary>
    public const string DefaultPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
        "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>
    /// Scalar: su script inline se permite por hash, no con <c>'unsafe-inline'</c>. Los estilos sí necesitan
    /// <c>'unsafe-inline'</c> porque Vue los inyecta en tiempo de ejecución. Sin fuentes externas
    /// (<c>WithDefaultFonts(false)</c>).
    /// </summary>
    public const string DocsPolicy =
        "default-src 'none'; script-src 'self' '" + ScalarInitScriptHash + "'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.ContentSecurityPolicy = context.Request.Path.StartsWithSegments(DocsPath) ? DocsPolicy : DefaultPolicy;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["Permissions-Policy"] = PermissionsPolicy;
                return Task.CompletedTask;
            });
            return next(context);
        });
}
