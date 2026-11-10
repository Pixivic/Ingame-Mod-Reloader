using Terraria;
using Terraria.ModLoader;

namespace ModReloader;

public sealed class ReloaderMod : Mod
{
    public override void Load()
    {
        if (!Main.dedServ)
            ReloadSession.AttachResume();
    }
}
