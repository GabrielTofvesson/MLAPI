﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine.Networking;

namespace MLAPI
{
    public class NetworkingConfiguration
    {
        public ushort ProtocolVersion = 0;
        public SortedDictionary<string, QosType> Channels = new SortedDictionary<string, QosType>();
        public List<string> MessageTypes = new List<string>();
        public List<string> PassthroughMessageTypes = new List<string>();
        public List<string> EncryptionMessageTypes = new List<string>();
        internal HashSet<ushort> RegisteredPassthroughMessageTypes = new HashSet<ushort>();
        internal HashSet<ushort> RegisteredEncryptionMessageTypes = new HashSet<ushort>();
        public int MessageBufferSize = 65535;
        public int MaxMessagesPerFrame = 150;
        public int MaxConnections = 100;
        public int Port = 7777;
        public string Address = "127.0.0.1";
        public int ClientConnectionBufferTimeout = 10;
        public bool ConnectionApproval = false;
        public Action<byte[], int, Action<int, bool>> ConnectionApprovalCallback = null;
        public byte[] ConnectionData = new byte[0];
        public bool HandleObjectSpawning = true;
        //TODO
        public bool CompressMessages = false;
        public bool AllowPassthroughMessages = true;
        public bool EnableDiffieHellman = true;
        public bool DenyUntrustedKeys = false;
        public RSAParameters ServerPublicKey;
        public RSAParameters ServerPrivateKey;

        //Cached config hash
        private byte[] ConfigHash = null;
        public byte[] GetConfig(bool cache = true)
        {
            if (ConfigHash != null && cache)
                return ConfigHash;

            using(MemoryStream writeStream = new MemoryStream())
            {
                using(BinaryWriter writer = new BinaryWriter(writeStream))
                {
                    writer.Write(ProtocolVersion);
                    foreach (KeyValuePair<string, QosType> pair in Channels)
                    {
                        writer.Write(pair.Key);
                        writer.Write((int)pair.Value);
                    }
                    MessageTypes.Sort();
                    PassthroughMessageTypes.Sort();
                    for (int i = 0; i < MessageTypes.Count; i++)
                    {
                        writer.Write(MessageTypes[i]);
                    }
                    for (int i = 0; i < PassthroughMessageTypes.Count; i++)
                    {
                        writer.Write(PassthroughMessageTypes[i]);
                    }
                    writer.Write(HandleObjectSpawning);
                    writer.Write(CompressMessages);
                    writer.Write(EnableDiffieHellman);
                    writer.Write(AllowPassthroughMessages);
                }
                using(SHA256Managed sha256 = new SHA256Managed())
                {
                    //Returns a 256 bit / 32 byte long checksum of the config
                    if (cache)
                    {
                        ConfigHash = sha256.ComputeHash(writeStream.ToArray());
                        return ConfigHash;
                    }
                    return sha256.ComputeHash(writeStream.ToArray());
                }
            }
        }

        public bool CompareConfig(byte[] hash)
        {
            byte[] localConfigHash = GetConfig();

            if (hash.Length != localConfigHash.Length)
                return false;

            for (int i = 0; i < hash.Length; i++)
            {
                if (hash[i] != localConfigHash[i])
                    return false;
            }
            return true;
        }
    }
}
