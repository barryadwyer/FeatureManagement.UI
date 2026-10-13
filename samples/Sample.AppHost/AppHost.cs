var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Sample_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Sample_Blazor>("blazor")
    .WithExternalHttpEndpoints()
    .WithReference(api)
    .WaitFor(api);

builder.AddProject<Projects.Sample_Mvc>("mvc")
    .WithExternalHttpEndpoints()
    .WithReference(api)
    .WaitFor(api);

builder.AddProject<Projects.Sample_RazorPages>("razorpages")
    .WithExternalHttpEndpoints()
    .WithReference(api)
    .WaitFor(api);

builder.AddViteApp("react", "../Sample.React")
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
