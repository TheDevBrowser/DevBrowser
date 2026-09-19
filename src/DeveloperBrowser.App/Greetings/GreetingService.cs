namespace DeveloperBrowser.App.Greetings;

public sealed record Greeting(string Language, string Text, string Pronunciation);

/// <summary>Selects one greeting for the application session, shared by all new tabs.</summary>
public sealed class GreetingService
{
    // Add another entry here to include a language in the launch selection.
    public static IReadOnlyList<Greeting> AvailableGreetings { get; } = Array.AsReadOnly(new[]
    {
        new Greeting("Bengali", "কি অবস্থা !", "Ki obosta"),
        new Greeting("Hindi", "नमस्ते", "Namaste"),
        new Greeting("Urdu", "السلام علیکم", "Assalamu alaikum"),
        new Greeting("English", "Hello there !", "Hello there"),
        new Greeting("Danish", "Hej med dig!", "Hej med dig"),
        new Greeting("Swedish", "Hejsan!", "Hejsan"),
        new Greeting("Arabic", "أهلاً", "Ahlan"),
        new Greeting("French", "Bonjour !", "Bonjour"),
        new Greeting("Spanish", "Hola !", "Hola"),
        new Greeting("Malay", "Hai !", "Hai")
    });

    public Greeting CurrentGreeting { get; } = AvailableGreetings[Random.Shared.Next(AvailableGreetings.Count)];
}
