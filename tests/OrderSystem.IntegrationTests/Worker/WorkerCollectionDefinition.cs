namespace OrderSystem.IntegrationTests.Worker;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkerCollectionDefinition
{
    public const string Name = "Worker";
}
