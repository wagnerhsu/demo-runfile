#:package Microsoft.Extensions.Configuration.UserSecrets@10.0.0-*
#:property LangVersion=preview

using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;

var key = args.Length > 0 ? args[0] : "MySecret";

var configuration = new ConfigurationBuilder()
    .AddUserSecrets()
    .Build();

Console.WriteLine(configuration[key] ?? $"""
    Value for secret '{key}' was not found. Run the following command to set one:
    dotnet user-secrets set --id {IConfigurationBuilder.GetDefaultUserSecretsId()} {key} "This is a secret!!"
    """);

static class UserSecretsExtensions
{
    extension(IConfigurationBuilder builder)
    {
        /// <summary>
        /// Gets the implicit user secrets ID.
        /// </summary>
        /// <remarks>
        /// This method defaults the user secrets ID to:
        /// - the value of the <c>UserSecretsIdAttribute</c> attribute in the entry assembly if present
        /// - for file-based apps, a value derived from the app file path
        /// - otherwise, the string "global"
        /// </remarks>
        public static string GetDefaultUserSecretsId()
        {
            var userSecretsId = Assembly.GetEntryAssembly()?.GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId;
            if (userSecretsId is not null)
            {
                // Use the UserSecretsId from the attribute
                return userSecretsId;
            }

            if (AppContext.GetData("EntryPointFilePath") is string entryPointFilePath)
            {
                // Use the file path to generate a unique ID for user secrets
                return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entryPointFilePath)));
            }

            return "global";
        }

        /// <summary>
        /// Adds user secrets configuration source to <see cref="IConfigurationBuilder"/> based on the user secrets ID returned by <see cref="GetDefaultUserSecretsId"/>.
        /// </summary>
        public IConfigurationBuilder AddUserSecrets()
        {
            return builder.AddUserSecrets(GetDefaultUserSecretsId());
        }
    }
}
