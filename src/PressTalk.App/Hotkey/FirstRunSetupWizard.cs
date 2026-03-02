namespace PressTalk.App.Hotkey;

public static class FirstRunSetupWizard
{
    public static HoldKeyPreset SelectPreset()
    {
        var presets = HoldKeyPresetCatalog.All;

        while (true)
        {
            Console.WriteLine("Select one hold key for PressTalk:");
            for (var i = 0; i < presets.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {presets[i].DisplayName}");
            }

            Console.Write("Input number and press Enter: ");
            var input = Console.ReadLine();

            if (int.TryParse(input, out var choice) &&
                choice >= 1 &&
                choice <= presets.Count)
            {
                return presets[choice - 1];
            }

            Console.WriteLine("Invalid input. Try again.");
            Console.WriteLine();
        }
    }
}

