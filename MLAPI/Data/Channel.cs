﻿using System;

namespace MLAPI.Data
{
    /// <summary>
    /// A data object that represents a NetworkTransport channel
    /// </summary>
    [Serializable]
    public class Channel
    {
        /// <summary>
        /// The name of the channel
        /// </summary>
        public string Name;
        /// <summary>
        /// The Transport QOS type
        /// </summary>
        public ChannelType Type;
        /// <summary>
        /// Wheter or not the channel should be encrypted
        /// </summary>
        public bool Encrypted;
    }
}
