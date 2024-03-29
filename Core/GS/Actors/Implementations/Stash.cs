﻿﻿/*
 * Copyright (C) 2011 - 2018 NullD project
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
 */

using NullD.Core.GS.Map;
using NullD.Core.GS.Players;
using NullD.Net.GS.Message.Definitions.Stash;
using NullD.Net.GS.Message.Definitions.World;
using NullD.Core.GS.Common.Types.TagMap;

namespace NullD.Core.GS.Actors.Implementations
{
    [HandledSNO(130400 /* Player_Shared_Stash.acr */)]
    public sealed class Stash : Gizmo
    {
        public Stash(World world, int snoId, TagMap tags)
            : base(world, snoId, tags)
        { }

        public override void OnTargeted(Player player, TargetMessage message)
        {
            player.InGameClient.SendMessage(new OpenSharedStashMessage((int)this.DynamicID));
        }
    }
}
