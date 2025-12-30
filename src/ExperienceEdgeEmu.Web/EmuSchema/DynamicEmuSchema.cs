using GraphQL;
using GraphQL.Conversion;
using GraphQL.Instrumentation;
using GraphQL.Introspection;
using GraphQL.Types;
using GraphQL.Utilities;
using System.Diagnostics.CodeAnalysis;

namespace ExperienceEdgeEmu.Web.EmuSchema;

public partial class DynamicEmuSchema(EmuSchemaBuilder schemaBuilder, ILogger<DynamicEmuSchema> logger) : ISchema
{
    private static ISchema? _schema;
    private readonly Lock _lock = new();

    private ISchema GetSchema()
    {
        _schema ??= BuildSchema();

        return _schema;
    }

    public void ReloadSchema()
    {
        lock (_lock)
        {
            var newSchema = BuildSchema();
            var old = Interlocked.Exchange(ref _schema, newSchema);

            (old as IDisposable)?.Dispose();
        }
    }

    private ISchema BuildSchema()
    {
        logger.LogInformation("Building schema...");

        var schema = schemaBuilder.Build();

        logger.LogInformation("Schema ready.");

        return schema;
    }

    public ExperimentalFeatures Features { get => GetSchema().Features; set => GetSchema().Features = value; }
    public bool Initialized => GetSchema().Initialized;
    public void Initialize() => GetSchema().Initialize();
    public INameConverter NameConverter => GetSchema().NameConverter;
    public IFieldMiddlewareBuilder FieldMiddleware => GetSchema().FieldMiddleware;
    public IObjectGraphType Query { get => GetSchema().Query; set => GetSchema().Query = value; }
    public IObjectGraphType? Mutation
    {
        get => GetSchema().Mutation;
        set => GetSchema().Mutation = value;
    }
    public IObjectGraphType? Subscription
    {
        get => GetSchema().Subscription;
        set => GetSchema().Subscription = value;
    }
    public SchemaDirectives Directives => GetSchema().Directives;
    public SchemaTypes AllTypes => GetSchema().AllTypes;
    public IEnumerable<Type> AdditionalTypes => GetSchema().AdditionalTypes;
    public IEnumerable<IGraphType> AdditionalTypeInstances => GetSchema().AdditionalTypeInstances;
    public void RegisterVisitor(ISchemaNodeVisitor visitor) => GetSchema().RegisterVisitor(visitor);
    public void RegisterVisitor(Type type) => GetSchema().RegisterVisitor(type);
    public void RegisterType(IGraphType type) => GetSchema().RegisterType(type);
    public void RegisterType([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type) => GetSchema().RegisterType(type);
    public void RegisterTypeMapping(Type clrType, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type graphType) => GetSchema().RegisterTypeMapping(clrType, graphType);
    public IEnumerable<(Type clrType, Type graphType)> TypeMappings => GetSchema().TypeMappings;
    public IEnumerable<(Type clrType, Type graphType)> BuiltInTypeMappings => GetSchema().BuiltInTypeMappings;
    public ISchemaFilter Filter { get => GetSchema().Filter; set => GetSchema().Filter = value; }
    public ISchemaComparer Comparer { get => GetSchema().Comparer; set => GetSchema().Comparer = value; }
    public FieldType SchemaMetaFieldType => GetSchema().SchemaMetaFieldType;
    public FieldType TypeMetaFieldType => GetSchema().TypeMetaFieldType;
    public FieldType TypeNameMetaFieldType => GetSchema().TypeNameMetaFieldType;
    public string? Description { get => GetSchema().Description; set => GetSchema().Description = value; }
    public TType GetMetadata<TType>(string key, Func<TType> defaultValueFactory) => GetSchema().GetMetadata<TType>(key, defaultValueFactory.Invoke());
    public bool HasMetadata(string key) => GetSchema().HasMetadata(key);
    public TType GetMetadata<TType>(string key, TType defaultValue) => GetSchema().GetMetadata(key, defaultValue);
    public IDictionary<string, object?> Metadata => GetSchema().Metadata;
    public IMetadataReader MetadataReader => throw new NotImplementedException();
    Dictionary<string, object?> IProvideMetadata.Metadata => throw new NotImplementedException();
}
