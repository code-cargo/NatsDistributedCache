using System.Reflection;
using NatsAppHost;

var builder = DistributedApplication.CreateBuilder(args);

// Find the solution directory
var slnDir = Assembly.GetExecutingAssembly().Location;
while (!File.Exists(Path.Combine(slnDir, "CodeCargo.NatsDistributedCache.sln")))
{
    slnDir = Path.GetDirectoryName(slnDir) ??
             throw new ArgumentException("Could not find CodeCargo.NatsDistributedCache.sln in any parent directory");
}

// Get path to NATS config directory
var natsConfigDir = Path.GetFullPath(Path.Combine(slnDir, "dev", "nats-integration", "config"));

// NATS - this is the only container we need
var natsResource = new NatsResource("Nats");
builder.AddResource(natsResource)
    .WithImage("nats")
    .WithImageTag("2.11.3")
    .WithBindMount(natsConfigDir, "/etc/nats-config", isReadOnly: true)
    .WithArgs("-c", "/etc/nats-config/nats.conf")
    .WithEndpoint(port: 14222, targetPort: 4222, name: NatsResource.NatsEndpointName, scheme: "tcp");

builder.Build().Run();
