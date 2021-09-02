using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using Mirror;

namespace VNT 
{
    //SyncData is the struct used to construct a payload to send to server/clients
    //for use with the NetworkTransform. You can amend this to suit your needs for eg
    //if you are only using delta, or some form of compression, and your payload can be
    //byte[] or anything serializable.
    //Override Construct/Deconstruct methods in VariantNetworkTransformBase to
    //populate payload or retrieve position/rotation/scale data from payload.
    //Feel free to add new data types but remember to include custom reader/writer classes.
    [Serializable]
    public struct SyncData
    {
        public Vector3? position;
        public Quaternion? rotation;
        public Vector3? scale;

        public SyncData(Vector3? position, Quaternion? rotation, Vector3? scale)
        {
            this.position = position;
            this.rotation = rotation;
            this.scale = scale;
        }        
    }

    public static class CustomReaderWriter
    {
        public static void WriteSyncData(this NetworkWriter writer, SyncData syncData)
        {
            writer.WriteVector3Nullable(syncData.position);
            writer.WriteQuaternionNullable(syncData.rotation);
            writer.WriteVector3Nullable(syncData.scale);
        }

        public static SyncData ReadSyncData(this NetworkReader reader)
        {
            return new SyncData(
                reader.ReadVector3Nullable(), 
                reader.ReadQuaternionNullable(), 
                reader.ReadVector3Nullable()
            );
        }        
    }
}
