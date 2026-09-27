using Terraria.ModLoader;
using Jevaria.Combat;

namespace Jevaria;

public sealed class Jevaria : Mod
{
    internal static JevBrain Brain { get; private set; } = null!;
    internal static ModKeybind DodgeToggle { get; private set; } = null!;

    public override void Load()
    {
        Brain = new JevBrain();
        DodgeToggle = KeybindLoader.RegisterKeybind(this, "ToggleDodge", "P");
    }

    public override void Unload()
    {
        Brain.Dispose();
        DodgeToggle = null!;
    }
}
