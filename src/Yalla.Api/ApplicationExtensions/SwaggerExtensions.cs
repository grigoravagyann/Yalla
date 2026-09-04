using System.Reflection;
using Microsoft.OpenApi.Models;
using Yalla.Api.Filters;

namespace Yalla.Api.ApplicationExtensions;

/// <summary>Swagger/OpenAPI generation for the Yalla API.</summary>
public static class SwaggerExtensions
{
    private const string DocumentName = "v1";

    public static IServiceCollection AddYallaSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();

        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc(DocumentName, new OpenApiInfo
            {
                Title = "Yalla API",
                Version = DocumentName,
                Description =
                    "Table reservation and in-app ordering for restaurants and cafes. Consumed by "
                    + "the diner app, the staff tablet and the admin panel.\n\n"
                    + "Conventions: money is a whole count of Armenian dram (properties suffixed "
                    + "`Amd`); every instant is UTC; wall-clock values (opening hours, the local "
                    + "date and time on a booking) are dates and times in the branch's own time "
                    + "zone and are never converted; enums travel as strings.",
            });

            // Every operation publishes the unified failure envelope, so a client generator sees
            // the error shape instead of discovering it in production.
            options.OperationFilter<ErrorResponsesOperationFilter>();

            // Surfaces the XML doc comments already generated for every project (see
            // Directory.Build.props) as endpoint and schema descriptions.
            IncludeXmlComments(options);
        });

        return services;
    }

    /// <summary>
    /// Serves the UI in Development and Staging only. Publishing the full schema of a production
    /// ordering and payment API to anonymous callers is free reconnaissance.
    /// </summary>
    public static WebApplication UseYallaSwagger(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() && !app.Environment.IsStaging())
        {
            return app;
        }

        app.UseSwagger(options => options.RouteTemplate = "swagger/{documentName}/swagger.json");
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint($"/swagger/{DocumentName}/swagger.json", "Yalla API v1");
            options.DocumentTitle = "Yalla API";
            options.DisplayRequestDuration();
        });

        return app;
    }

    private static void IncludeXmlComments(Swashbuckle.AspNetCore.SwaggerGen.SwaggerGenOptions options)
    {
        var assemblyNames = new[]
        {
            Assembly.GetExecutingAssembly().GetName().Name,
            "Yalla.Application",
            "Yalla.Domain",
        };

        foreach (var assemblyName in assemblyNames)
        {
            var path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.xml");
            if (File.Exists(path))
            {
                options.IncludeXmlComments(path, includeControllerXmlComments: true);
            }
        }
    }
}
