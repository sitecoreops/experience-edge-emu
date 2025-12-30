namespace ExperienceEdgeEmu.Web.EmuSchema;

public static partial class EmuSchemaStartupExtensions
{
    public static IServiceCollection AddEmuSchema(this IServiceCollection services)
    {
        services.AddSingleton<SchemaMerger>();
        services.AddSingleton<SdlBuilder>();
        services.AddSingleton<EmuSchemaBuilder>();
        services.AddScoped<DynamicEmuSchema>();

        return services;
    }
}
