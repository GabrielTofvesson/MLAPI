﻿namespace MLAPI.Data.Transports.UNET
{
    /// <summary>
    /// Represents a ClientId structure
    /// </summary>
    public struct NetId
    {
        /// <summary>
        /// The hostId this client is on
        /// </summary>
        public byte HostId;
        /// <summary>
        /// The connectionId this client is assigned
        /// </summary>
        public ushort ConnectionId;
        /// <summary>
        /// Meta data about hte client
        /// </summary>
        public byte Meta;

        /// <summary>
        /// Returns wheter or not the clientId represents a -1
        /// </summary>
        /// <returns><c>true</c>, if host, <c>false</c> otherwise.</returns>
        public bool IsHost()
        {
            return Meta == 1;
        }
        /// <summary>
        /// Returns if this is a invalid clientId, (-2)
        /// </summary>
        /// <returns><c>true</c>, if invalid, <c>false</c> otherwise.</returns>
        public bool IsInvalid()
        {
            return Meta == 2;
        }
        /// <summary>
        /// Initializes a new instance of the netId struct from transport values
        /// </summary>
        /// <param name="hostId">Host identifier.</param>
        /// <param name="connectionId">Connection identifier.</param>
        /// <param name="isHost">If set to <c>true</c> is host.</param>
        /// <param name="isInvalid">If set to <c>true</c> is invalid.</param>
        public NetId(byte hostId, ushort connectionId, bool isHost, bool isInvalid)
        {
            HostId = hostId;
            ConnectionId = connectionId;
            if (isHost)
                Meta = 1;
            else if (isInvalid)
                Meta = 2;
            else
                Meta = 0;
        }
        /// <summary>
        /// Initializes a new instance of the netId struct from a clientId
        /// </summary>
        /// <param name="clientId">Client identifier.</param>
        public NetId(uint clientId)
        {
            HostId = (byte)(clientId & 0xFF);
            ConnectionId = (ushort)((byte)((clientId >> 8) & 0xFF) | (ushort)(((clientId >> 16) & 0xFF) << 8));
            Meta = (byte)((clientId >> 24) & 0xFF);
        }
        /// <summary>
        /// Gets the clientId.
        /// </summary>
        /// <returns>The client identifier.</returns>
        public uint GetClientId()
        {
            return HostId | (uint)((ConnectionId & 0xFF) << 8) | (uint)(((ConnectionId >> 8) & 0xFF) << 16) | (uint)(Meta << 24);
        }
        // Rider generated vvv
        /// <summary>
        /// Determines whether the specified <see cref="object"/> is equal to the current <see cref="T:MLAPI.Data.NetId"/>.
        /// </summary>
        /// <param name="obj">The <see cref="object"/> to compare with the current <see cref="T:MLAPI.Data.NetId"/>.</param>
        /// <returns><c>true</c> if the specified <see cref="object"/> is equal to the current <see cref="T:MLAPI.Data.NetId"/>;
        /// otherwise, <c>false</c>.</returns>
        public override bool Equals (object obj)
        {
            if (obj == null || GetType() != obj.GetType())
                return false;

            NetId key = (NetId)obj;
            return (HostId == key.HostId) && (ConnectionId == key.ConnectionId);
        }
        // Rider generated vvv
        /// <summary>
        /// Serves as a hash function for a <see cref="T:MLAPI.Data.NetId"/> object.
        /// </summary>
        /// <returns>A hash code for this instance that is suitable for use in hashing algorithms and data structures such as a
        /// hash table.</returns>
        public override int GetHashCode()
        {
            return (int)GetClientId();
        }
        // Rider generated vvv
        /// <summary>
        /// Determines whether a specified instance of <see cref="MLAPI.Data.NetId"/> is equal to another specified <see cref="MLAPI.Data.NetId"/>.
        /// </summary>
        /// <param name="client1">The first <see cref="MLAPI.Data.NetId"/> to compare.</param>
        /// <param name="client2">The second <see cref="MLAPI.Data.NetId"/> to compare.</param>
        /// <returns><c>true</c> if <c>client1</c> and <c>client2</c> are equal; otherwise, <c>false</c>.</returns>
        public static bool operator ==(NetId client1, NetId client2)
        {
            return (client1.HostId == client2.HostId && client1.ConnectionId == client2.ConnectionId) || (client1.IsHost() == client2.IsHost());
        }
        // Rider generated vvv
        /// <summary>
        /// Determines whether a specified instance of <see cref="MLAPI.Data.NetId"/> is not equal to another specified <see cref="MLAPI.Data.NetId"/>.
        /// </summary>
        /// <param name="client1">The first <see cref="MLAPI.Data.NetId"/> to compare.</param>
        /// <param name="client2">The second <see cref="MLAPI.Data.NetId"/> to compare.</param>
        /// <returns><c>true</c> if <c>client1</c> and <c>client2</c> are not equal; otherwise, <c>false</c>.</returns>
        public static bool operator !=(NetId client1, NetId client2)
        {
            return !(client1 == client2);
        }
    }
}
