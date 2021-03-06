using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using System;

namespace VNT
{
    // VariantNetworkTransform v1.2
    // ChangeLog:
    // 1. Split UpdateServer() and UpdaterClient() to 2 functions each:
    //    a. Sync and send data
    //    b. Render
    // 2. Added option Manual Trigger Send. If true, objects will NOT send sync data
    //    by itself. Useful if you want to trigger sending by another method or have a
    //    specific start time, or maybe after X ticks instead of time interval.
    // 3. Added option Sync Send Interval. If true object will add itself to static list
    //    and on each update only the send interval 1st object on the list will be considered,
    //    and triggered on all objects on that list.
    // 4. Changed buffer multiplier to float, allowing for more sensitive adjustment.

    // VariantNetworkTransform v1.1
    // ChangeLog:
    // 1. Removed static network writer and use Mirror's pool networkwriter instead.
    // 2. Changed ConstructSyncData() to be void, and REQUIRES the method to call
    //    SerializeAndSend<T>(T, bool) within, to send the syncdata across network.
    
    // VariantNetworkTransform v1.0
    // This is built upon NetworkTransform V2 by vis2k, not from scratch!
    // Changes and additions made to NT2:
    // 1. Made UpdateServer() and UpdateClient() virtual so it can be overriden.
    // 2. Changed Command & RPC to send ArraySegment payload instead, to make
    //    the payload customizable.
    // 3. Added virtual methods ConstructSyncData() & DeconstructSyncData()
    //    to convert custom sync data to ArraySegment and vice versa.
    // 4. Added onlySendOnMove option to not send data if object is not moving.
    //    Check comments below.
    // 5. Added a static networkwriter for NT use only.
    public abstract class VariantNetworkTransformBase : NetworkBehaviour
    {
        // NetworkTransform V2 aka project Oumuamua by vis2k (2021-07)
        // Snapshot Interpolation: https://gafferongames.com/post/snapshot_interpolation/
        //
        // Base class for NetworkTransform and NetworkTransformChild.
        // => simple unreliable sync without any interpolation for now.
        // => which means we don't need teleport detection either
        //
        // NOTE: several functions are virtual in case someone needs to modify a part.
        //
        // Channel: uses UNRELIABLE at all times.
        // -> out of order packets are dropped automatically
        // -> it's better than RELIABLE for several reasons:
        //    * head of line blocking would add delay
        //    * resending is mostly pointless
        //    * bigger data race:
        //      -> if we use a Cmd() at position X over reliable
        //      -> client gets Cmd() and X at the same time, but buffers X for bufferTime
        //      -> for unreliable, it would get X before the reliable Cmd(), still
        //         buffer for bufferTime but end up closer to the original time

        [Header("Authority")]
        [Tooltip("Set to true if moves come from owner client, set to false if moves always come from server")]
        public bool clientAuthority;

        // Is this a client with authority over this transform?
        // This component could be on the player object or any object that has been assigned authority to this client.
        protected bool IsClientWithAuthority => hasAuthority && clientAuthority;

        // target transform to sync. can be on a child.
        protected abstract Transform targetComponent { get; }

        [Header("Synchronization")]
        [Tooltip("Set to true if you do NOT want to send sync data automatically")]
        public bool manualTriggerSend = false;
        [Tooltip("Set to true to make snapshots for these objects be sent at the same time")]        
        public bool syncSendInterval = true;
        public static readonly List<VariantNetworkTransformBase> vntObjects = new List<VariantNetworkTransformBase>();
        public int vntIndex = -1; // WIP - used to identify each NT script on particular GameObject.

        [Range(0, 1)] public float sendInterval = 0.050f;
        public bool syncPosition = true;
        public bool syncRotation = true;
        // scale sync is rare. off by default.
        public bool syncScale = false;

        protected double lastClientSendTime;
        protected double lastServerSendTime;

        // not all games need to interpolate. a board game might jump to the
        // final position immediately.
        [Header("Interpolation")]
        public bool interpolatePosition = true;
        public bool interpolateRotation = true;
        public bool interpolateScale = false;

        // "Experimentally I’ve found that the amount of delay that works best
        //  at 2-5% packet loss is 3X the packet send rate"
        // NOTE: we do NOT use a dyanmically changing buffer size.
        //       it would come with a lot of complications, e.g. buffer time
        //       advantages/disadvantages for different connections.
        //       Glenn Fiedler's recommendation seems solid, and should cover
        //       the vast majority of connections.
        //       (a player with 2000ms latency will have issues no matter what)
        [Header("Buffering")]
        [Tooltip("Snapshots are buffered for sendInterval * multiplier seconds. At 2-5% packet loss, 3x supposedly works best.")]
        public float bufferTimeMultiplier = 1;
        public float bufferTime => sendInterval * bufferTimeMultiplier;
        [Tooltip("Buffer size limit to avoid ever growing list memory consumption attacks.")]
        public int bufferSizeLimit = 64;

        [Tooltip("Start to accelerate interpolation if buffer size is >= threshold. Needs to be larger than bufferTimeMultiplier.")]
        public int catchupThreshold = 4;

        [Tooltip("Once buffer is larger catchupThreshold, accelerate by multiplier % per excess entry.")]
        [Range(0, 1)] public float catchupMultiplier = 0.10f;
        
        //Adding option to allow for not sending when object is not moving.
        //This is to save bandwidth but may complicate matters if consistent
        //tick based movement/snapshots are required for client side prediction
        //and server reconcilliation. The logic to this implementation is:
        //If the client/server has not received any sync data for a period of time
        //(measured by timeMultiplier * send interval), it assumes the client has not moved
        //or (you can interprete it as) a bad enough network that the next time it receives
        //any data, it should just interpolate from the new set(s). This is done by clearing
        //all stored snapshot buffers IF enough time has passed with no new data.
        //NOTE: This is an assumption and will conflict with your game logic if your game is 
        //designed to have a very slow move, and there happened to be a bad network.
        [Header("Send Only If Moved")]
        [Tooltip("When true, data is not sent when object does not move, refer to internal comments.")]
        public bool onlySendOnMove = false;
        [Tooltip("How much time, as a multiple of send interval, has passed before clearing buffers.")]
        public float timeMultiplierToResetBuffers = 3;

        // snapshots sorted by timestamp
        // in the original article, glenn fiedler drops any snapshots older than
        // the last received snapshot.
        // -> instead, we insert into a sorted buffer
        // -> the higher the buffer information density, the better
        // -> we still drop anything older than the first element in the buffer
        // => internal for testing
        //
        // IMPORTANT: of explicit 'NTSnapshot' type instead of 'Snapshot'
        //            interface because List<interface> allocates through boxing
        public SortedList<double, NTSnapshot> serverBuffer = new SortedList<double, NTSnapshot>();
        public SortedList<double, NTSnapshot> clientBuffer = new SortedList<double, NTSnapshot>();

        // absolute interpolation time, moved along with deltaTime
        // (roughly between [0, delta] where delta is snapshot B - A timestamp)
        // (can be bigger than delta when overshooting)
        protected double serverInterpolationTime;
        protected double clientInterpolationTime;

        protected Func<NTSnapshot, NTSnapshot, double, NTSnapshot> Interpolate = NTSnapshot.Interpolate;

        // used only in onlySendOnMove mode
        protected Vector3 lastPosition;
        protected bool hasSentUnchangedPosition;

        [Header("Debug")]
        public bool showGizmos;
        public bool showOverlay;
        public Color overlayColor = new Color(0, 0, 0, 0.5f);

        // snapshot functions //////////////////////////////////////////////////
        // construct a snapshot of the current state
        // => internal for testing
        protected virtual NTSnapshot ConstructSnapshot()
        {
            // NetworkTime.localTime for double precision until Unity has it too
            return new NTSnapshot(
                // our local time is what the other end uses as remote time
                NetworkTime.localTime,
                // the other end fills out local time itself
                0,
                targetComponent.localPosition,
                targetComponent.localRotation,
                targetComponent.localScale
            );
        }

        // Serialize sync data struct and send to server or client.
        // IMPORTANT: Call this function within ConstructSyncData() when
        // overriding and pass the data struct as parameter.
        protected virtual void SerializeAndSend<T>(T syncData, bool fromServer)
        {
            using (PooledNetworkWriter writer = NetworkWriterPool.GetWriter())
            {
                writer.Write<T>(syncData);
                if (fromServer)
                {
                    RpcServerToClientSync(writer.ToArraySegment());
                }
                else
                {
                    CmdClientToServerSync(writer.ToArraySegment());
                }
            }
        }

        // Create data to send. This can be overriden and changing SyncData in 
        // case there is a different implementation like compression, etc. 
        // REMEMBER: SerializeAndSend() must be called within the override.
        protected virtual void ConstructSyncData(bool fromServer)
        {
            SyncData syncData = new SyncData(
                syncPosition ? targetComponent.localPosition : new Vector3?(),
                syncRotation ? targetComponent.localRotation : new Quaternion?(),
                syncScale ? targetComponent.localScale : new Vector3?()         
            );

            SerializeAndSend<SyncData>(syncData, fromServer);
        }

        // This is to extract position/rotation/scale data from payload. Override
        // Construct and Deconstruct if you are implementing a different SyncData logic.
        // Note however that snapshot interpolation still requires the basic 3 data
        // position, rotation and scale, which are computed from here.   
        protected virtual void DeconstructSyncData(ArraySegment<byte> receivedPayload, out Vector3? position, out Quaternion? rotation, out Vector3? scale)
        {
            using (PooledNetworkReader reader = NetworkReaderPool.GetReader(receivedPayload))
            {
                SyncData syncData = reader.Read<SyncData>();
                position = syncData.position;
                rotation = syncData.rotation;
                scale = syncData.scale;
            }
        }


        // apply a snapshot to the Transform.
        // -> start, end, interpolated are all passed in caes they are needed
        // -> a regular game would apply the 'interpolated' snapshot
        // -> a board game might want to jump to 'goal' directly
        // (it's easier to always interpolate and then apply selectively,
        //  instead of manually interpolating x, y, z, ... depending on flags)
        // => internal for testing
        //
        // NOTE: stuck detection is unnecessary here.
        //       we always set transform.position anyway, we can't get stuck.
        protected virtual void ApplySnapshot(NTSnapshot start, NTSnapshot goal, NTSnapshot interpolated)
        {
            // local position/rotation for VR support
            //
            // if syncPosition/Rotation/Scale is disabled then we received nulls
            // -> current position/rotation/scale would've been added as snapshot
            // -> we still interpolated
            // -> but simply don't apply it. if the user doesn't want to sync
            //    scale, then we should not touch scale etc.
    
            if (syncPosition)
                targetComponent.localPosition = interpolatePosition ? interpolated.position : goal.position;

            if (syncRotation)
                targetComponent.localRotation = interpolateRotation ? interpolated.rotation : goal.rotation;

            if (syncScale)
                targetComponent.localScale = interpolateScale ? interpolated.scale : goal.scale;
        }

        // cmd /////////////////////////////////////////////////////////////////
        // only unreliable. see comment above of this file.
        [Command(channel = Channels.Unreliable)]
        public virtual void CmdClientToServerSync(ArraySegment<byte> payload)
        {
            OnClientToServerSync(payload);
            //Immediately pass the sync on to other clients.
            if (clientAuthority)
            {
                RpcServerToClientSync(payload);
            }
        }

        // local authority client sends sync message to server for broadcasting
        protected virtual void OnClientToServerSync(ArraySegment<byte> receivedPayload)
        {
            // only apply if in client authority mode
            if (!clientAuthority) return;

            // protect against ever growing buffer size attacks
            if (serverBuffer.Count >= bufferSizeLimit) return;

            // only player owned objects (with a connection) can send to
            // server. we can get the timestamp from the connection.
            double timestamp = connectionToClient.remoteTimeStamp;

            if (onlySendOnMove)
            {
                double timeIntervalCheck = timeMultiplierToResetBuffers * sendInterval;

                if (serverBuffer.Count == 2 && serverBuffer.Values[1].remoteTimestamp + timeIntervalCheck < timestamp)
                {
                    Reset();
                }                
            }

            DeconstructSyncData(receivedPayload, out Vector3? position, out Quaternion? rotation, out Vector3? scale);

            // position, rotation, scale can have no value if same as last time.
            // saves bandwidth.
            // but we still need to feed it to snapshot interpolation. we can't
            // just have gaps in there if nothing has changed. for example, if
            //   client sends snapshot at t=0
            //   client sends nothing for 10s because not moved
            //   client sends snapshot at t=10
            // then the server would assume that it's one super slow move and
            // replay it for 10 seconds.
            if (!position.HasValue) position = targetComponent.localPosition;
            if (!rotation.HasValue) rotation = targetComponent.localRotation;
            if (!scale.HasValue) scale = targetComponent.localScale;

            // construct snapshot with batch timestamp to save bandwidth
            NTSnapshot snapshot = new NTSnapshot(
                timestamp,
                NetworkTime.localTime,
                position.Value, rotation.Value, scale.Value
            );

            // add to buffer (or drop if older than first element)
            SnapshotInterpolation.InsertIfNewEnough(snapshot, serverBuffer);
        }

        // rpc /////////////////////////////////////////////////////////////////
        // only unreliable. see comment above of this file.
        [ClientRpc(channel = Channels.Unreliable)] //Should this go exclude owner?
        public virtual void RpcServerToClientSync(ArraySegment<byte> payload)
        {
            OnServerToClientSync(payload);
        }

        // server broadcasts sync message to all clients
        protected virtual void OnServerToClientSync(ArraySegment<byte> receivedPayload)
        {
            // in host mode, the server sends rpcs to all clients.
            // the host client itself will receive them too.
            // -> host server is always the source of truth
            // -> we can ignore any rpc on the host client
            // => otherwise host objects would have ever growing clientBuffers
            // (rpc goes to clients. if isServer is true too then we are host)
            if (isServer) return;

            // don't apply for local player with authority
            if (IsClientWithAuthority) return;

            // protect against ever growing buffer size attacks
            if (clientBuffer.Count >= bufferSizeLimit) return;

            // on the client, we receive rpcs for all entities.
            // not all of them have a connectionToServer.
            // but all of them go through NetworkClient.connection.
            // we can get the timestamp from there.
            double timestamp = NetworkClient.connection.remoteTimeStamp;

            if (onlySendOnMove)
            {
                double timeIntervalCheck = timeMultiplierToResetBuffers * sendInterval;

                if (clientBuffer.Count == 2 && clientBuffer.Values[1].remoteTimestamp + timeIntervalCheck < timestamp)
                {
                    Reset();
                }
            }

            DeconstructSyncData(receivedPayload, out Vector3? position, out Quaternion? rotation, out Vector3? scale);        
            //? if batching has more than 1 message, does the remoteTimeStamp get a mistake?
            // position, rotation, scale can have no value if same as last time.
            // saves bandwidth.
            // but we still need to feed it to snapshot interpolation. we can't
            // just have gaps in there if nothing has changed. for example, if
            //   client sends snapshot at t=0
            //   client sends nothing for 10s because not moved
            //   client sends snapshot at t=10
            // then the server would assume that it's one super slow move and
            // replay it for 10 seconds.
            if (!position.HasValue) position = targetComponent.localPosition;
            if (!rotation.HasValue) rotation = targetComponent.localRotation;
            if (!scale.HasValue) scale = targetComponent.localScale;

            // construct snapshot with batch timestamp to save bandwidth
            NTSnapshot snapshot = new NTSnapshot(
                timestamp,
                NetworkTime.localTime,
                position.Value, rotation.Value, scale.Value
            );

            // add to buffer (or drop if older than first element)
            SnapshotInterpolation.InsertIfNewEnough(snapshot, clientBuffer);
        }

        // NT basically does 2 functions every update:
        // 1) Check interval and send data every interval
        // 2) Render data
        protected virtual void ServerSendSyncData()
        {
            //We want to send once more even if position hasn't change from last interval.
            //We do that and toggle the bool hasSentUnchangedPosition. This is so receiver has 
            //2 copies of the same position snapshot in their buffers. 
            //This ensures there is an extra (snapshot 3) snapshot that is identical to the previous one (2)
            //and thus force snapshot-interpolation to at least interpolate till the end of snapshot 2.

            if (this.transform.position == lastPosition && hasSentUnchangedPosition && onlySendOnMove) { return; }

            // send sync data without timestamp.
            // receiver gets it from batch timestamp to save bandwidth.

            ConstructSyncData(true);
            
            lastServerSendTime = NetworkTime.localTime;

            if (this.transform.position == lastPosition)
            {
                hasSentUnchangedPosition = true;
            } 
            else
            {
                hasSentUnchangedPosition = false;
                lastPosition = this.transform.position;
            }
        }

        protected virtual void ServerRenderData()
        {
            // compute snapshot interpolation & apply if any was spit out
            // TODO we don't have Time.deltaTime double yet. float is fine.
            if (SnapshotInterpolation.Compute(
                NetworkTime.localTime, Time.deltaTime,
                ref serverInterpolationTime,
                bufferTime, serverBuffer,
                catchupThreshold, catchupMultiplier,
                Interpolate,
                out NTSnapshot computed))
            {
                NTSnapshot start = serverBuffer.Values[0];
                NTSnapshot goal = serverBuffer.Values[1];
                ApplySnapshot(start, goal, (NTSnapshot)computed);
            }   
        }

        // If syncSendInterval is true, we call this method during update on the script
        // that is the first on the list of objects to sync to ensure it is only called once.
        // This loops through every object on the list to send sync data, ensuring they are
        // all called at once.
        public virtual void ServerSendSyncDataAll()
        {
            for (int i = 0, length = vntObjects.Count; i < length; i++)
            {
                if ((!vntObjects[i].clientAuthority || vntObjects[i].IsClientWithAuthority))
                {
                    vntObjects[i].ServerSendSyncData();
                }
            }
        }

        // CLIENT functions:
        // Again, update is split into 2, sending sync data and rendering.
        protected virtual void ClientSendSyncData()
        {
            // send sync data without timestamp.
            // receiver gets it from batch timestamp to save bandwidth.                 
            if (this.transform.position == lastPosition && hasSentUnchangedPosition && onlySendOnMove) { return; }  

            ConstructSyncData(false);

            lastClientSendTime = NetworkTime.localTime;
            
            if (this.transform.position == lastPosition)
            {
                hasSentUnchangedPosition = true;
            }
            else
            {
                hasSentUnchangedPosition = false;
                lastPosition = this.transform.position;
            }  
        }

        protected virtual void ClientRenderData()
        {
            // compute snapshot interpolation & apply if any was spit out
            // TODO we don't have Time.deltaTime double yet. float is fine.
            if (SnapshotInterpolation.Compute(
                NetworkTime.localTime, Time.deltaTime,
                ref clientInterpolationTime,
                bufferTime, clientBuffer,
                catchupThreshold, catchupMultiplier,
                Interpolate,
                out NTSnapshot computed))
            {
                NTSnapshot start = clientBuffer.Values[0];
                NTSnapshot goal = clientBuffer.Values[1];
                ApplySnapshot(start, goal, (NTSnapshot)computed); // Any reason why start is needed?
            }
        }

        // If syncSendInterval is true, we call this method during update on the script
        // that is the first on the list of objects to sync to ensure it is only called once.
        // This loops through every object on the list to send sync data, ensuring they are
        // all called at once.
        public virtual void ClientSendSyncDataAll()
        {
            for (int i = 0, length = vntObjects.Count; i < length; i++)
            {
                if (vntObjects[i].IsClientWithAuthority)
                {
                    vntObjects[i].ClientSendSyncData();
                }
            }            
        }

        // update //////////////////////////////////////////////////////////////
        public virtual void UpdateServer() 
        {
            // broadcast to all clients each 'sendInterval'
            // (client with authority will drop the rpc)
            // NetworkTime.localTime for double precision until Unity has it too
            //
            // IMPORTANT:
            // snapshot interpolation requires constant sending.
            // DO NOT only send if position changed. for example:
            // ---
            // * client sends first position at t=0
            // * ... 10s later ...
            // * client moves again, sends second position at t=10
            // ---
            // * server gets first position at t=0
            // * server gets second position at t=10
            // * server moves from first to second within a time of 10s
            //   => would be a super slow move, instead of a wait & move.
            //
            // IMPORTANT:
            // DO NOT send nulls if not changed 'since last send' either. we
            // send unreliable and don't know which 'last send' the other end
            // received successfully.

            if (!manualTriggerSend)
            {
                bool timeToSend = NetworkTime.localTime >= lastServerSendTime + sendInterval;
            
                if (!syncSendInterval)
                {
                    if (timeToSend && (!clientAuthority || IsClientWithAuthority)) // Server sends stuff with server Authority or Host objects with clientAuthority
                    {
                        ServerSendSyncData();
                    }
                }
                else
                {
                    if (vntObjects.Count > 0 && vntObjects[0] == this && timeToSend)   
                    {
                        ServerSendSyncDataAll();
                    }
                }
            }
            // apply buffered snapshots IF client authority
            // -> in server authority, server moves the object
            //    so no need to apply any snapshots there.
            // -> don't apply for host mode player either, even if in
            //    client authority mode. if it doesn't go over the network,
            //    then we don't need to do anything.
            if (clientAuthority && !hasAuthority)  
            {
                ServerRenderData();        
            }
        }

        public virtual void UpdateClient() 
        {
            // See UpdateServer() comments
        

            // send to server each 'sendInterval'
            // NetworkTime.localTime for double precision until Unity has it too
            //
            // IMPORTANT:
            // snapshot interpolation requires constant sending.
            // DO NOT only send if position changed. for example:
            // ---
            // * client sends first position at t=0
            // * ... 10s later ...
            // * client moves again, sends second position at t=10
            // ---
            // * server gets first position at t=0
            // * server gets second position at t=10
            // * server moves from first to second within a time of 10s
            //   => would be a super slow move, instead of a wait & move.
            //
            // IMPORTANT:
            // DO NOT send nulls if not changed 'since last send' either. we
            // send unreliable and don't know which 'last send' the other end
            // received successfully.
            if (!manualTriggerSend)
            {
                bool timeToSend = NetworkTime.localTime >= lastClientSendTime + sendInterval;
            
                if (!syncSendInterval)
                {
                    if (timeToSend && IsClientWithAuthority)
                    {
                        ClientSendSyncData();
                    }
                }
                else
                {
                    if (vntObjects.Count > 0 && vntObjects[0] == this && timeToSend)   
                    {
                        ClientSendSyncDataAll();
                    }
                }
            } 

     
            // for all other clients (and for local player if !authority),
            // we need to apply snapshots from the buffer
            if (!IsClientWithAuthority)
            {
                ClientRenderData();
            }
        }

        void Update() 
        {
            // if server then always sync to others.
            if (isServer) UpdateServer();
            // 'else if' because host mode shouldn't send anything to server.
            // it is the server. don't overwrite anything there.
            else if (isClient) UpdateClient(); 
        }

        // common Teleport code for client->server and server->client
        protected virtual void OnTeleport(Vector3 destination)
        {
            // reset any in-progress interpolation & buffers
            Reset();

            // set the new position.
            // interpolation will automatically continue.
            targetComponent.position = destination;

            // TODO
            // what if we still receive a snapshot from before the interpolation?
            // it could easily happen over unreliable.
            // -> maybe add destionation as first entry?
        }

        // server->client teleport to force position without interpolation.
        // otherwise it would interpolate to a (far away) new position.
        // => manually calling Teleport is the only 100% reliable solution.
        [ClientRpc]
        public void RpcTeleport(Vector3 destination)
        {
            // NOTE: even in client authority mode, the server is always allowed
            //       to teleport the player. for example:
            //       * CmdEnterPortal() might teleport the player
            //       * Some people use client authority with server sided checks
            //         so the server should be able to reset position if needed.

            // TODO what about host mode?
            OnTeleport(destination);
        }

        // client->server teleport to force position without interpolation.
        // otherwise it would interpolate to a (far away) new position.
        // => manually calling Teleport is the only 100% reliable solution.
        [Command]
        public void CmdTeleport(Vector3 destination)
        {
            // client can only teleport objects that it has authority over.
            if (!clientAuthority) return;

            // TODO what about host mode?
            OnTeleport(destination);

            // if a client teleports, we need to broadcast to everyone else too
            // TODO the teleported client should ignore the rpc though.
            //      otherwise if it already moved again after teleporting,
            //      the rpc would come a little bit later and reset it once.
            // TODO or not? if client ONLY calls Teleport(pos), the position
            //      would only be set after the rpc. unless the client calls
            //      BOTH Teleport(pos) and targetComponent.position=pos
            RpcTeleport(destination);
        }

        protected virtual void Reset()
        {
            // disabled objects aren't updated anymore.
            // so let's clear the buffers.
            serverBuffer.Clear();
            clientBuffer.Clear();

            // reset interpolation time too so we start at t=0 next time
            serverInterpolationTime = 0;
            clientInterpolationTime = 0;
        }

        public void AddEntity(VariantNetworkTransformBase entity)
        {
            if (!vntObjects.Contains(entity))
            {
                vntObjects.Add(entity);
            }
        }

        public void RemoveEntity(VariantNetworkTransformBase entity)
        {
            if (vntObjects.Contains(entity))
            {
                vntObjects.Remove(entity);
            }
        }

        protected virtual void OnEnable() 
        {
            Reset();
            vntIndex = Array.IndexOf(GetComponents(typeof(VariantNetworkTransformBase)), this);

            if (syncSendInterval)    
            {
                AddEntity(this);
            }
        }

        protected virtual void OnDisable()
        {
            Reset();
            if (syncSendInterval)    
            {
                RemoveEntity(this);
            }            
        }

        protected virtual void OnValidate()
        {
            // make sure that catchup threshold is > buffer multiplier.
            // for a buffer multiplier of '3', we usually have at _least_ 3
            // buffered snapshots. often 4-5 even.
            catchupThreshold = Mathf.Max(Mathf.CeilToInt(bufferTimeMultiplier) + 3, catchupThreshold);

            // buffer limit should be at least multiplier to have enough in there
            bufferSizeLimit = Mathf.Max(Mathf.CeilToInt(bufferTimeMultiplier), bufferSizeLimit);
        }

        // debug ///////////////////////////////////////////////////////////////
        protected virtual void OnGUI()
        {
            if (!showOverlay) return;

            // show data next to player for easier debugging. this is very useful!
            // IMPORTANT: this is basically an ESP hack for shooter games.
            //            DO NOT make this available with a hotkey in release builds
            if (!Debug.isDebugBuild) return;

            // project position to screen
            Vector3 point = Camera.main.WorldToScreenPoint(targetComponent.position);

            // enough alpha, in front of camera and in screen?
            if (point.z >= 0 && Utils.IsPointInScreen(point))
            {
                // catchup is useful to show too
                int serverBufferExcess = Mathf.Max(serverBuffer.Count - catchupThreshold, 0);
                int clientBufferExcess = Mathf.Max(clientBuffer.Count - catchupThreshold, 0);
                float serverCatchup = serverBufferExcess * catchupMultiplier;
                float clientCatchup = clientBufferExcess * catchupMultiplier;

                GUI.color = overlayColor;
                GUILayout.BeginArea(new Rect(point.x, Screen.height - point.y, 200, 100));

                // always show both client & server buffers so it's super
                // obvious if we accidentally populate both.
                GUILayout.Label($"Server Buffer:{serverBuffer.Count}");
                if (serverCatchup > 0)
                    GUILayout.Label($"Server Catchup:{serverCatchup * 100:F2}%");

                GUILayout.Label($"Client Buffer:{clientBuffer.Count}");
                if (clientCatchup > 0)
                    GUILayout.Label($"Client Catchup:{clientCatchup * 100:F2}%");

                GUILayout.EndArea();
                GUI.color = Color.white;
            }
        }

        protected virtual void DrawGizmos(SortedList<double, NTSnapshot> buffer)
        {
            // only draw if we have at least two entries
            if (buffer.Count < 2) return;

            // calcluate threshold for 'old enough' snapshots
            double threshold = NetworkTime.localTime - bufferTime;
            Color oldEnoughColor = new Color(0, 1, 0, 0.5f);
            Color notOldEnoughColor = new Color(0.5f, 0.5f, 0.5f, 0.3f);

            // draw the whole buffer for easier debugging.
            // it's worth seeing how much we have buffered ahead already
            for (int i = 0; i < buffer.Count; ++i)
            {
                // color depends on if old enough or not
                NTSnapshot entry = buffer.Values[i];
                bool oldEnough = entry.localTimestamp <= threshold;
                Gizmos.color = oldEnough ? oldEnoughColor : notOldEnoughColor;
                Gizmos.DrawCube(entry.position, Vector3.one);
            }

            // extra: lines between start<->position<->goal
            Gizmos.color = Color.green;
            Gizmos.DrawLine(buffer.Values[0].position, targetComponent.position);
            Gizmos.color = Color.white;
            Gizmos.DrawLine(targetComponent.position, buffer.Values[1].position);
        }

        protected virtual void OnDrawGizmos()
        {
            if (!showGizmos) return;

            if (isServer) DrawGizmos(serverBuffer);
            if (isClient) DrawGizmos(clientBuffer);
        }

    }
}
