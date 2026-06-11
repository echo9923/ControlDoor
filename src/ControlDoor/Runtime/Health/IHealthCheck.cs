namespace ControlDoor.Runtime.Health
{
    public interface IHealthCheck
    {
        string Name { get; }

        HealthCheckResult Run(HealthCheckContext context);
    }
}
