using Terraria.ModLoader;
using Jevaria.Combat;

namespace Jevaria;

public sealed class Jevaria : Mod
{
    internal static JevBrain Brain { get; private set; } = null!;

    public override void Load() => Brain = new JevBrain();

    public override void Unload() => Brain.Dispose();
}
