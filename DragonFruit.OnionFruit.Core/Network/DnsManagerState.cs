﻿// OnionFruit Copyright DragonFruit Network <inbox@dragonfruit.network>
// Licensed under LGPL-3.0. Refer to the LICENCE file for more info

namespace DragonFruit.OnionFruit.Core.Network
{
    public enum DnsManagerState
    {
        Available,

        /// <summary>
        /// This manager cannot be used on this system
        /// </summary>
        Unavailable,

        /// <summary>
        /// The manager can be used, but the current user does not have the required permissions to make the required changes.
        /// </summary>
        MissingPermissions
    }
}