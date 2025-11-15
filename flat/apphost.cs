#!/usr/bin/env aspire

#:sdk Aspire.AppHost.Sdk@13.0.0
#:package Aspire.Hosting.Redis@13.0.0

var builder = DistributedApplication.CreateBuilder(args);

var webapi = builder.AddCSharpApp("webapi", "./webapi.cs");   

builder.AddCSharpApp("razorapp", "../razorapp/razorapp.cs")
    .WithReference(webapi).WaitFor(webapi);

if (!string.Equals(builder.Configuration["DOTNET_LAUNCH_PROFILE"], "verify", StringComparison.OrdinalIgnoreCase)
    || Environment.GetEnvironmentVariable("VERIFY_MODE") != "1")
{
    var redis = builder.AddRedis("redis");
    webapi.WithReference(redis).WaitFor(redis);
}

builder.Build().Run();
