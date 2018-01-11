﻿using MLAPI.NetworkingManagerComponents;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Networking;

namespace MLAPI
{
    public class NetworkingManager : MonoBehaviour
    {
        public List<GameObject> SpawnablePrefabs;
        public GameObject DefaultPlayerPrefab;
        public static NetworkingManager singleton;
        //Client only, what my connectionId is on the server
        public int MyClientId;
        internal Dictionary<int, NetworkedClient> connectedClients;
        internal Dictionary<int, ECDiffieHellmanPublicKey> server_pendingPublicKeys;
        internal ECDiffieHellmanCng client_pendingDiffieHellmanExchange;
        internal Dictionary<int, byte[]> encryptionKeys;
        public Dictionary<int, NetworkedClient> ConnectedClients
        {
            get
            {
                return connectedClients;
            }
        }
        internal HashSet<int> pendingClients;
        internal bool isServer;
        internal bool isClient;
        public bool isHost
        {
            get
            {
                return isServer && isClient;
            }
        }
        private bool isListening;
        private byte[] messageBuffer;
        internal int serverClientId;


        public NetworkingConfiguration NetworkConfig;

        private void OnValidate()
        {
            for (int i = 0; i < SpawnablePrefabs.Count; i++)
            {
                NetworkedObject netObject = SpawnablePrefabs[i].GetComponentInChildren<NetworkedObject>();
                if (netObject == null)
                {
                    Debug.LogWarning("MLAPI: All SpawnablePrefabs need a NetworkedObject component. Please add one to the prefab " + SpawnablePrefabs[i].gameObject.name);
                    continue;
                }
                netObject.SpawnablePrefabIndex = i;
            }
        }

        private ConnectionConfig Init(NetworkingConfiguration netConfig)
        {
            NetworkConfig = netConfig;

            pendingClients = new HashSet<int>();
            connectedClients = new Dictionary<int, NetworkedClient>();
            messageBuffer = new byte[NetworkConfig.MessageBufferSize];
            MessageManager.channels = new Dictionary<string, int>();
            MessageManager.messageTypes = new Dictionary<string, ushort>();
            MessageManager.messageCallbacks = new Dictionary<ushort, Dictionary<int, Action<int, byte[]>>>();
            MessageManager.messageHandlerCounter = new Dictionary<ushort, int>();
            MessageManager.releasedMessageHandlerCounters = new Dictionary<ushort, Stack<int>>();
            MessageManager.targetedMessages = new Dictionary<ushort, Dictionary<uint, List<int>>>();
            MessageManager.reverseChannels = new Dictionary<int, string>();
            MessageManager.reverseMessageTypes = new Dictionary<ushort, string>();
            SpawnManager.spawnedObjects = new Dictionary<uint, NetworkedObject>();
            SpawnManager.releasedNetworkObjectIds = new Stack<uint>();
            server_pendingPublicKeys = new Dictionary<int, ECDiffieHellmanPublicKey>();
            client_pendingDiffieHellmanExchange = null;
            encryptionKeys = new Dictionary<int, byte[]>();
            if (NetworkConfig.HandleObjectSpawning)
            {
                NetworkedObject[] sceneObjects = FindObjectsOfType<NetworkedObject>();
                for (int i = 0; i < sceneObjects.Length; i++)
                {
                    uint networkId = SpawnManager.GetNetworkObjectId();
                    SpawnManager.spawnedObjects.Add(networkId, sceneObjects[i]);
                }
            }

            NetworkTransport.Init();
            ConnectionConfig cConfig = new ConnectionConfig();

            //MLAPI channels and messageTypes
            NetworkConfig.Channels.Add("MLAPI_RELIABLE_FRAGMENTED_SEQUENCED", QosType.ReliableFragmentedSequenced);
            NetworkConfig.Channels.Add("MLAPI_POSITION_UPDATE", QosType.StateUpdate);
            MessageManager.messageTypes.Add("MLAPI_CONNECTION_REQUEST", 0);
            MessageManager.messageTypes.Add("MLAPI_CONNECTION_APPROVED", 1);
            MessageManager.messageTypes.Add("MLAPI_ADD_OBJECT", 2);
            MessageManager.messageTypes.Add("MLAPI_CLIENT_DISCONNECT", 3);
            MessageManager.messageTypes.Add("MLAPI_DESTROY_OBJECT", 4);
            NetworkConfig.MessageTypes.Add("MLAPI_OnRecieveTransformFromClient");
            NetworkConfig.MessageTypes.Add("MLAPI_OnRecieveTransformFromServer");

            HashSet<string> channelNames = new HashSet<string>();
            foreach (KeyValuePair<string, QosType> pair in NetworkConfig.Channels)
            {
                if(channelNames.Contains(pair.Key))
                {
                    Debug.LogWarning("MLAPI: Duplicate channel name: " + pair.Key);
                    continue;
                }
                int channelId = cConfig.AddChannel(pair.Value);
                MessageManager.channels.Add(pair.Key, channelId);
                channelNames.Add(pair.Key);
                MessageManager.reverseChannels.Add(channelId, pair.Key);
            }
            //0-32 are reserved for MLAPI messages
            ushort messageId = 32;
            for (ushort i = 0; i < NetworkConfig.MessageTypes.Count; i++)
            {
                MessageManager.messageTypes.Add(NetworkConfig.MessageTypes[i], messageId);
                MessageManager.reverseMessageTypes.Add(messageId, NetworkConfig.MessageTypes[i]);
                messageId++;
            }

            if (NetworkConfig.AllowPassthroughMessages)
            {
                for (int i = 0; i < NetworkConfig.PassthroughMessageTypes.Count; i++)
                {
                    NetworkConfig.RegisteredPassthroughMessageTypes.Add(MessageManager.messageTypes[NetworkConfig.PassthroughMessageTypes[i]]);
                }
            }

            return cConfig;
        }


        public void StartServer(NetworkingConfiguration netConfig)
        {
            ConnectionConfig cConfig = Init(netConfig);
            if (NetworkConfig.ConnectionApproval)
            {
                if (NetworkConfig.ConnectionApprovalCallback == null)
                {
                    Debug.LogWarning("MLAPI: No ConnectionApproval callback defined. Connection approval will timeout");
                }
            }
            HostTopology hostTopology = new HostTopology(cConfig, NetworkConfig.MaxConnections);
            hostId = NetworkTransport.AddHost(hostTopology, NetworkConfig.Port);
            isServer = true;
            isClient = false;
            isListening = true;
        }

        public void StartClient(NetworkingConfiguration netConfig)
        {
            ConnectionConfig cConfig = Init(netConfig);
            HostTopology hostTopology = new HostTopology(cConfig, NetworkConfig.MaxConnections);
            hostId =  NetworkTransport.AddHost(hostTopology, 0, null);

            isServer = false;
            isClient = true;
            isListening = true;
            serverClientId = NetworkTransport.Connect(hostId, NetworkConfig.Address, NetworkConfig.Port, 0, out error);
        }

        public void StopServer()
        {
            HashSet<int> sentIds = new HashSet<int>();
            //Don't know if I have to disconnect the clients. I'm assuming the NetworkTransport does all the cleaning on shtudown. But this way the clients get a disconnect message from server (so long it does't get lost)
            foreach (KeyValuePair<int, NetworkedClient> pair in connectedClients)
            {
                if(!sentIds.Contains(pair.Key))
                {
                    sentIds.Add(pair.Key);
                    NetworkTransport.Disconnect(hostId, pair.Key, out error);
                }
            }
            foreach (int clientId in pendingClients)
            {
                if (!sentIds.Contains(clientId))
                {
                    sentIds.Add(clientId);
                    NetworkTransport.Disconnect(hostId, clientId, out error);
                }
            }
            Shutdown();
        }

        public void StopHost()
        {
            StopServer();
            //We don't stop client since we dont actually have a transport connection to our own host. We just handle host messages directly in the MLAPI
        }

        public void StopClient()
        {
            NetworkTransport.Disconnect(hostId, serverClientId, out error);
            Shutdown();
        }

        public void StartHost(NetworkingConfiguration netConfig)
        {
            ConnectionConfig cConfig = Init(netConfig);
            if (NetworkConfig.ConnectionApproval)
            {
                if (NetworkConfig.ConnectionApprovalCallback == null)
                {
                    Debug.LogWarning("MLAPI: No ConnectionApproval callback defined. Connection approval will timeout");
                }
            }
            HostTopology hostTopology = new HostTopology(cConfig, NetworkConfig.MaxConnections);
            hostId = NetworkTransport.AddHost(hostTopology, NetworkConfig.Port, null);
            isServer = true;
            isClient = true;
            isListening = true;
            connectedClients.Add(-1, new NetworkedClient() { ClientId = -1 });
            if(NetworkConfig.HandleObjectSpawning)
            {
                SpawnManager.SpawnPlayerObject(-1, 0);
            }
        }

        private void OnEnable()
        {
            if (singleton != null)
            {
                Debug.LogWarning("MLAPI: Multiple NetworkingManagers");
                Destroy(this);
                return;
            }
            singleton = this;
            DontDestroyOnLoad(gameObject);
        }
        
        private void OnDestroy()
        {
            singleton = null;
            Shutdown();
        }

        private void Shutdown()
        {
            isListening = false;
            isClient = false;
            isServer = false;
            NetworkTransport.Shutdown();
        }

        //Receive stuff
        int hostId;
        int clientId;
        int channelId;
        int receivedSize;
        byte error;
        private void Update()
        {
            if(isListening)
            {
                NetworkEventType eventType;
                int messagesProcessed = 0;
                do
                {
                    messagesProcessed++;
                    eventType = NetworkTransport.Receive(out hostId, out clientId, out channelId, messageBuffer, messageBuffer.Length, out receivedSize, out error);
                    NetworkError networkError = (NetworkError)error;
                    if(networkError == NetworkError.Timeout)
                    {
                        //Client timed out. 
                        if (isServer)
                        {
                            OnClientDisconnect(clientId);
                            continue;
                        }
                    }
                    else if (networkError != NetworkError.Ok)
                    {
                        Debug.LogWarning("MLAPI: NetworkTransport receive error: " + networkError.ToString());
                        continue;
                    }
                    switch (eventType)
                    {
                        case NetworkEventType.ConnectEvent:
                            if (isServer)
                            {
                                pendingClients.Add(clientId);
                                StartCoroutine(ApprovalTimeout(clientId));
                            }
                            else
                            {
                                int sizeOfStream = 32;
                                if (NetworkConfig.ConnectionApproval)
                                    sizeOfStream += 2 + NetworkConfig.ConnectionData.Length;

                                byte[] clientPubKey = new byte[0];
                                if(NetworkConfig.EnableDiffieHellman)
                                {
                                    ECDiffieHellmanCng diffieHellman = new ECDiffieHellmanCng();
                                    client_pendingDiffieHellmanExchange = diffieHellman;
                                    clientPubKey = diffieHellman.PublicKey.ToByteArray();
                                }

                                using (MemoryStream writeStream = new MemoryStream(sizeOfStream))
                                {
                                    using (BinaryWriter writer = new BinaryWriter(writeStream))
                                    {
                                        writer.Write(NetworkConfig.GetConfig());
                                        if(NetworkConfig.EnableDiffieHellman)
                                        {
                                            writer.Write((ushort)clientPubKey.Length);
                                            writer.Write(clientPubKey);
                                        }
                                        if (NetworkConfig.ConnectionApproval)
                                        {
                                            writer.Write((ushort)NetworkConfig.ConnectionData.Length);
                                            writer.Write(NetworkConfig.ConnectionData);
                                        }
                                    }
                                    Send(clientId, "MLAPI_CONNECTION_REQUEST", "MLAPI_RELIABLE_FRAGMENTED_SEQUENCED", writeStream.GetBuffer());
                                }
                            }
                            
                            break;
                        case NetworkEventType.DataEvent:
                            HandleIncomingData(clientId, messageBuffer, channelId);
                            break;
                        case NetworkEventType.DisconnectEvent:
                            if(isServer)
                            {
                                OnClientDisconnect(clientId);
                            }
                            break;
                    }
                } while (eventType != NetworkEventType.Nothing && 
                    (messagesProcessed < NetworkConfig.MaxMessagesPerFrame || NetworkConfig.MaxMessagesPerFrame < 0));

            }
        }

        private IEnumerator ApprovalTimeout(int clientId)
        {
            float timeStarted = Time.time;
            //We yield every frame incase a pending client disconnects and someone else gets its connection id
            while (Time.time - timeStarted < NetworkConfig.ClientConnectionBufferTimeout && pendingClients.Contains(clientId))
            {
                yield return null;
            }
            if(pendingClients.Contains(clientId) && !connectedClients.ContainsKey(clientId))
            {
                //Timeout
                DisconnectClient(clientId);
            }
        }

        private void HandleIncomingData(int clientId, byte[] data, int channelId)
        {
            using(MemoryStream readStream = new MemoryStream(data))
            {
                using (BinaryReader reader = new BinaryReader(readStream))
                {
                    ushort messageType = reader.ReadUInt16();
                    bool targeted = reader.ReadBoolean();
                    uint targetNetworkId = 0;
                    if(targeted)
                        targetNetworkId = reader.ReadUInt32();
                    bool isPassthrough = reader.ReadBoolean();

                    int passthroughOrigin = 0;
                    int passthroughTarget = 0;

                    if (isPassthrough && isServer)
                        passthroughTarget = reader.ReadInt32();
                    else if (isPassthrough && !isServer)
                        passthroughOrigin = reader.ReadInt32();


                    //Client tried to send a network message that was not the connection request before he was accepted.
                    if (isServer && pendingClients.Contains(clientId) && messageType != 0)
                    {
                        Debug.LogWarning("MLAPI: Message recieved from clientId " + clientId + " before it has been accepted");
                        return;
                    }


                    ushort bytesToRead = reader.ReadUInt16();
                    byte[] incommingData = reader.ReadBytes(bytesToRead);

                    if (isServer && isPassthrough && !NetworkConfig.RegisteredPassthroughMessageTypes.Contains(messageType))
                    {
                        Debug.LogWarning("MLAPI: Client " + clientId + " tried to send a passthrough message for a messageType not registered as passthrough");
                        return;
                    }
                    else if(isClient && isPassthrough && !NetworkConfig.RegisteredPassthroughMessageTypes.Contains(messageType))
                    {
                        Debug.LogWarning("MLAPI: Server tried to send a passthrough message for a messageType not registered as passthrough");
                        return;
                    }
                    else if(isServer && isPassthrough)
                    {
                        if (!connectedClients.ContainsKey(passthroughTarget))
                        {
                            Debug.LogWarning("MLAPI: Passthrough message was sent with invalid target: " + passthroughTarget + " from client " + clientId);
                            return;
                        }
                        uint? netIdTarget = null;
                        if (targeted)
                            netIdTarget = targetNetworkId;
                        PassthroughSend(passthroughTarget, clientId, messageType, channelId, incommingData, netIdTarget);
                        return;
                    }
                       
                    if (messageType >= 32)
                    {
                        //Custom message, invoke all message handlers
                        if(targeted)
                        {
                            List<int> handlerIds = MessageManager.targetedMessages[messageType][targetNetworkId];
                            for (int i = 0; i < handlerIds.Count; i++)
                            {
                                if (isPassthrough)
                                    MessageManager.messageCallbacks[messageType][handlerIds[i]](passthroughOrigin, incommingData);
                                else
                                    MessageManager.messageCallbacks[messageType][handlerIds[i]](clientId, incommingData);
                            }
                        }
                        else
                        {
                            foreach (KeyValuePair<int, Action<int, byte[]>> pair in MessageManager.messageCallbacks[messageType])
                            {
                                if (isPassthrough)
                                    pair.Value(passthroughOrigin, incommingData);
                                else
                                    pair.Value(clientId, incommingData);
                            }
                        }
                    }
                    else
                    {
                        //MLAPI message
                        switch (messageType)
                        {
                            case 0: //Client to server > sends connection buffer
                                if (isServer)
                                {
                                    using (MemoryStream messageReadStream = new MemoryStream(incommingData))
                                    {
                                        using (BinaryReader messageReader = new BinaryReader(messageReadStream))
                                        {
                                            byte[] configHash = messageReader.ReadBytes(32);
                                            if (!NetworkConfig.CompareConfig(configHash))
                                            {
                                                Debug.LogWarning("MLAPI: NetworkConfiguration missmatch. The configuration between the server and client does not match.");
                                                DisconnectClient(clientId);
                                                return;
                                            }
                                            if(NetworkConfig.EnableDiffieHellman)
                                            {
                                                ushort pubKeyLength = messageReader.ReadUInt16();
                                                byte[] clientPubKeyBlob = messageReader.ReadBytes(pubKeyLength);
                                                ECDiffieHellmanPublicKey clientPubKey = ECDiffieHellmanCngPublicKey.FromByteArray(clientPubKeyBlob, CngKeyBlobFormat.EccPublicBlob);
                                                byte[] serverPubKeyBlob = new byte[0];
                                                using (ECDiffieHellmanCng diffieHellman = new ECDiffieHellmanCng())
                                                {
                                                    encryptionKeys.Add(clientId, diffieHellman.DeriveKeyMaterial(clientPubKey));
                                                    server_pendingPublicKeys.Add(clientId, diffieHellman.PublicKey);
                                                }
                                            }
                                            if (NetworkConfig.ConnectionApproval)
                                            {
                                                ushort bufferSize = messageReader.ReadUInt16();
                                                byte[] connectionBuffer = messageReader.ReadBytes(bufferSize);
                                                NetworkConfig.ConnectionApprovalCallback(connectionBuffer, clientId, HandleApproval);
                                            }
                                            else
                                            {
                                                HandleApproval(clientId, true);
                                            }
                                        }
                                    }
                                }
                                break;
                            case 1: //Server informs client it has been approved:
                                if (isClient)
                                {
                                    //SceneManager.LoadScene(PlaySceneIndex);
                                    using (MemoryStream messageReadStream = new MemoryStream(incommingData))
                                    {
                                        using (BinaryReader messageReader = new BinaryReader(messageReadStream))
                                        {
                                            if(NetworkConfig.EnableDiffieHellman)
                                            {
                                                ushort serverPubKeyLength = messageReader.ReadUInt16();
                                                byte[] serverPubKeyBlob = messageReader.ReadBytes(serverPubKeyLength);
                                                if(NetworkConfig.DenyUntrustedKeys)
                                                {
                                                    ushort signedPubKeyLength = messageReader.ReadUInt16();
                                                    byte[] signedPubKey = messageReader.ReadBytes(signedPubKeyLength);
                                                    //TODO: NEEDS THREADING!!!!
                                                    using(RSA rsa = RSA.Create())
                                                    {
                                                        rsa.ImportParameters(NetworkConfig.ServerPublicKey);
                                                        byte[] decryptedSignedPubKey = rsa.DecryptValue(signedPubKey);
                                                        if(!serverPubKeyBlob.SequenceEqual(decryptedSignedPubKey))
                                                        {
                                                            //Man in the middle attack detected. Or the RSA keys are wrong ;)
                                                        }
                                                    }
                                                }
                                                ECDiffieHellmanCng diffieHellman = client_pendingDiffieHellmanExchange;
                                                ECDiffieHellmanPublicKey serverPubKey = ECDiffieHellmanCngPublicKey.FromByteArray(serverPubKeyBlob, CngKeyBlobFormat.EccPublicBlob);
                                                encryptionKeys.Add(serverClientId, diffieHellman.DeriveKeyMaterial(serverPubKey));
                                                diffieHellman.Clear();
                                                client_pendingDiffieHellmanExchange = null;
                                            }
                                            MyClientId = messageReader.ReadInt32();
                                            connectedClients.Add(MyClientId, new NetworkedClient() { ClientId = MyClientId });
                                            int clientCount = messageReader.ReadInt32();
                                            for (int i = 0; i < clientCount; i++)
                                            {
                                                int conId = messageReader.ReadInt32();
                                                connectedClients.Add(conId, new NetworkedClient() { ClientId = conId });
                                            }
                                            if(NetworkConfig.HandleObjectSpawning)
                                            {
                                                int objectCount = messageReader.ReadInt32();
                                                for (int i = 0; i < objectCount; i++)
                                                {
                                                    bool isPlayerObject = messageReader.ReadBoolean();
                                                    uint networkId = messageReader.ReadUInt32();
                                                    int ownerId = messageReader.ReadInt32();
                                                    int prefabId = messageReader.ReadInt32();
                                                    if(isPlayerObject)
                                                    {
                                                        SpawnManager.SpawnPlayerObject(ownerId, networkId);
                                                    }
                                                    else
                                                    {
                                                        SpawnManager.SpawnObject(prefabId, networkId, ownerId);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                break;
                            case 2:
                                //Server informs client another client connected
                                //MLAPI_ADD_OBJECT
                                if (isClient)
                                {
                                    using (MemoryStream messageReadStream = new MemoryStream(incommingData))
                                    {
                                        using (BinaryReader messageReader = new BinaryReader(messageReadStream))
                                        {
                                            if(NetworkConfig.HandleObjectSpawning)
                                            {
                                                bool isPlayerObject = messageReader.ReadBoolean();
                                                uint networkId = messageReader.ReadUInt32();
                                                int ownerId = messageReader.ReadInt32();
                                                int prefabId = messageReader.ReadInt32();
                                                if (isPlayerObject)
                                                {
                                                    connectedClients.Add(ownerId, new NetworkedClient() { ClientId = ownerId });
                                                    SpawnManager.SpawnPlayerObject(ownerId, networkId);
                                                }
                                                else
                                                {
                                                    SpawnManager.SpawnObject(prefabId, networkId, ownerId);
                                                }
                                            }
                                            else
                                            {
                                                int ownerId = messageReader.ReadInt32();
                                                connectedClients.Add(ownerId, new NetworkedClient() { ClientId = ownerId });
                                            }
                                        }
                                    }
                                }
                                break;
                            case 3:
                                //Server informs client another client disconnected
                                //MLAPI_CLIENT_DISCONNECT
                                if(isClient)
                                {
                                    using (MemoryStream messageReadStream = new MemoryStream(incommingData))
                                    {
                                        using (BinaryReader messageReader = new BinaryReader(messageReadStream))
                                        {
                                            int disconnectedClientId = messageReader.ReadInt32();
                                            OnClientDisconnect(disconnectedClientId);
                                        }
                                    }
                                }
                                break;
                            case 4:
                                //Server infroms clients to destroy an object
                                if(isClient)
                                {
                                    using (MemoryStream messageReadStream = new MemoryStream(incommingData))
                                    {
                                        using (BinaryReader messageReader = new BinaryReader(messageReadStream))
                                        {
                                            uint netId = messageReader.ReadUInt32();
                                            SpawnManager.OnDestroyObject(netId, true);
                                        }
                                    }
                                }
                                break;
                        }
                    }
                }
            }
        }

        internal void PassthroughSend(int targetId, int sourceId, ushort messageType, int channelId, byte[] data, uint? networkId = null)
        {
            if (isHost && targetId == -1)
            {
                //Host trying to send data to it's own client
                if (networkId == null)
                    MessageManager.InvokeMessageHandlers(MessageManager.reverseMessageTypes[messageType], data, sourceId);
                else
                    MessageManager.InvokeTargetedMessageHandler(MessageManager.reverseMessageTypes[messageType], data, sourceId, networkId.Value);
                return;
            }

            int sizeOfStream = 10;
            if (networkId != null)
                sizeOfStream += 4;
            sizeOfStream += data.Length;

            using (MemoryStream stream = new MemoryStream(sizeOfStream))
            {
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    writer.Write(messageType);
                    writer.Write(networkId != null);
                    if (networkId != null)
                        writer.Write(networkId.Value);
                    writer.Write(true);
                    writer.Write(sourceId);
                    writer.Write((ushort)data.Length);
                    writer.Write(data);
                }
                NetworkTransport.Send(hostId, targetId, channelId, stream.GetBuffer(), sizeOfStream, out error);
            }
        }

        internal void Send(int clientId, string messageType, string channelName, byte[] data, uint? networkId = null)
        {
            if(isHost && clientId == -1)
            {
                //Host trying to send data to it's own client
                if (networkId == null)
                    MessageManager.InvokeMessageHandlers(messageType, data, clientId);
                else
                    MessageManager.InvokeTargetedMessageHandler(messageType, data, clientId, networkId.Value);
                return;
            }
            else if(clientId == -1)
            {
                //Client trying to send data to host
                clientId = serverClientId;
            }

            bool isPassthrough = (!isServer && clientId != serverClientId && NetworkConfig.AllowPassthroughMessages);
            if (isPassthrough && !NetworkConfig.RegisteredPassthroughMessageTypes.Contains(MessageManager.messageTypes[messageType]))
            {
                Debug.LogWarning("MLAPI: The The MessageType " + messageType + " is not registered as an allowed passthrough message type.");
                return;
            }

            int sizeOfStream = 6;
            if (networkId != null)
                sizeOfStream += 4;
            if (isPassthrough)
                sizeOfStream += 4;
            sizeOfStream += data.Length;

            using (MemoryStream stream = new MemoryStream(sizeOfStream))
            {
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    writer.Write(MessageManager.messageTypes[messageType]);
                    writer.Write(networkId != null);
                    if (networkId != null)
                        writer.Write(networkId.Value);
                    writer.Write(isPassthrough);
                    if (isPassthrough)
                        writer.Write(clientId);
                    writer.Write((ushort)data.Length);
                    writer.Write(data);
                }
                if (isPassthrough)
                    clientId = serverClientId;
                NetworkTransport.Send(hostId, clientId, MessageManager.channels[channelName], stream.GetBuffer(), sizeOfStream, out error);
            }
        }

        internal void Send(int[] clientIds, string messageType, string channelName, byte[] data, uint? networkId = null)
        {
            int sizeOfStream = 6;
            if (networkId != null)
                sizeOfStream += 4;
            sizeOfStream += data.Length;

            using (MemoryStream stream = new MemoryStream(sizeOfStream))
            {
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    writer.Write(MessageManager.messageTypes[messageType]);
                    writer.Write(networkId != null);
                    if (networkId != null)
                        writer.Write(networkId.Value);
                    writer.Write(false);
                    writer.Write((ushort)data.Length);
                    writer.Write(data);
                }
                int channel = MessageManager.channels[channelName];
                for (int i = 0; i < clientIds.Length; i++)
                {
                    int clientId = clientIds[i];
                    if (isHost && clientId == -1)
                    {
                        if (networkId == null)
                            MessageManager.InvokeMessageHandlers(messageType, data, clientId);
                        else
                            MessageManager.InvokeTargetedMessageHandler(messageType, data, clientId, networkId.Value);
                        continue;
                    }
                    else if (clientId == -1)
                    {
                        //Client trying to send data to host
                        clientId = serverClientId;
                    }
                    NetworkTransport.Send(hostId, clientId, channel, stream.GetBuffer(), sizeOfStream, out error);
                }
            }
        }

        internal void Send(List<int> clientIds, string messageType, string channelName, byte[] data, uint? networkId = null)
        {
            //2 bytes for messageType, 2 bytes for buffer length and one byte for target bool
            int sizeOfStream = 6;
            if (networkId != null)
                sizeOfStream += 4;
            sizeOfStream += data.Length;

            using (MemoryStream stream = new MemoryStream(sizeOfStream))
            {
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    writer.Write(MessageManager.messageTypes[messageType]);
                    writer.Write(networkId != null);
                    if (networkId != null)
                        writer.Write(networkId.Value);
                    writer.Write(false);
                    writer.Write((ushort)data.Length);
                    writer.Write(data);
                }
                int channel = MessageManager.channels[channelName];
                for (int i = 0; i < clientIds.Count; i++)
                {
                    int clientId = clientIds[i];
                    if (isHost && clientId == -1)
                    {
                        if (networkId == null)
                            MessageManager.InvokeMessageHandlers(messageType, data, clientId);
                        else
                            MessageManager.InvokeTargetedMessageHandler(messageType, data, clientId, networkId.Value);
                        continue;
                    }
                    else if (clientId == -1)
                    {
                        //Client trying to send data to host
                        clientId = serverClientId;
                    }
                    NetworkTransport.Send(hostId, clientId, channel, stream.GetBuffer(), sizeOfStream, out error);
                }
            }
        }

        internal void Send(string messageType, string channelName, byte[] data, uint? networkId = null)
        {
            //2 bytes for messageType, 2 bytes for buffer length and one byte for target bool
            int sizeOfStream = 6;
            if (networkId != null)
                sizeOfStream += 4;
            sizeOfStream += data.Length;

            using (MemoryStream stream = new MemoryStream(sizeOfStream))
            {
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    writer.Write(MessageManager.messageTypes[messageType]);
                    writer.Write(networkId != null);
                    if (networkId != null)
                        writer.Write(networkId.Value);
                    writer.Write(false);
                    writer.Write((ushort)data.Length);
                    writer.Write(data);
                }
                int channel = MessageManager.channels[channelName];
                foreach (KeyValuePair<int, NetworkedClient> pair in connectedClients)
                {
                    int clientId = pair.Key;
                    if(isHost && pair.Key == -1)
                    {
                        if (networkId == null)
                            MessageManager.InvokeMessageHandlers(messageType, data, clientId);
                        else
                            MessageManager.InvokeTargetedMessageHandler(messageType, data, clientId, networkId.Value);
                        continue;
                    }
                    else if (clientId == -1)
                    {
                        //Client trying to send data to host
                        clientId = serverClientId;
                    }
                    NetworkTransport.Send(hostId, clientId, channel, stream.GetBuffer(), sizeOfStream, out error);
                    
                }
            }
        }

        internal void Send(string messageType, string channelName, byte[] data, int clientIdToIgnore, uint? networkId = null)
        {
            //2 bytes for messageType, 2 bytes for buffer length and one byte for target bool
            int sizeOfStream = 5;
            if (networkId != null)
                sizeOfStream += 4;
            sizeOfStream += data.Length;

            using (MemoryStream stream = new MemoryStream(sizeOfStream))
            {
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    writer.Write(MessageManager.messageTypes[messageType]);
                    writer.Write(networkId != null);
                    if (networkId != null)
                        writer.Write(networkId.Value);
                    writer.Write(false);
                    writer.Write((ushort)data.Length);
                    writer.Write(data);
                }
                int channel = MessageManager.channels[channelName];
                foreach (KeyValuePair<int, NetworkedClient> pair in connectedClients)
                {
                    if (pair.Key == clientIdToIgnore)
                        continue;
                    int clientId = pair.Key;
                    if (isHost && pair.Key == -1)
                    {
                        if (networkId == null)
                            MessageManager.InvokeMessageHandlers(messageType, data, clientId);
                        else
                            MessageManager.InvokeTargetedMessageHandler(messageType, data, clientId, networkId.Value);
                        continue;
                    }
                    else if (clientId == -1)
                    {
                        //Client trying to send data to host
                        clientId = serverClientId;
                    }
                    NetworkTransport.Send(hostId, clientId, channel, stream.GetBuffer(), sizeOfStream, out error);
                }
            }
        }

        private void DisconnectClient(int clientId)
        {
            if (!isServer)
                return;
            if (pendingClients.Contains(clientId))
                pendingClients.Remove(clientId);
            if (connectedClients.ContainsKey(clientId))
                connectedClients.Remove(clientId);
            NetworkTransport.Disconnect(hostId, clientId, out error);          
        }

        private void OnClientDisconnect(int clientId)
        {
            if (pendingClients.Contains(clientId))
                pendingClients.Remove(clientId);
            if (encryptionKeys.ContainsKey(clientId))
                encryptionKeys.Remove(clientId);
            if (server_pendingPublicKeys.ContainsKey(clientId))
                server_pendingPublicKeys.Remove(clientId);
            if (connectedClients.ContainsKey(clientId))
            {
                if(NetworkConfig.HandleObjectSpawning)
                {
                    if (connectedClients[clientId].PlayerObject != null)
                        Destroy(connectedClients[clientId].PlayerObject);
                    for (int i = 0; i < connectedClients[clientId].OwnedObjects.Count; i++)
                    {
                        if (connectedClients[clientId].OwnedObjects[i] != null)
                            Destroy(connectedClients[clientId].OwnedObjects[i].gameObject);
                    }
                }
                connectedClients.Remove(clientId);
            }

            if (isServer)
            {
                using (MemoryStream stream = new MemoryStream(4))
                {
                    using (BinaryWriter writer = new BinaryWriter(stream))
                    {
                        writer.Write(clientId);
                    }
                    Send("MLAPI_CLIENT_DISCONNECT", "MLAPI_RELIABLE_FRAGMENTED_SEQUENCED", stream.GetBuffer(), clientId);
                }   
            }
        }

        private void HandleApproval(int clientId, bool approved)
        {
            if(approved)
            {
                //Inform new client it got approved
                if (pendingClients.Contains(clientId))
                    pendingClients.Remove(clientId);
                NetworkedClient client = new NetworkedClient()
                {
                    ClientId = clientId
                };
                connectedClients.Add(clientId, client);
                
                if(NetworkConfig.HandleObjectSpawning)
                {
                    uint networkId = SpawnManager.GetNetworkObjectId();
                    GameObject go = SpawnManager.SpawnPlayerObject(clientId, networkId);
                    connectedClients[clientId].PlayerObject = go;
                }

                int amountOfObjectsToSend = 0;
                foreach (KeyValuePair<uint, NetworkedObject> pair in SpawnManager.spawnedObjects)
                {
                    if (pair.Value.ServerOnly)
                        continue;
                    else
                        amountOfObjectsToSend++;
                }

                using (MemoryStream writeStream = new MemoryStream())
                {
                    using (BinaryWriter writer = new BinaryWriter(writeStream))
                    {
                        if(NetworkConfig.EnableDiffieHellman)
                        {
                            byte[] serverPubKey = server_pendingPublicKeys[clientId].ToByteArray();
                            writer.Write((ushort)serverPubKey.Length);
                            writer.Write(serverPubKey);
                            if(NetworkConfig.DenyUntrustedKeys)
                            {
                                //NEEDS TO BE THREADED!!!
                                using (RSA rsa = RSA.Create())
                                {
                                    rsa.ImportParameters(NetworkConfig.ServerPrivateKey);
                                    byte[] signedPubKey = rsa.EncryptValue(serverPubKey);
                                    writer.Write((ushort)signedPubKey.Length);
                                    writer.Write(signedPubKey);
                                }
                            }
                        }
                        writer.Write(clientId);
                        writer.Write(connectedClients.Count - 1);
                        foreach (KeyValuePair<int, NetworkedClient> item in connectedClients)
                        {
                            //Our own ID. Already added as the first one above
                            if (item.Key == clientId)
                                continue;
                            writer.Write(item.Key); //Connection id
                        }
                        if (NetworkConfig.HandleObjectSpawning)
                        {
                            writer.Write(amountOfObjectsToSend);

                            foreach (KeyValuePair<uint, NetworkedObject> pair in SpawnManager.spawnedObjects)
                            {
                                if (pair.Value.ServerOnly)
                                    continue;
                                writer.Write(pair.Value.isPlayerObject);
                                writer.Write(pair.Value.NetworkId);
                                writer.Write(pair.Value.OwnerClientId);
                                writer.Write(pair.Value.SpawnablePrefabIndex);
                            }
                        }
                    }
                    Send(clientId, "MLAPI_CONNECTION_APPROVED", "MLAPI_RELIABLE_FRAGMENTED_SEQUENCED", writeStream.ToArray());
                }

                //Inform old clients of the new player

                int sizeOfStream = 4;
                if(NetworkConfig.HandleObjectSpawning)
                    sizeOfStream = 13;

                using (MemoryStream stream = new MemoryStream(sizeOfStream))
                {
                    using (BinaryWriter writer = new BinaryWriter(stream))
                    {
                        if (NetworkConfig.HandleObjectSpawning)
                        {
                            writer.Write(true);
                            writer.Write(connectedClients[clientId].PlayerObject.GetComponent<NetworkedObject>().NetworkId);
                            writer.Write(clientId);
                            writer.Write(-1);
                        }
                        else
                        {
                            writer.Write(clientId);
                        }
                    }
                    Send("MLAPI_ADD_OBJECT", "MLAPI_RELIABLE_FRAGMENTED_SEQUENCED", stream.GetBuffer(), clientId);
                }
            }
            else
            {
                if (pendingClients.Contains(clientId))
                    pendingClients.Remove(clientId);
                NetworkTransport.Disconnect(hostId, clientId, out error);
            }
        }
    }
}