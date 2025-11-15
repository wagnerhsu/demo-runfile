#!/usr/bin/env dotnet
#:package Microsoft.NET.Test.Sdk@18.0.0
#:package TUnit.Playwright@1.1.10
#:property PublishAot=false

using TUnit.Playwright;
using System.Diagnostics;
using System.Text.RegularExpressions;

public partial class Tests : PageTest
{
    [Test]
    public async Task HomepageHasPlaywrightInTitleAndGetStartedLinkLinkingtoTheIntroPage()
    {
        await Page.GotoAsync("https://playwright.dev");

        // Expect a title "to contain" a substring.
        await Expect(Page).ToHaveTitleAsync(PlaywrightRegex());

        // create a locator
        var getStarted = Page.Locator("text=Get Started");

        // Expect an attribute "to be strictly equal" to the value.
        await Expect(getStarted).ToHaveAttributeAsync("href", "/docs/intro");

        // Click the get started link.
        await getStarted.ClickAsync();

        // Expects the URL to contain intro.
        await Expect(Page).ToHaveURLAsync(IntroRegex());
    }

    [GeneratedRegex("Playwright")]
    public static partial Regex PlaywrightRegex();

    [GeneratedRegex(".*intro")]
    public static partial Regex IntroRegex();
}

public partial class GlobalHooks
{
    [Before(TestSession)]
    public static void InstallPlaywright()
    {
        Console.WriteLine("Installing Playwright browsers...");

        //install playwright
        if (Debugger.IsAttached)
        {
            Environment.SetEnvironmentVariable("PWDEBUG", "1");
        }

        // Set environment variable to prevent PowerShell profile loading
        var originalPsNoProfile = Environment.GetEnvironmentVariable("POWERSHELL_NOPROFILE");
        Environment.SetEnvironmentVariable("POWERSHELL_NOPROFILE", "1");

        try
        {
            Microsoft.Playwright.Program.Main(["install-deps"]);
            Microsoft.Playwright.Program.Main(["install"]);
        }
        finally
        {
            // Restore original value
            Environment.SetEnvironmentVariable("POWERSHELL_NOPROFILE", originalPsNoProfile);
        }
    }
}
