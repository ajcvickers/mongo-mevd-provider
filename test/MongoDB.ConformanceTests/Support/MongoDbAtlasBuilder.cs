// Copyright (c) Microsoft. All rights reserved.

using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.VectorData.ConformanceTests.Support;

internal sealed class MongoDbAtlasBuilder : ContainerBuilder<MongoDbAtlasBuilder, MongoDbAtlasContainer, IContainerConfiguration>
{
    private const string MongoDbAtlasImage = "mongodb/mongodb-atlas-local:latest";
    public const ushort MongoDbAtlasPort = 27017;

    public MongoDbAtlasBuilder()
        : this(new ContainerConfiguration())
        => this.DockerResourceConfiguration = this.Init().DockerResourceConfiguration;

    private MongoDbAtlasBuilder(ContainerConfiguration resourceConfiguration)
        : base(resourceConfiguration)
        => this.DockerResourceConfiguration = resourceConfiguration;

    protected override ContainerConfiguration DockerResourceConfiguration { get; }

    public override MongoDbAtlasContainer Build()
    {
        this.Validate();

        // The mongodb-atlas-local image starts mongod, then SIGTERMs and restarts it a couple of
        // seconds later to inject the mongot/search wiring (mongotHost + searchIndexManagementHostAndPort).
        // Until that restart completes, a vector query either hits the node mid-shutdown
        // (MongoNodeIsRecoveringException, ShutdownInProgress) or finds mongot's query port (27027) not yet
        // serving (Connection refused). Wait for the image's own healthcheck first - it only reports healthy
        // after the restart and mongot wiring complete - then run the search-index probe as a final check.
        var builder = this.WithWaitStrategy(Wait.ForUnixContainer()
            .UntilContainerIsHealthy()
            .AddCustomWaitStrategy(new WaitIndicateReadiness()));

        return new MongoDbAtlasContainer(builder.DockerResourceConfiguration);
    }

    protected override MongoDbAtlasBuilder Init()
        => base.Init()
            .WithImage(MongoDbAtlasImage)
            .WithPortBinding(MongoDbAtlasPort, true);

    protected override MongoDbAtlasBuilder Clone(IResourceConfiguration<CreateContainerParameters> resourceConfiguration)
        => this.Merge(this.DockerResourceConfiguration, new ContainerConfiguration(resourceConfiguration));

    protected override MongoDbAtlasBuilder Clone(IContainerConfiguration resourceConfiguration)
        => this.Merge(this.DockerResourceConfiguration, new ContainerConfiguration(resourceConfiguration));

    protected override MongoDbAtlasBuilder Merge(IContainerConfiguration oldValue, IContainerConfiguration newValue)
        => new MongoDbAtlasBuilder(new ContainerConfiguration(oldValue, newValue));

    private sealed class WaitIndicateReadiness : IWaitUntil
    {
        public async Task<bool> UntilAsync(IContainer container)
        {
            var connectionString = ((MongoDbAtlasContainer)container).GetConnectionString();

            using var client = new MongoClient(connectionString);
            var databaseName = Guid.NewGuid().ToString();
            var ready = false;

            try
            {
                var database = client.GetDatabase(databaseName);
                var collectionName = Guid.NewGuid().ToString();
                await database.CreateCollectionAsync(collectionName).ConfigureAwait(false);

                var model = new CreateSearchIndexModel(
                    Guid.NewGuid().ToString(),
                    SearchIndexType.VectorSearch,
                    BsonDocument.Parse(
                        """
                        {
                          "fields": [
                            {
                              "type": "vector",
                              "path": "Dummy",
                              "numDimensions": 8,
                              "similarity": "cosine"
                            }
                          ]
                        }
                        """));

                var collection = database.GetCollection<BsonDocument>(collectionName);
                await collection.SearchIndexes.CreateOneAsync(model).ConfigureAwait(false);
                using var _ = await collection.SearchIndexes.ListAsync().ConfigureAwait(false);
                ready = true;
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                // Container/search-index service not yet ready - will be retried on the next poll.
            }

            try
            {
                await client.DropDatabaseAsync(databaseName).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                // Best-effort cleanup of the throwaway readiness database.
            }

            return ready;
        }
    }
}
