﻿using Robust.Client.AutoGenerated;
using Robust.Client.UserInterface.CustomControls;
using Robust.Client.UserInterface.XAML;

namespace Content.Client._RMC14.Xenonids.Egg;

[GenerateTypedNameReferences]
public sealed partial class XenoEggGhostWindow : DefaultWindow
{
    public XenoEggGhostWindow()
    {
        RobustXamlLoader.Load(this);
    }
}