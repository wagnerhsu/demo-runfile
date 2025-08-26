#:package Microsoft.Extensions.Configuration.UserSecrets@10.0.0-*
#:property UserSecretsId=runfile-flat-usersecrets-12345

using System.Reflection;
using Microsoft.Extensions.Configuration;

var key = args.Length > 0 ? args[0] : "MySecret";

var configuration = new ConfigurationBuilder()
    .AddUserSecrets(Assembly.GetExecutingAssembly())
    .Build();

Console.WriteLine(configuration[key] ?? $"""
    Value for secret '{key}' was not found. Run the following command to set one:
    dotnet user-secrets set --id runfile-flat-usersecrets-12345 {key} "This is a secret!!"
    """);
