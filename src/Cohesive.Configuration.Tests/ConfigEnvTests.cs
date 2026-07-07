#pragma warning disable CS9113 // Parameter is unread.
using Microsoft.Extensions.Configuration;

namespace Cohesive.Configuration.Tests;

public class ConfigEnvTests
{
    class CosmosStorageOptions
    {
        public string? Endpoint { get; init; }
        
        public string? Key { get; init; }
    }


    class CosmosClient(CosmosStorageOptions options);
    
    sealed record CosmosInstance(string Name);
    
    class TrainingService(CosmosClient client);
    
    static class MyCosmosConfig
    {
        public static readonly ConfigKey<CosmosStorageOptions> DefaultOptions = new("Cosmos:Default:Options");
        public static readonly ConfigKey<CosmosStorageOptions> AnalyticsOptions = new("Cosmos:Analytics:Options");
        public static readonly ConfigKey<CosmosClient> DefaultClient = new("Cosmos:Default:Client");
        public static readonly ConfigKey<CosmosClient> AnalyticsClient = new("Cosmos:Analytics:Client");
        public static readonly ConfigKey<TrainingService> TrainingModuleKey = new("Modules:Training");
        
        static ConfigKey<CosmosStorageOptions> Options(CosmosInstance i) => new($"Cosmos:{i.Name}:Options");

        static ConfigKey<CosmosClient> Client(CosmosInstance i) => new($"Cosmos:{i.Name}:Client");

        public static ConfigFragment Cosmos2(IConfiguration configuration) => builder =>
        {
            var cosmos = new CosmosInstance("Default");

            var section = configuration.GetSection("Training:Infrastructure:Cosmos:Default");
            var options = section.Get<CosmosStorageOptions>()!;
            
            builder.BindValue(Options(cosmos), options);
            
            builder.Bind(Client(cosmos), env =>
            {
                var options = env.Resolve(Options(cosmos));
                return new CosmosClient(options);
            });
        };
        
        public static ConfigFragment TrainingModule(IConfiguration configuration) => builder =>
        {
            var cosmos = new CosmosInstance("Default");
            
            builder.Bind(TrainingModuleKey, env =>
            {
                var client = env.Resolve(Client(cosmos));
                return new TrainingService(client);
            });
        };
    }
    
    [Fact]
    public void Should_Be_Implemented()
    {
        var cb = new ConfigurationBuilder();
        var configuration = cb.Build();
        
        var builder = new ConfigBuilder();
        builder.Include(MyCosmosConfig.Cosmos2(configuration));
        
        var env = builder.Build();

        Assert.NotNull(env);

        //var trainingModule = env.Resolve(MyCosmosConfig.TrainingModuleKey);
        //Assert.NotNull(trainingModule);
    }
}