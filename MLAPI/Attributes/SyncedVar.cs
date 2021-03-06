﻿using System;

namespace MLAPI.Attributes
{
    /// <summary>
    /// The attribute to use for variables that should be automatically. replicated from Server to Client.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class SyncedVar : Attribute
    {
        /// <summary>
        /// The method name to invoke when the SyncVar get's updated.
        /// </summary>
        public string hookMethodName;
        /// <summary>
        /// If true, the syncedVar will only be synced to the owner.
        /// </summary>
        public bool target;
    }
}
